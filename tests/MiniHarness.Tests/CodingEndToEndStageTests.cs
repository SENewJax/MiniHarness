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
    // Layer-1 deterministic acceptance: a scripted decision model drives the REAL
    // runtime, REAL workspace files and REAL test processes. The model's edit text
    // is scripted, but every hash, exit code and revision comes from the real system.
    internal static class CodingEndToEndStageTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v1-e2e] failing first fix is corrected from real test output then task completes", FailThenFixScenario));
            tests.Add(("[v1-e2e] claiming completion before current evidence is rejected with the gap", EarlyCompletionScenario));
            tests.Add(("[v1-e2e] adding a regression test creates a discovered test that exposes the bug", RegressionTestScenario));
        }

        private static string Exe => Environment.ProcessPath!;

        private sealed record Project(string Dir, string Root, string Store, string Artifacts, string ValueFile);

        private static Project CreateProject(string initialValue)
        {
            string dir = Path.Combine(Path.GetTempPath(), "miniharness-v1-e2e-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(dir, "sample-dotnet-project");
            string store = Path.Combine(dir, "checkpoints");
            string artifacts = Path.Combine(dir, "artifacts");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "tests"));
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(root, "value.txt"), initialValue);
            File.WriteAllText(Path.Combine(root, "tests", "expect.tst"), "value = 42");
            return new Project(dir, root, store, artifacts, Path.Combine(root, "value.txt"));
        }

        private static VerificationProfile UnitTestProfile() => new VerificationProfile
        {
            Name = "unit_tests", FileName = Exe, Arguments = new List<string> { "--self", "project-tests" },
            TimeoutSeconds = 60, MinimumTests = 1, TestCountRegex = @"Tests run:\s*(\d+)"
        };

        private static ITool[] CodingTools(ChangeLedger ledger, VerificationTool verification, WorkspaceOptions options) => new ITool[]
        {
            new ListFilesTool(options), new SearchTextTool(options), new ReadFileTool(options),
            new CreateFileTool(ledger, options), new ApplyPatchTool(ledger, options), new GetChangesTool(ledger, options), verification
        };

        private static string[] ToolNames(ITool[] tools) => tools.Select(t => t.Definition.Name).ToArray();

        // Scripted agent driven by the latest real tool observation. It deliberately
        // makes a wrong first fix, reads the real failing test output, then corrects
        // it. It also honors V0's edit-invalidation rule: a verification operation
        // proposed in the pre-edit step is rejected, so the agent re-issues it on a
        // fresh step before trusting the result.
        private sealed class RepairAgent : IModelProvider
        {
            public List<ModelRequest> Inputs { get; } = new List<ModelRequest>();
            // Observation-driven state machine. The wrong-fix turn BATCHES
            // apply_patch + run_verification in a single response: they share one
            // pre-edit capability snapshot, so after the edit lands, V0's rule
            // (Operations.cs invalidation) rejects the queued verification and the
            // agent must re-issue it on a fresh step — the exact spec C/G link.
            private enum Phase { Read, PatchWrong, VerifyWrongRejected, WrongResult, PatchFix, IssueVerifyFix, FixResult, Done }
            private Phase phase = Phase.Read;
            private int seenToolMessages;
            private int calls;

            public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Inputs.Add(request);
                calls++;

                var toolMessages = request.Messages.Where(m => m.Role == "tool").ToList();
                var fresh = toolMessages.Skip(seenToolMessages).Select(InnerOutput).ToList();
                seenToolMessages = toolMessages.Count;
                string joined = string.Join("\n", fresh);

                switch (phase)
                {
                    case Phase.Read:
                        phase = Phase.PatchWrong;
                        return Task.FromResult(Call("read_file", "{\"path\":\"value.txt\"}", "call_r1"));

                    case Phase.PatchWrong:
                    {
                        string hash = ParseHash(joined);
                        phase = Phase.VerifyWrongRejected;
                        // Batch the edit and its verification under one snapshot.
                        return Task.FromResult(Multi(
                            ("apply_patch", PatchArgs("value.txt", hash, "value = 1", "value = 99"), "call_wrong"),
                            ("run_verification", "{\"profile\":\"unit_tests\"}", "call_v1_stale")));
                    }

                    case Phase.VerifyWrongRejected:
                        // The edit is confirmed applied AND the co-batched verify is
                        // rejected as invalidated; re-issue verification fresh.
                        if (!joined.Contains("\"status\":\"modified\"", StringComparison.Ordinal))
                            throw new Exception("Expected the wrong edit to be applied: " + joined);
                        if (!joined.Contains("Operation invalidated", StringComparison.Ordinal))
                            throw new Exception("Expected the co-batched verification to be invalidated: " + joined);
                        phase = Phase.WrongResult;
                        return Task.FromResult(Call("run_verification", "{\"profile\":\"unit_tests\"}", "call_v1_retry"));

                    case Phase.WrongResult:
                    {
                        string? tail = fresh.Select(o => Field(o, "stderrTail")).FirstOrDefault(v => v != null);
                        if (!joined.Contains("\"exitCode\":1", StringComparison.Ordinal) ||
                            tail == null || !tail.Contains("got 'value = 99'", StringComparison.Ordinal))
                            throw new Exception("Agent did not receive the real failing test output: " + joined);
                        phase = Phase.PatchFix;
                        return Task.FromResult(Call("read_file", "{\"path\":\"value.txt\"}", "call_r2"));
                    }

                    case Phase.PatchFix:
                        phase = Phase.IssueVerifyFix;
                        return Task.FromResult(Call("apply_patch", PatchArgs("value.txt", ParseHash(joined), "value = 99", "value = 42"), "call_fix"));

                    case Phase.IssueVerifyFix:
                        if (!joined.Contains("\"status\":\"modified\"", StringComparison.Ordinal))
                            throw new Exception("Expected the fix edit to be applied: " + joined);
                        phase = Phase.FixResult;
                        return Task.FromResult(Call("run_verification", "{\"profile\":\"unit_tests\"}", "call_v2"));

                    case Phase.FixResult:
                        // The first post-edit verification is invalidated because the
                        // edit's revision is observed only at the next loop's refresh;
                        // re-issue once on the now-fresh snapshot.
                        if (joined.Contains("Operation invalidated", StringComparison.Ordinal))
                            return Task.FromResult(Call("run_verification", "{\"profile\":\"unit_tests\"}", "call_v2_retry"));
                        if (!joined.Contains("\"exitCode\":0", StringComparison.Ordinal))
                            throw new Exception("Expected final verification to pass: " + joined);
                        phase = Phase.Done;
                        return Task.FromResult(new ModelResponse
                        {
                            Message = new Message { Role = "assistant", Text = "Fixed value.txt to 42; current unit_tests evidence passes." },
                            Finish = ModelFinish.Stop,
                            Usage = new TokenUsage { InputTokens = 5, OutputTokens = 5 }
                        });

                    default:
                        throw new Exception("Agent produced an extra turn after finish, calls=" + calls);
                }
            }

            private static ModelResponse Multi(params (string Name, string Args, string Id)[] calls)
            {
                var response = new ModelResponse
                {
                    Message = new Message { Role = "assistant", Text = "" },
                    Finish = ModelFinish.ToolCalls,
                    Usage = new TokenUsage { InputTokens = 5, OutputTokens = 5 }
                };
                foreach (var c in calls)
                    response.Message.ToolCalls.Add(new ToolCall { Id = c.Id, Name = c.Name, Arguments = c.Args });
                return response;
            }

            // The harness stores a tool observation as ToolResult serialized with
            // DEFAULT options: PascalCase keys, Status as an enum number, and the
            // tool's own (camelCase) JSON carried as an escaped string in Output.
            private static string InnerOutput(Message toolMessage)
            {
                using var outer = JsonDocument.Parse(toolMessage.Text);
                return outer.RootElement.GetProperty("Output").GetString() ?? "";
            }

            private static string ParseHash(string output)
            {
                using var doc = JsonDocument.Parse(output);
                return doc.RootElement.GetProperty("hash").GetString()!;
            }

            // Structured field read: GetString decodes \uXXXX escapes, so assertions
            // can match the real text regardless of JSON escaping.
            private static string? Field(string json, string name)
            {
                try { using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null; }
                catch (JsonException) { return null; }
            }

            private static string PatchArgs(string path, string hash, string oldText, string newText) =>
                JsonSerializer.Serialize(new { path, expected_hash = hash, old_text = oldText, new_text = newText });

            private static ModelResponse Call(string name, string args, string id)
            {
                var response = new ModelResponse
                {
                    Message = new Message { Role = "assistant", Text = "" },
                    Finish = ModelFinish.ToolCalls,
                    Usage = new TokenUsage { InputTokens = 5, OutputTokens = 5 }
                };
                response.Message.ToolCalls.Add(new ToolCall { Id = id, Name = name, Arguments = args });
                return response;
            }
        }

        private static TaskRuntime BuildRuntime(Project project, IModelProvider model, ITool[] tools, IWorkspaceRevisionProvider revision)
        {
            var store = new FileTaskStore(project.Store);
            var options = new HarnessOptions { MaxModelCalls = 20, MaxToolCalls = 30, ToolTimeoutSeconds = 5 };
            var binding = new ModelBinding { Model = "test", Protocol = "Responses", ConfigurationVersion = "v1" };
            var policy = new LegacyToolPolicyAdapter(new AllowListToolPolicy(ToolNames(tools)));
            return new TaskRuntime(model, store, options, binding, tools: new ToolRegistry(tools),
                policy: policy, completion: new CodingCompletionPolicy(), workspaceRevision: revision);
        }

        private static async Task FailThenFixScenario()
        {
            var project = CreateProject("value = 1");
            var ledger = new ChangeLedger(Path.Combine(project.Artifacts, "changes"));
            var revision = new DirectoryWorkspaceRevision();
            var verification = new VerificationTool(new[] { UnitTestProfile() }, revision, project.Artifacts);
            var tools = CodingTools(ledger, verification, new WorkspaceOptions());
            var agent = new RepairAgent();

            await using var runtime = BuildRuntime(project, agent, tools, revision);
            AgentTaskState final;
            try
            {
                var command = new TaskCommand
                {
                    Kind = TaskCommandKind.Start,
                    Request = HarnessTests.Request(),
                    Facts = new TaskFacts
                    {
                        Goal = "Fix the project so the value satisfies its test.",
                        Workspace = new WorkspaceScope { RootPath = project.Root, Grants = { WorkspaceGrants.Read, WorkspaceGrants.Write, WorkspaceGrants.Execute } },
                        RequiredEvidence = { VerificationTool.VerificationRequirement("unit_tests") }
                    }
                };
                var result = await runtime.ExecuteAsync(command, CancellationToken.None);
                final = result.State;

                Check.Equal(AgentTaskStatus.Completed, final.Status);
                Check.Equal("value = 42", File.ReadAllText(project.ValueFile));
                Check.True(final.Budget.ToolCalls == 6, "expected 6 tool calls, got " + final.Budget.ToolCalls);

                var passed = final.Evidence.Where(e => e.Passed).ToList();
                var failed = final.Evidence.Where(e => !e.Passed).ToList();
                Check.True(failed.Count == 1, "one failing verification must be recorded");
                Check.True(passed.Count == 1, "one passing verification must be recorded");
                Check.Equal(1, failed[0].ExitCode);
                Check.Equal(0, passed[0].ExitCode);
                Check.Equal(final.Facts.Workspace.Revision, passed[0].WorkspaceRevision);
                Check.True(File.Exists(passed[0].Artifacts[0].Location), "passing log artifact must exist");

                // No temp file left behind by the atomic-replace write path.
                Check.True(!Directory.EnumerateFiles(project.Root, "*.mh-tmp-*", SearchOption.AllDirectories).Any());

                // get_changes ledger attributes only the agent file, not the pre-existing test.
                var changes = await new GetChangesTool(ledger).ExecuteAsync(
                    JsonDocument.Parse("{}").RootElement, ToolContext(final, "op_getchanges"), default);
                using var cdoc = JsonDocument.Parse(changes.Output);
                var paths = cdoc.RootElement.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString()).ToList();
                Check.True(paths.Count == 1 && paths[0] == "value.txt", "only value.txt is agent-owned");
            }
            finally { if (Directory.Exists(project.Dir)) Directory.Delete(project.Dir, true); }
        }

        private static ToolExecutionContext ToolContext(AgentTaskState state, string operationId) => new ToolExecutionContext
        {
            TaskId = state.Id, TurnId = state.Turns[0].Id, StepId = "step", OperationId = operationId,
            Workspace = state.Facts.Workspace, Capabilities = new CapabilitySnapshot(), Approval = ApprovalState.Approved
        };

        private sealed class StopImmediatelyAgent : IModelProvider
        {
            public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
            {
                bool hasGapFeedback = request.Messages.Any(m => m.Role == "system" &&
                    m.Text.Contains("missing passing evidence", StringComparison.OrdinalIgnoreCase));
                if (hasGapFeedback)
                    return Task.FromResult(new ModelResponse
                    {
                        Message = new Message { Role = "assistant", Text = "still cannot finish" },
                        Finish = ModelFinish.Stop,
                        Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1 }
                    });
                return Task.FromResult(new ModelResponse
                {
                    Message = new Message { Role = "assistant", Text = "All done, tests pass." },
                    Finish = ModelFinish.Stop,
                    Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1 }
                });
            }
        }

        private static async Task EarlyCompletionScenario()
        {
            var project = CreateProject("value = 1");
            var ledger = new ChangeLedger(Path.Combine(project.Artifacts, "changes"));
            var revision = new DirectoryWorkspaceRevision();
            var verification = new VerificationTool(new[] { UnitTestProfile() }, revision, project.Artifacts);
            var tools = CodingTools(ledger, verification, new WorkspaceOptions());

            await using var runtime = BuildRuntime(project, new StopImmediatelyAgent(), tools, revision);
            try
            {
                var result = await runtime.ExecuteAsync(new TaskCommand
                {
                    Kind = TaskCommandKind.Start,
                    Request = HarnessTests.Request(),
                    Facts = new TaskFacts
                    {
                        Goal = "Fix without actually running anything.",
                        Workspace = new WorkspaceScope { RootPath = project.Root, Grants = { WorkspaceGrants.Read, WorkspaceGrants.Write, WorkspaceGrants.Execute } },
                        RequiredEvidence = { VerificationTool.VerificationRequirement("unit_tests") }
                    }
                }, CancellationToken.None);

                var state = result.State;
                Check.True(state.Status != AgentTaskStatus.Completed, "a bare claim must not complete the task");
                // Gate keeps returning CanContinue with the gap; the loop only stops
                // when the model-call budget is reached.
                Check.True(state.Events.Any(e => e.Type == "CompletionFeedback"), "gap must be fed back as an event");
                Check.True(state.Budget.ModelCalls >= 2, "the rejected completion must have been retried: " + state.Budget.ModelCalls);
                Check.Equal(0, state.Budget.ToolCalls);
                Check.True(state.History.Any(m => m.Message.Role == "system" && m.Message.Text.Contains("unit_tests", StringComparison.Ordinal)));
                Check.True(state.Wait == WaitReason.Evidence || state.Wait == WaitReason.Budget, "ends waiting, not completed");
            }
            finally { if (Directory.Exists(project.Dir)) Directory.Delete(project.Dir, true); }
        }

        private static async Task RegressionTestScenario()
        {
            // Drive the discovery/failure portion through the real tool directly to
            // prove a newly created test file is actually found and fails, then turns
            // green after the fix — no agent scripting beyond that core assertion.
            var project = CreateProject("value = 1");
            var ledger = new ChangeLedger(Path.Combine(project.Artifacts, "changes"));
            var revision = new DirectoryWorkspaceRevision();
            var verification = new VerificationTool(new[] { UnitTestProfile() }, revision, project.Artifacts);
            var context = new ToolExecutionContext
            {
                TaskId = "task_reg", TurnId = "t", StepId = "s", OperationId = "op_reg",
                Workspace = new WorkspaceScope { RootPath = project.Root, Grants = { WorkspaceGrants.Read, WorkspaceGrants.Write, WorkspaceGrants.Execute } },
                Capabilities = new CapabilitySnapshot(), Approval = ApprovalState.Approved
            };
            try
            {
                var create = await new CreateFileTool(ledger).ExecuteAsync(
                    JsonDocument.Parse(JsonSerializer.Serialize(new { path = "tests/regression.tst", content = "value = 42" })).RootElement, context, default);
                Check.Equal(ToolCallStatus.Succeeded, create.Status);

                var failing = await verification.ExecuteAsync(
                    JsonDocument.Parse("{\"profile\":\"unit_tests\"}").RootElement, context, default);
                Check.True(!failing.Evidence[0].Passed, "new regression must fail against value=1");
                Check.Equal(1, failing.Evidence[0].ExitCode);
                // Failure detail lives in the captured stderr tail and the log artifact.
                using var fdoc = JsonDocument.Parse(failing.Output);
                string stderr = fdoc.RootElement.GetProperty("stderrTail").GetString() ?? "";
                Check.True(stderr.Contains("regression.tst", StringComparison.Ordinal), stderr);
                Check.True(File.ReadAllText(failing.Evidence[0].Artifacts[0].Location).Contains("regression.tst", StringComparison.Ordinal));

                File.WriteAllText(project.ValueFile, "value = 42");
                var passing = await verification.ExecuteAsync(
                    JsonDocument.Parse("{\"profile\":\"unit_tests\"}").RootElement, context, default);
                Check.True(passing.Evidence[0].Passed, "after the fix both tests pass");
                Check.Equal(0, passing.Evidence[0].ExitCode);
            }
            finally { if (Directory.Exists(project.Dir)) Directory.Delete(project.Dir, true); }
        }
    }
}
