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
    /// deadlock on a full pipe. The configured timeout bounds BOTH phases of the
    /// lifecycle: waiting for the launcher to exit AND waiting for the output
    /// pipes to reach EOF. A launcher that exits early while a descendant keeps
    /// the pipes open no longer hangs the drain (and is never reported Exited/0):
    /// the whole job tree is reclaimed and the run is TimedOut, or
    /// KillUnconfirmed when termination cannot be confirmed. Both a timeout and a
    /// user cancellation kill the whole process tree and wait for confirmation.
    /// </summary>
    public sealed class ProcessRunner
    {
        // After the launcher exits, drains are given at most this long (and never
        // past the overall deadline) to reach EOF. A healthy tree closes its
        // inherited handles within milliseconds; anything longer means a
        // descendant is keeping a pipe open on purpose.
        private const int MaxDrainGraceMs = 2_000;
        private const int MinDrainGraceMs = 50;
        private static readonly TimeSpan KillConfirm = TimeSpan.FromSeconds(5);

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
            // The job owns the whole tree even after the launcher exits. On hosts
            // where it cannot be attached we still bound every wait and degrade to
            // KillUnconfirmed instead of trusting an unconfirmable tree.
            using (var job = ProcessJob.TryCreateAndAssign(process))
            {
                var stdout = new LimitedBuffer(spec.MaxOutputBytes);
                var stderr = new LimitedBuffer(spec.MaxOutputBytes);
                using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var stdoutTask = Task.Run(() => Drain(process.StandardOutput, stdout, drainCts.Token));
                var stderrTask = Task.Run(() => Drain(process.StandardError, stderr, drainCts.Token));
                var drainFinish = Task.WhenAll(stdoutTask, stderrTask);

                int timeoutSeconds = Math.Max(1, spec.TimeoutSeconds);
                var overallDeadline = started.AddSeconds(timeoutSeconds);

                bool interrupted = false;
                bool timedOut = false;
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
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
                    // Launcher was still alive at the deadline / on cancellation.
                    bool killed = await ReclaimTreeAsync(process, job, orphanSuspect: false).ConfigureAwait(false);
                    await StopDrainAsync(process, drainCts, drainFinish).ConfigureAwait(false);

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

                // Launcher exited before the deadline. The timeout must still bound
                // the drain: a descendant that inherited our pipe handles keeps EOF
                // from ever arriving. Wait out only the remaining budget.
                int graceMs = (int)Math.Clamp((overallDeadline - DateTimeOffset.UtcNow).TotalMilliseconds,
                    MinDrainGraceMs, MaxDrainGraceMs);
                bool drainsDone = await WaitDrainAsync(drainFinish, TimeSpan.FromMilliseconds(graceMs), cancellationToken).ConfigureAwait(false);

                if (drainsDone && !cancellationToken.IsCancellationRequested)
                {
                    drainCts.Cancel();
                    ObserveDrain(drainFinish);
                    return new ProcessRunResult
                    {
                        State = ProcessOutcomeState.Exited,
                        ExitCode = process.HasExited ? process.ExitCode : null,
                        StandardOutput = stdout.Text, StandardError = stderr.Text,
                        OutputTruncated = stdout.Truncated || stderr.Truncated, ElapsedMilliseconds = Elapsed(started)
                    };
                }

                // A descendant still holds a pipe open (or the user cancelled during
                // drain): force our readers shut, reclaim the entire tree, and never
                // report a clean Exited/0 for an execution that did not actually end.
                await StopDrainAsync(process, drainCts, drainFinish).ConfigureAwait(false);
                // The launcher has already exited here; without a job any surviving
                // descendant is unenumerable, so reclaim must be treated as suspect.
                bool reclaimed = await ReclaimTreeAsync(process, job, orphanSuspect: true).ConfigureAwait(false);
                int elapsedAfter = Elapsed(started);

                if (cancellationToken.IsCancellationRequested)
                {
                    if (!reclaimed)
                        return new ProcessRunResult
                        {
                            State = ProcessOutcomeState.KillUnconfirmed,
                            StandardOutput = stdout.Text, StandardError = stderr.Text,
                            OutputTruncated = stdout.Truncated || stderr.Truncated, ElapsedMilliseconds = elapsedAfter
                        };
                    throw new OperationCanceledException("Process was cancelled after its surviving child process tree was terminated.", cancellationToken);
                }

                if (!reclaimed)
                    return new ProcessRunResult
                    {
                        State = ProcessOutcomeState.KillUnconfirmed,
                        StandardOutput = stdout.Text, StandardError = stderr.Text,
                        OutputTruncated = stdout.Truncated || stderr.Truncated, ElapsedMilliseconds = elapsedAfter
                    };

                string orphanNote = "[process runner] launcher exited, but a descendant kept an output pipe open past the deadline; the surviving process tree was terminated.\n";
                return new ProcessRunResult
                {
                    State = ProcessOutcomeState.TimedOut,
                    StandardOutput = stdout.Text, StandardError = orphanNote + stderr.Text,
                    OutputTruncated = stdout.Truncated || stderr.Truncated, ElapsedMilliseconds = elapsedAfter
                };
            }
        }

        // True when both drain tasks finish within the grace window. A user cancel
        // also releases the wait (drains are linked to that token).
        private static async Task<bool> WaitDrainAsync(Task drainFinish, TimeSpan grace, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), tcs))
            {
                Task winner = await Task.WhenAny(drainFinish, Task.Delay(grace), tcs.Task).ConfigureAwait(false);
                return ReferenceEquals(winner, drainFinish) && drainFinish.IsCompleted;
            }
        }

        // Hard ceiling used once the tree is being (or has been) reclaimed: cancel
        // the reads, force the pipes shut, and give readers a brief bounded chance
        // to unwind. They are never awaited past this; faults are observed only.
        private static async Task StopDrainAsync(System.Diagnostics.Process process,
            CancellationTokenSource drainCts, Task drainFinish)
        {
            drainCts.Cancel();
            if (drainFinish.IsCompleted) { ObserveDrain(drainFinish); return; }
            var deadline = Task.Delay(TimeSpan.FromSeconds(2));
            if (await Task.WhenAny(drainFinish, deadline).ConfigureAwait(false) == drainFinish) { ObserveDrain(drainFinish); return; }
            ForcePipesClosed(process);
            ObserveDrain(drainFinish);
        }

        // Kill the launcher tree and confirm the WHOLE tree is gone. Process.Kill
        // walks descendants of a live root; the job additionally reclaims children
        // orphaned by an early launcher exit, and its membership list confirms the
        // tree is empty rather than only that the parent handle exited. When the
        // launcher has already exited and no job is attached, surviving descendants
        // are unenumerable: such a reclaim cannot be confirmed.
        private static async Task<bool> ReclaimTreeAsync(System.Diagnostics.Process process, ProcessJob? job, bool orphanSuspect)
        {
            bool parentConfirmed;
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                // Process may already have exited; fall through to confirm.
            }
            try
            {
                using var deadline = new CancellationTokenSource(KillConfirm);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                parentConfirmed = process.HasExited;
            }
            catch (OperationCanceledException) { parentConfirmed = false; }

            if (job is not null && job.IsValid)
            {
                bool treeConfirmed = await job.TerminateAndWaitEmptyAsync(KillConfirm).ConfigureAwait(false);
                return parentConfirmed && treeConfirmed;
            }

            // No job (non-Windows or assignment refused). A still-live parent has
            // its tree reaped by Process.Kill; a parent that already exited cannot
            // have its orphaned descendants enumerated, so do not claim success.
            if (orphanSuspect) return false;
            return parentConfirmed;
        }

        // Background observation so an unwinding drain task can never raise on the
        // finalizer; it does not delay reporting under any circumstance.
        private static void ObserveDrain(Task drainFinish)
        {
            _ = drainFinish.ContinueWith(t => { var observed = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        // Hard-stop the redirected readers. Called only during reclamation once a
        // surviving descendant is known to be holding a pipe: closing the streams
        // aborts a stuck ReadAsync so reporting is not held hostage.
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
            catch (ObjectDisposedException) { }
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
