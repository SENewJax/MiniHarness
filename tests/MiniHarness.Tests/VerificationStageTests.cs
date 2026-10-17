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
