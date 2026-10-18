using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHarness.Coding
{
    public enum ProcessOutcomeState { Exited, TimedOut, StartFailed, KillUnconfirmed }

    public sealed class ProcessRunResult
    {
        public ProcessOutcomeState State { get; init; }
        public int? ExitCode { get; init; }
        public string StandardOutput { get; init; } = "";
        public string StandardError { get; init; } = "";
        public bool OutputTruncated { get; init; }
        public int ElapsedMilliseconds { get; init; }
        public string? LogPath { get; set; }
        public string? LogHash { get; set; }
    }

    public sealed class ProcessSpecification
    {
        public string FileName { get; init; } = "";
        public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
        public string WorkingDirectory { get; init; } = "";
        public int TimeoutSeconds { get; init; } = 120;
        public int MaxOutputBytes { get; init; } = 64_000;
        public int MaxLogBytes { get; init; } = 1_000_000;
    }

    /// <summary>
    /// One-shot, argument-based process execution. No shell, no interaction.
    /// stdout/stderr are drained concurrently so a chatty test run cannot
    /// deadlock on a full pipe. Both a timeout AND a user cancellation kill the
    /// whole process tree and wait for exit confirmation before reporting;
    /// failure to confirm termination is reported (KillUnconfirmed), never hidden.
    /// </summary>
    public sealed class ProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessSpecification spec, CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            var psi = new ProcessStartInfo
            {
                FileName = spec.FileName,
                WorkingDirectory = spec.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in spec.Arguments) psi.ArgumentList.Add(arg);

            System.Diagnostics.Process? process;
            try { process = System.Diagnostics.Process.Start(psi); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is FileNotFoundException || ex is ObjectDisposedException)
            {
                return new ProcessRunResult { State = ProcessOutcomeState.StartFailed, StandardError = ex.Message, ElapsedMilliseconds = Elapsed(started) };
            }
            if (process == null)
                return new ProcessRunResult { State = ProcessOutcomeState.StartFailed, StandardError = "Process could not be started." };

            using (process)
            {
                var stdout = new LimitedBuffer(spec.MaxOutputBytes);
                var stderr = new LimitedBuffer(spec.MaxOutputBytes);
                var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var stdoutTask = Task.Run(() => Drain(process.StandardOutput, stdout, drainCts.Token));
                var stderrTask = Task.Run(() => Drain(process.StandardError, stderr, drainCts.Token));

                bool interrupted = false;
                bool timedOut = false;
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, spec.TimeoutSeconds)));
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                try
                {
                    await process.WaitForExitAsync(combined.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Both a user cancellation and our own timeout interrupt the wait.
                    // The process is reclaimed below in either case; distinguishing why
                    // only changes how the outcome is reported afterwards.
                    interrupted = true;
                    timedOut = !cancellationToken.IsCancellationRequested;
                }

                if (interrupted)
                {
                    // Bounded termination + exit confirmation is shared by timeout and
                    // user cancellation so an aborted build/test cannot keep running
                    // (or mutating files) after the task reports it has stopped.
                    bool killed = await TryKillTreeAsync(process).ConfigureAwait(false);

                    // Stop the pipe readers, then wait only briefly: a dead child must
                    // not hang cancellation forever, but we still collect what drained.
                    drainCts.Cancel();
                    var drainFinish = Task.WhenAll(stdoutTask, stderrTask);
                    var drainDeadline = Task.Delay(TimeSpan.FromSeconds(2));
                    if (await Task.WhenAny(drainFinish, drainDeadline).ConfigureAwait(false) != drainFinish)
                    {
                        // The 2s ceiling is a hard bound, not a hint. The tree is
                        // confirmed dead, so force the pipes shut (aborting any stuck
                        // read) and observe the drain tasks in the background. They are
                        // never awaited again: a wedged reader cannot delay reporting.
                        ForcePipesClosed(process);
                        _ = drainFinish.ContinueWith(t => { var observed = t.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    }

                    int elapsed = Elapsed(started);
                    if (!killed)
                    {
                        // Termination could not be confirmed: surface uncertain side
                        // effects instead of throwing, so the runtime enters
                        // reconciliation rather than assuming the tree is gone.
                        return new ProcessRunResult
                        {
                            State = ProcessOutcomeState.KillUnconfirmed,
                            StandardOutput = stdout.Text, StandardError = stderr.Text,
                            OutputTruncated = stdout.Truncated || stderr.Truncated, ElapsedMilliseconds = elapsed
                        };
                    }

                    // The tree is confirmed gone. A timeout is a structured result; a
                    // user cancellation propagates the OCE after reclamation succeeds.
                    if (cancellationToken.IsCancellationRequested && !timedOut)
                        throw new OperationCanceledException("Process was cancelled after its process tree was terminated.", cancellationToken);

                    return new ProcessRunResult
                    {
                        State = ProcessOutcomeState.TimedOut,
                        StandardOutput = stdout.Text, StandardError = stderr.Text,
                        OutputTruncated = stdout.Truncated || stderr.Truncated, ElapsedMilliseconds = elapsed
                    };
                }

                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                drainCts.Cancel();
                return new ProcessRunResult
                {
                    State = ProcessOutcomeState.Exited,
                    ExitCode = process.HasExited ? process.ExitCode : null,
                    StandardOutput = stdout.Text, StandardError = stderr.Text,
                    OutputTruncated = stdout.Truncated || stderr.Truncated, ElapsedMilliseconds = Elapsed(started)
                };
            }
        }

        // Returns true when the tree is confirmed gone.
        private static async Task<bool> TryKillTreeAsync(System.Diagnostics.Process process)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                // Process may already have exited; fall through to confirm.
            }
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return process.HasExited;
            }
            catch (OperationCanceledException) { return false; }
        }

        // Hard-stop the redirected readers. Called only after kill confirmation
        // and the drain deadline, when the tree is already dead: closing the
        // streams aborts a stuck ReadAsync so reporting is not held hostage.
        private static void ForcePipesClosed(System.Diagnostics.Process process)
        {
            try { process.StandardOutput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
            try { process.StandardError.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
        }

        private static async Task Drain(StreamReader reader, LimitedBuffer sink, CancellationToken cancellationToken)
        {
            var chunk = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                    sink.Append(chunk, read);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }

        private static int Elapsed(DateTimeOffset start) => (int)Math.Max(0, (DateTimeOffset.UtcNow - start).TotalMilliseconds);

        private sealed class LimitedBuffer
        {
            private readonly int max;
            private readonly StringBuilder builder = new StringBuilder();
            public bool Truncated { get; private set; }
            public string Text => builder.ToString();
            public LimitedBuffer(int max) { this.max = max; }
            public void Append(char[] chunk, int count)
            {
                if (builder.Length >= max) { Truncated = true; return; }
                int room = max - builder.Length;
                int take = Math.Min(room, count);
                builder.Append(chunk, 0, take);
                if (take < count) Truncated = true;
            }
        }
    }
}
