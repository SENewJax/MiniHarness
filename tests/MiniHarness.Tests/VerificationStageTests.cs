using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Coding;
using MiniHarness.Harness;

namespace MiniHarness.Tests
{
    internal static class VerificationStageTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v1-process] exit code, chatter drain, stderr and start failure are distinguished", ProcessScenario));
            tests.Add(("[v1-process] timeout kills the one-shot process and reports it", TimeoutScenario));
            tests.Add(("[v1-process] user cancellation reclaims the process and its child tree", CancellationScenario));
            tests.Add(("[v1-verify] profile pass/fail evidence is bound to the real exit code and revision", VerificationScenario));
            tests.Add(("[v1-verify] zero-test and missing marker do not produce passing evidence", ZeroTestScenario));
            tests.Add(("[v1-verify] a change during the run invalidates passing evidence", ChangeDuringRunScenario));
        }

        private static string Exe => Environment.ProcessPath!;
        private static ProcessSpecification Spec(params string[] args) => new ProcessSpecification
        {
            FileName = Exe,
            Arguments = args,
            WorkingDirectory = Path.GetTempPath(),
            TimeoutSeconds = 30,
            MaxOutputBytes = 200_000
        };

        private static async Task ProcessScenario()
        {
            var runner = new ProcessRunner();

            var zero = await runner.RunAsync(Spec("--self", "exit", "0"), default);
            Check.Equal(ProcessOutcomeState.Exited, zero.State);
            Check.Equal(0, zero.ExitCode);

            var seven = await runner.RunAsync(Spec("--self", "exit", "7"), default);
            Check.Equal(7, seven.ExitCode);

            var err = await runner.RunAsync(Spec("--self", "echoerr", "boom"), default);
            Check.Equal(3, err.ExitCode);
            Check.True(err.StandardError.Contains("boom", StringComparison.Ordinal));

            // Heavy concurrent stdout/stderr must not deadlock the pipe drains.
            var chatter = await runner.RunAsync(Spec("--self", "chatter", "2000"), default);
            Check.Equal(ProcessOutcomeState.Exited, chatter.State);
            Check.True(chatter.StandardOutput.Contains("line 1999", StringComparison.Ordinal));

            var bad = await runner.RunAsync(new ProcessSpecification { FileName = "definitely-not-a-real-executable-404.exe", WorkingDirectory = Path.GetTempPath() }, default);
            Check.Equal(ProcessOutcomeState.StartFailed, bad.State);
        }

        private static async Task TimeoutScenario()
        {
            var runner = new ProcessRunner();
            var spec = new ProcessSpecification { FileName = Exe, Arguments = new[] { "--self", "sleep", "30" }, WorkingDirectory = Path.GetTempPath(), TimeoutSeconds = 2 };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await runner.RunAsync(spec, default);
            watch.Stop();
            Check.Equal(ProcessOutcomeState.TimedOut, result.State);
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(12), "timeout must terminate promptly: " + watch.Elapsed);
        }

        private static async Task CancellationScenario()
        {
            string dir = Path.Combine(Path.GetTempPath(), "miniharness-v1-cancel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            int parentPid = 0, childPid = 0;
            try
            {
                var runner = new ProcessRunner();

                // (1) A single long-running child: after cancellation the real OS
                // process must be reclaimed, not just the managed Process released.
                string pidFile = Path.Combine(dir, "single.pid");
                var cts1 = new CancellationTokenSource();
                var run1 = Task.Run(() => runner.RunAsync(new ProcessSpecification
                {
                    FileName = Exe, Arguments = new[] { "--self", "sleep-pid", pidFile, "30" },
                    WorkingDirectory = Path.GetTempPath(), TimeoutSeconds = 60
                }, cts1.Token));
                await WaitForFileAsync(pidFile);
                int singlePid = int.Parse(await File.ReadAllTextAsync(pidFile));
                Check.True(IsAlive(singlePid), "child must be running before cancellation");
                cts1.Cancel();
                await ExpectCancellationAsync(run1);
                Check.True(await WaitDeadAsync(singlePid, TimeSpan.FromSeconds(8)),
                    "cancelled child process must be terminated");

                // (2) A parent that spawns a grandchild: tree kill must reclaim both,
                // proving the grandchild is not orphaned by the cancellation path.
                string childPidFile = Path.Combine(dir, "tree-child.pid");
                string parentPidFile = childPidFile + ".parent";
                var cts2 = new CancellationTokenSource();
                var run2 = Task.Run(() => runner.RunAsync(new ProcessSpecification
                {
                    FileName = Exe, Arguments = new[] { "--self", "spawn-sleep", childPidFile, "30" },
                    WorkingDirectory = Path.GetTempPath(), TimeoutSeconds = 60
                }, cts2.Token));
                await WaitForFileAsync(childPidFile);
                await WaitForFileAsync(parentPidFile);
                childPid = int.Parse(await File.ReadAllTextAsync(childPidFile));
                parentPid = int.Parse(await File.ReadAllTextAsync(parentPidFile));
                Check.True(IsAlive(parentPid) && IsAlive(childPid), "parent and grandchild must be running");
                cts2.Cancel();
                await ExpectCancellationAsync(run2);
                Check.True(await WaitDeadAsync(parentPid, TimeSpan.FromSeconds(8)), "cancelled parent must be terminated");
                Check.True(await WaitDeadAsync(childPid, TimeSpan.FromSeconds(8)),
                    "grandchild must be terminated with the process tree");
                childPid = parentPid = 0;
            }
            finally
            {
                // Safety net so a failed assertion never leaves sleeping test processes.
                foreach (int pid in new[] { parentPid, childPid })
                {
                    if (pid > 0) { try { System.Diagnostics.Process.GetProcessById(pid).Kill(true); } catch { } }
                }
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        private static async Task ExpectCancellationAsync(Task run)
        {
            try { await run; }
            catch (OperationCanceledException) { return; }
            throw new Exception("Cancellation was expected to propagate after the process tree was reclaimed.");
        }

        private static async Task WaitForFileAsync(string path)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !File.Exists(path)) await Task.Delay(25);
            if (!File.Exists(path)) throw new Exception("Timed out waiting for pid file: " + path);
        }

        private static bool IsAlive(int pid)
        {
            try { var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static async Task<bool> WaitDeadAsync(int pid, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsAlive(pid)) return true;
                await Task.Delay(50);
            }
            return !IsAlive(pid);
        }

        private sealed record Area(string Dir, string Root, string Artifacts);

        private static Area SetupArea()
        {
            string dir = Path.Combine(Path.GetTempPath(), "miniharness-v1-verify-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(dir, "workspace");
            string artifacts = Path.Combine(dir, "artifacts");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "marker.cs"), "class Marker {}");
            return new Area(dir, root, artifacts);
        }

        private static ToolExecutionContext Context(string root) => new ToolExecutionContext
        {
            TaskId = "task_verify", TurnId = "turn_1", StepId = "step_1", OperationId = "op_1",
            Workspace = new WorkspaceScope { RootPath = root, Grants = { WorkspaceGrants.Read, WorkspaceGrants.Execute } },
            Capabilities = new CapabilitySnapshot(), Approval = ApprovalState.Approved
        };

        private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

        private static VerificationProfile Profile(string name, string mode, string code, int minTests = 0, string? countRegex = null) =>
            new VerificationProfile
            {
                Name = name, FileName = Exe, Arguments = new List<string> { "--self", mode, code },
                TimeoutSeconds = 30, MinimumTests = minTests, TestCountRegex = countRegex
            };

        private static async Task VerificationScenario()
        {
            var area = SetupArea();
            try
            {
                var revision = new DirectoryWorkspaceRevision();
                var tool = new VerificationTool(new[] { Profile("unit_tests", "exit", "0") }, revision, area.Artifacts);
                var outcome = await tool.ExecuteAsync(Args("{\"profile\":\"unit_tests\"}"), Context(area.Root), default);
                Check.Equal(ToolCallStatus.Succeeded, outcome.Status);
                Check.Equal(1, outcome.Evidence.Count);
                Check.True(outcome.Evidence[0].Passed);
                Check.Equal(0, outcome.Evidence[0].ExitCode);
                Check.Equal("verification:unit_tests", outcome.Evidence[0].Requirement);
                var current = await revision.GetRevisionAsync(new WorkspaceScope { RootPath = area.Root }, default);
                Check.Equal(current, outcome.Evidence[0].WorkspaceRevision);
                Check.True(File.Exists(outcome.Evidence[0].Artifacts[0].Location));

                var failing = new VerificationTool(new[] { Profile("unit_tests", "exit", "1") }, revision, area.Artifacts);
                var bad = await failing.ExecuteAsync(Args("{\"profile\":\"unit_tests\"}"), Context(area.Root), default);
                Check.Equal(ToolCallStatus.Succeeded, bad.Status);
                Check.True(!bad.Evidence[0].Passed, "non-zero exit is a model-visible failure, not a thrown exception");
                Check.Equal(1, bad.Evidence[0].ExitCode);

                var unknown = await new VerificationTool(new[] { Profile("unit_tests", "exit", "0") }, revision, area.Artifacts)
                    .ExecuteAsync(Args("{\"profile\":\"nope\"}"), Context(area.Root), default);
                Check.Equal(ToolCallStatus.Failed, unknown.Status);
            }
            finally { if (Directory.Exists(area.Dir)) Directory.Delete(area.Dir, true); }
        }

        private static async Task ZeroTestScenario()
        {
            var area = SetupArea();
            try
            {
                var revision = new DirectoryWorkspaceRevision();
                // Profile requires >=1 discovered test; the child reports zero and exits 0.
                var zero = new VerificationTool(new[]
                {
                    new VerificationProfile { Name = "unit_tests", FileName = Exe, Arguments = new List<string> { "--self", "tests", "0" },
                        TimeoutSeconds = 30, MinimumTests = 1, TestCountRegex = @"Tests run:\s*(\d+)" }
                }, revision, area.Artifacts);
                var outcome = await zero.ExecuteAsync(Args("{\"profile\":\"unit_tests\"}"), Context(area.Root), default);
                Check.True(!outcome.Evidence[0].Passed);
                Check.True(outcome.Evidence[0].Output.Contains("at least 1", StringComparison.Ordinal));

                var one = new VerificationTool(new[]
                {
                    new VerificationProfile { Name = "unit_tests", FileName = Exe, Arguments = new List<string> { "--self", "tests", "1" },
                        TimeoutSeconds = 30, MinimumTests = 1, TestCountRegex = @"Tests run:\s*(\d+)" }
                }, revision, area.Artifacts);
                var good = await one.ExecuteAsync(Args("{\"profile\":\"unit_tests\"}"), Context(area.Root), default);
                Check.True(good.Evidence[0].Passed);
            }
            finally { if (Directory.Exists(area.Dir)) Directory.Delete(area.Dir, true); }
        }

        private static async Task ChangeDuringRunScenario()
        {
            var area = SetupArea();
            try
            {
                var revision = new DirectoryWorkspaceRevision();
                string target = Path.Combine(area.Root, "marker.cs");
                var tool = new VerificationTool(new[]
                {
                    new VerificationProfile { Name = "unit_tests", FileName = Exe,
                        Arguments = new List<string> { "--self", "touch", target }, TimeoutSeconds = 30 }
                }, revision, area.Artifacts);
                var outcome = await tool.ExecuteAsync(Args("{\"profile\":\"unit_tests\"}"), Context(area.Root), default);
                Check.Equal(ToolCallStatus.Succeeded, outcome.Status);
                Check.True(!outcome.Evidence[0].Passed, "a run that mutates the workspace must not certify the revision");
                Check.True(outcome.Evidence[0].Output.Contains("changed while verification", StringComparison.Ordinal));
            }
            finally { if (Directory.Exists(area.Dir)) Directory.Delete(area.Dir, true); }
        }
    }
}
