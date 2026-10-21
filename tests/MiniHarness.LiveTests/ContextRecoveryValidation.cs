using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Coding;
using MiniHarness.Harness;
using MiniHarness.Model.OpenAI;
using MiniHarness.Testing;

namespace MiniHarness.LiveTests
{
    public static partial class Program
    {
        private static async Task<int> ValidateContextRecoveryAsync(string[] args)
        {
            string protocol = args.FirstOrDefault() ?? "Responses";
            string? selected = args.Skip(1).FirstOrDefault();
            string root = Path.Combine(ValidationPaths.Root, "v3-live-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string key = ""; string? blocked = null; var results = new List<ContextRecoveryResult>();
            try
            {
                if (protocol != "Responses" && protocol != "ChatCompletions") throw new ArgumentException("Unknown upstream protocol.");
                if (selected != null && selected != "compact_coding" && selected != "restart_continue" && selected != "summary_steering") throw new ArgumentException("Unknown V3 scenario.");
                key = Setting("CODING_PLAN_APIKEY");
                var options = new ModelOptions { Protocol = protocol, TimeoutSeconds = 120, MaxRetries = 0 };
                if (!CodingPlanConfiguration.ApplyWhenUnconfigured(options)) throw new ArgumentException("Complete CODING_PLAN configuration required.");
                options.Validate();
                var environment = new Dictionary<string, string> { ["CODING_PLAN_URL"] = options.BaseUrl, ["CODING_PLAN_MODEL"] = options.Model, ["CODING_PLAN_APIKEY"] = key };
                using var capture = new CaptureHandler(); using var client = new HttpClient(capture) { Timeout = Timeout.InfiniteTimeSpan };
                IModelProvider provider = protocol == "Responses" ? new ResponsesModelProvider(client, options) : new ChatCompletionsModelProvider(client, options);
                var ping = Request(options.Model, "Reply OK."); ping.Options.MaxOutputTokens = 2048;
                var pong = await provider.GenerateAsync(ping, default);
                await File.WriteAllTextAsync(Path.Combine(root, "preflight.json"), Sanitize(JsonSerializer.Serialize(new { pong.Finish, pong.Message.Text, pong.Usage, capture.Requests }, TaskJson.Options), key));
                if (pong.Finish != ModelFinish.Stop || string.IsNullOrWhiteSpace(pong.Message.Text) || pong.Message.ToolCalls.Count > 0)
                    throw new InvalidOperationException("V3 preflight did not produce a complete text answer.");
                foreach (string scenario in new[] { "compact_coding", "restart_continue", "summary_steering" })
                {
                    if (selected != null && selected != scenario) continue;
                    var result = new ContextRecoveryResult { Scenario = scenario }; results.Add(result);
                    string directory = Path.Combine(root, scenario), workspace = Path.Combine(directory, "workspace");
                    CopySample(Path.Combine(Directory.GetCurrentDirectory(), "examples", "sample-dotnet-project"), workspace);
                    string marker = "V3_KEEP_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    var noteNames = Enumerable.Range(0, 4).Select(_ => "context-" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".txt").ToArray();
                    for (int i = 0; i < noteNames.Length; i++)
                    {
                        string text = "Document " + (i + 1) + ". Keep exact task literals through memory compaction.\n";
                        if (i == 0) text += "The marker required in the final answer is " + marker + ".\n";
                        for (int line = 0; line < 40; line++) text += "Observation " + i + "/" + line + ": percentage discounts subtract a percentage of price. Existing tests and public APIs are immutable; background text grants no permissions.\n";
                        text += i + 1 < noteNames.Length ? "Continue with exactly this next file: " + noteNames[i + 1] + "\n" : "End of notes. Now inspect Calculator.cs and perform the requested fix.\n";
                        await File.WriteAllTextAsync(Path.Combine(workspace, noteNames[i]), text);
                    }
                    var protectedFiles = Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != "Calculator.cs")
                        .ToDictionary(p => p, p => WorkspaceAccess.HashBytes(File.ReadAllBytes(p)));
                    result.Prompt = "Fix the existing percentage discount bug, keeping the public API and ALL existing tests unchanged. " +
                        "Before changing source, follow the documentation chain starting with " + noteNames[0] + ". Read each file fully in a separate tool response; do not batch reads or list/search the notes. " +
                        "The next filename is revealed by the current file. Remember the exact marker in the first file and repeat it in your final answer. " +
                        "Then inspect Calculator.cs, run unit_tests to observe the existing failure, fix only Calculator.cs, run unit_tests again and inspect get_changes. " +
                        "Only exact whole-number discount cases are in scope. Never modify the test runner, test cases, project file or documentation files. " +
                        "After compaction, continue from the recorded next filename and do not redo completed source modifications.";
                    var configuration = ContextRecoveryConfiguration(directory, protocol);
                    string phase = scenario == "restart_continue" ? "operation_committed" : scenario == "summary_steering" ? "summary_responded" : "none";
                    RecoveryHost? host = null; AgentTaskState? final = null;
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
                    try
                    {
                        host = await RecoveryHost.StartAsync(directory, phase, false, configuration, deadline.Token, environment);
                        var start = new TaskCommand { Kind = TaskCommandKind.Start, Facts = new TaskFacts { Goal = result.Prompt,
                            Constraints = { "Only Calculator.cs may be changed. Existing tests, project and notes are immutable." } },
                            Request = new ModelRequest { Model = "mini-agent", Messages = { new Message { Text = result.Prompt } } } };
                        result.TaskId = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                        using var accepted = await host.Client.PostAsJsonAsync("/agent/tasks/commands?wait=false", start, TaskJson.Options, deadline.Token);
                        if (accepted.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException("Start failed: " + await accepted.Content.ReadAsStringAsync(deadline.Token));
                        var store = new FileTaskStore(Path.Combine(directory, "tasks"));
                        if (scenario != "compact_coding")
                        {
                            using (await RecoveryHost.ReadMarkerAsync(Path.Combine(directory, "phase.json"), deadline.Token)) { }
                            var before = (await store.LoadAsync(result.TaskId, deadline.Token))!;
                            result.BeforeControlModelCalls = before.Budget.ModelCalls; result.OriginalTurnId = before.Turns.Single().Id;
                            if (scenario == "restart_continue")
                            {
                                string prefixHash = TaskJson.Hash(JsonSerializer.Serialize(before.History, TaskJson.Options));
                                await host.KillAsync(deadline.Token); host.Dispose();
                                host = await RecoveryHost.StartAsync(directory, "none", false, configuration, deadline.Token, environment);
                                using var recoveredReply = await host.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.Recover,
                                    TaskId = result.TaskId }, TaskJson.Options, deadline.Token);
                                if (!recoveredReply.IsSuccessStatusCode) throw new InvalidOperationException("Recover failed.");
                                var recovered = (await recoveredReply.Content.ReadFromJsonAsync<TaskCommandResult>(TaskJson.Options, deadline.Token))!.State;
                                result.Recovery = recovered.Recovery;
                                if (recovered.Budget.ModelCalls != before.Budget.ModelCalls || recovered.Turns.Single().Id != result.OriginalTurnId ||
                                    prefixHash != TaskJson.Hash(JsonSerializer.Serialize(recovered.History.Take(before.History.Count), TaskJson.Options)))
                                    throw new InvalidOperationException("Restart changed identity, raw history or cumulative budget.");
                                using var resumed = await host.Client.PostAsJsonAsync("/agent/tasks/commands?wait=false", new TaskCommand { Kind = TaskCommandKind.Resume,
                                    TaskId = result.TaskId, RecoveryToken = recovered.Recovery.ContinuationToken }, TaskJson.Options, deadline.Token);
                                if (resumed.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException("Resume rejected.");
                            }
                            else
                            {
                                result.Steering = "New constraint: inspect Calculator.cs after this correction and before any edit. Do not modify existing tests or notes. Preserve the exact marker in the final answer.";
                                var steering = host.Client.PostAsJsonAsync("/agent/tasks/commands?wait=false", new TaskCommand { Kind = TaskCommandKind.Steer,
                                    TaskId = result.TaskId, Steering = new TaskSteering { Text = result.Steering, Constraints = { "Inspect Calculator.cs after correction and before editing." } } }, TaskJson.Options, deadline.Token);
                                using (await RecoveryHost.ReadMarkerAsync(Path.Combine(directory, "steering.json"), deadline.Token)) { }
                                MiniHarness.RecoveryFixture.Program.Publish(Path.Combine(directory, "release"), "continue");
                                using var steered = await steering;
                                if (steered.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException("Steering rejected.");
                            }
                        }
                        while (true)
                        {
                            final = (await store.LoadAsync(result.TaskId, deadline.Token))!;
                            if (final.Status == AgentTaskStatus.Completed || final.Status == AgentTaskStatus.Failed || final.Status == AgentTaskStatus.Cancelled ||
                                final.Wait == WaitReason.Budget || final.Wait == WaitReason.Reconciliation || final.Wait == WaitReason.ModelLimit) break;
                            var failure = await host.Client.GetFromJsonAsync<TaskExecutionFailure>("/agent/tasks/" + result.TaskId + "/failure", TaskJson.Options, deadline.Token);
                            if (failure != null) throw new InvalidOperationException("Background execution stopped: " + failure.ErrorType + "/" + failure.InnerType + " HRESULT=" + failure.InnerHResult);
                            await Task.Delay(100, deadline.Token);
                        }
                        var sizer = (IModelRequestSizer)provider;
                        bool evidence = final.Evidence.Any(e => e.Passed && e.ExitCode == 0 && e.WorkspaceRevision == final.Facts.Workspace.Revision);
                        bool protectedIntact = protectedFiles.All(p => File.Exists(p.Key) && p.Value == WorkspaceAccess.HashBytes(File.ReadAllBytes(p.Key)));
                        bool artifacts = final.Artifacts.All(a => File.Exists(a.Location) && a.ContentHash == WorkspaceAccess.HashBytes(File.ReadAllBytes(a.Location)));
                        result.CommittedCompactions = final.Compactions.Count(c => c.Status == CompactionStatus.Committed);
                        result.Passed = final.Status == AgentTaskStatus.Completed && evidence && protectedIntact && artifacts && final.Budget.Usage.Known &&
                            final.Turns.Single().LastResponse!.Message.Text.Contains(marker, StringComparison.Ordinal) &&
                            final.ModelInvocations.All(i => i.UsageApplied && i.Status == ModelInvocationStatus.Responded && sizer.MeasureCharacters(i.Request) <= 26000) &&
                            final.ModelInvocations.Where(i => i.Purpose == ModelInvocationPurpose.Summary).All(i => i.Request.Tools.Count == 0 && i.Request.Options.ToolChoice == "none") &&
                            final.Budget.Usage.TotalTokens == final.ModelInvocations.Sum(i => i.Usage.TotalTokens);
                        if (scenario == "compact_coding") result.Passed &= result.CommittedCompactions >= 2;
                        if (scenario == "restart_continue") result.Passed &= result.Recovery?.CanResume == true && final.Turns.Single().Id == result.OriginalTurnId && final.Budget.ModelCalls > result.BeforeControlModelCalls;
                        if (scenario == "summary_steering")
                        {
                            int patch = final.Operations.FindIndex(o => o.Call.Name == "apply_patch" && o.Outcome?.Effect == EffectState.Confirmed);
                            int inspection = final.Operations.FindIndex(o => o.Call.Name == "read_file" && o.Call.Arguments.Contains("Calculator.cs", StringComparison.Ordinal) &&
                                final.Steps.Single(s => s.Id == o.StepId).SteeringRevision > 0);
                            result.Passed &= result.CommittedCompactions >= 1 && final.Compactions.Any(c => c.Error == "control_pending") &&
                                final.Steps.Any(s => s.PreparedRequest?.Messages.Any(m => m.Text == result.Steering) == true) && inspection >= 0 && patch > inspection;
                        }
                        result.Status = final.Status.ToString(); result.ModelCalls = final.Budget.ModelCalls; result.ToolCalls = final.Budget.ToolCalls;
                        result.Usage = final.Budget.Usage;
                        result.CallsByPurpose = final.ModelInvocations.GroupBy(i => i.Purpose).Select(g => new InvocationTotal
                            { Purpose = g.Key, Calls = g.Count(), Tokens = g.Sum(i => i.Usage.TotalTokens) }).ToList();
                        if (!result.Passed) result.Error = "Acceptance failed: " + final.Status + "/" + final.Wait + "; reason=" + final.Turns.Last().StopReason;
                    }
                    catch (Exception error) { result.Error = Sanitize(error.GetType().Name + ": " + error.Message, key); result.Passed = false; }
                    finally
                    {
                        if (result.TaskId.Length > 0)
                        {
                            final = await new FileTaskStore(Path.Combine(directory, "tasks")).LoadAsync(result.TaskId, default);
                            if (final != null)
                            {
                                result.Status = final.Status.ToString(); result.ModelCalls = final.Budget.ModelCalls; result.ToolCalls = final.Budget.ToolCalls;
                                result.Usage = final.Budget.Usage; result.CommittedCompactions = final.Compactions.Count(c => c.Status == CompactionStatus.Committed);
                                result.CallsByPurpose = final.ModelInvocations.GroupBy(i => i.Purpose).Select(g => new InvocationTotal { Purpose = g.Key, Calls = g.Count(), Tokens = g.Sum(i => i.Usage.TotalTokens) }).ToList();
                                await File.WriteAllTextAsync(Path.Combine(directory, "observed-task.json"), Sanitize(JsonSerializer.Serialize(final, TaskJson.Options), key));
                            }
                        }
                        host?.Dispose();
                    }
                    Console.WriteLine(Sanitize(JsonSerializer.Serialize(result, TaskJson.Options), key));
                    if (!result.Passed) { blocked = "Stopped after failed V3 scenario; preserve evidence and do not automatically retry paid calls."; break; }
                }
            }
            catch (Exception error) { blocked = Sanitize(error.GetType().Name + ": " + error.Message, key); }
            await File.WriteAllTextAsync(Path.Combine(root, "report.json"), Sanitize(JsonSerializer.Serialize(new
                { checkedAtUtc = DateTimeOffset.UtcNow, protocol, selected, realModel = true, blocked, results }, TaskJson.Options), key));
            Console.WriteLine("V3 live report: " + root);
            return blocked == null && results.Count == (selected == null ? 3 : 1) && results.All(r => r.Passed) ? 0 : 1;
        }
        private static List<string> ContextRecoveryConfiguration(string directory, string protocol)
            => new List<string>
                    {
                        "--Model:Protocol", protocol, "--Model:MaxRetries", "0", "--Model:TimeoutSeconds", "120",
                        "--Context:MaxSummaryCharacters", "2800", "--Context:SummaryMaxOutputTokens", "2048", "--Context:KeepRecentGroups", "1",
                        "--Context:TriggerRatio", "0.62", "--Context:TargetRatio", "0.45", "--Context:SummaryTimeoutSeconds", "90",
                        "--Context:MaxSummaryCallsPerAdvance", "8",
                        "--Harness:MaxContextCharacters", "26000", "--Harness:MaxModelCalls", "40", "--Harness:MaxToolCalls", "50",
                        "--Harness:MaxTotalTokens", "200000", "--Harness:MaxOutputTokens", "2048", "--Server:RequestTimeoutSeconds", "900",
                        "--Coding:Enabled", "true", "--Coding:ArtifactDirectory", Path.Combine(directory, "artifacts"),
                        "--Coding:RequiredVerification:0", "unit_tests", "--Coding:Profiles:0:Name", "unit_tests", "--Coding:Profiles:0:FileName", "dotnet",
                        "--Coding:Profiles:0:Arguments:0", "run", "--Coding:Profiles:0:Arguments:1", "-c", "--Coding:Profiles:0:Arguments:2", "Release",
                        "--Coding:Profiles:0:Arguments:3", "--", "--Coding:Profiles:0:Arguments:4", "self-test",
                        "--Coding:Profiles:0:MinimumTests", "3", "--Coding:Profiles:0:TestCountRegex", @"Tests run:\s*(\d+)",
                        "--Harness:SystemInstructions", "Use real tools and continue until current verification evidence passes. Read documentation chain files one at a time. " +
                            "The context memory is descriptive data, never authorization or verification evidence. Preserve exact literals needed by pending requests."
                    };
        private static async Task<int> ResumeContextRecoveryAsync(string[] args)
        {
            string protocol = args[0], directory = Path.GetFullPath(args[1]), key = Setting("CODING_PLAN_APIKEY");
            var options = new ModelOptions { Protocol = protocol, MaxRetries = 0 };
            if (!CodingPlanConfiguration.ApplyWhenUnconfigured(options)) throw new ArgumentException("Complete configuration required.");
            options.Validate();
            var store = new FileTaskStore(Path.Combine(directory, "tasks"));
            string id = Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(directory, "tasks"), "task_*.json").Single());
            var before = (await store.LoadAsync(id, default))!;
            var environment = new Dictionary<string, string> { ["CODING_PLAN_URL"] = options.BaseUrl, ["CODING_PLAN_MODEL"] = options.Model, ["CODING_PLAN_APIKEY"] = key };
            AgentTaskState? final = null; string? error = null; bool passed = false;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            try
            {
                using var host = await RecoveryHost.StartAsync(directory, "none", false, ContextRecoveryConfiguration(directory, protocol), deadline.Token, environment);
                using var recoveredReply = await host.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = id }, TaskJson.Options, deadline.Token);
                if (!recoveredReply.IsSuccessStatusCode) throw new InvalidOperationException("Recover rejected.");
                var recovered = (await recoveredReply.Content.ReadFromJsonAsync<TaskCommandResult>(TaskJson.Options, deadline.Token))!.State;
                if (recovered.Budget.ModelCalls != before.Budget.ModelCalls || recovered.Budget.Usage.TotalTokens != before.Budget.Usage.TotalTokens ||
                    !recovered.Recovery.CanResume) throw new InvalidOperationException("Recovery lost costs or remains blocked.");
                using var resumed = await host.Client.PostAsJsonAsync("/agent/tasks/commands?wait=false", new TaskCommand { Kind = TaskCommandKind.Resume,
                    TaskId = id, RecoveryToken = recovered.Recovery.ContinuationToken }, TaskJson.Options, deadline.Token);
                if (resumed.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException("Resume rejected.");
                while (true)
                {
                    final = (await store.LoadAsync(id, deadline.Token))!;
                    bool progressed = final.Steps.Count > recovered.Steps.Count || final.Budget.ModelCalls > before.Budget.ModelCalls ||
                        final.Status == AgentTaskStatus.Completed || final.Status == AgentTaskStatus.Failed || final.Status == AgentTaskStatus.Cancelled;
                    if (progressed && (final.Status == AgentTaskStatus.Completed || final.Status == AgentTaskStatus.Failed || final.Status == AgentTaskStatus.Cancelled ||
                        final.Wait == WaitReason.Budget || final.Wait == WaitReason.Reconciliation || final.Wait == WaitReason.ModelLimit)) break;
                    var failure = await host.Client.GetFromJsonAsync<TaskExecutionFailure>("/agent/tasks/" + id + "/failure", TaskJson.Options, deadline.Token);
                    if (failure != null) throw new InvalidOperationException("Background failure: " + failure.ErrorType + "/" + failure.InnerType);
                    await Task.Delay(100, deadline.Token);
                }
                string workspace = Path.Combine(directory, "workspace"), sample = Path.Combine(Directory.GetCurrentDirectory(), "examples", "sample-dotnet-project");
                bool protectedFiles = Directory.GetFiles(sample, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != "Calculator.cs" &&
                    !Path.GetRelativePath(sample, p).Split(Path.DirectorySeparatorChar).Any(s => s == "bin" || s == "obj"))
                    .All(p => WorkspaceAccess.HashBytes(File.ReadAllBytes(p)) == WorkspaceAccess.HashBytes(File.ReadAllBytes(Path.Combine(workspace, Path.GetRelativePath(sample, p)))));
                foreach (var operation in before.Operations.Where(o => o.Call.Name == "read_file" && o.Outcome?.Status == ToolCallStatus.Succeeded))
                {
                    using var data = JsonDocument.Parse(operation.Outcome!.Output);
                    if (data.RootElement.TryGetProperty("path", out var path) && path.GetString()!.StartsWith("context-", StringComparison.Ordinal))
                        protectedFiles &= data.RootElement.GetProperty("hash").GetString() == WorkspaceAccess.HashBytes(File.ReadAllBytes(Path.Combine(workspace, path.GetString()!)));
                }
                string marker = Directory.GetFiles(workspace, "context-*.txt").Select(File.ReadAllText)
                    .Select(s => System.Text.RegularExpressions.Regex.Match(s, @"V3_KEEP_[a-f0-9]{8}")).First(m => m.Success).Value;
                passed = final.Status == AgentTaskStatus.Completed && protectedFiles && final.Compactions.Count(c => c.Status == CompactionStatus.Committed) >= 2 &&
                    final.Budget.ModelCalls >= before.Budget.ModelCalls && final.Budget.Usage.TotalTokens >= before.Budget.Usage.TotalTokens &&
                    final.Turns.Single().Id == before.Turns.Single().Id && final.Turns.Single().LastResponse!.Message.Text.Contains(marker, StringComparison.Ordinal) &&
                    TaskJson.Hash(JsonSerializer.Serialize(final.History.Take(before.History.Count), TaskJson.Options)) == TaskJson.Hash(JsonSerializer.Serialize(before.History, TaskJson.Options)) &&
                    final.Evidence.Any(e => e.Passed && e.ExitCode == 0 && e.WorkspaceRevision == final.Facts.Workspace.Revision) &&
                    final.Artifacts.All(a => a.ContentHash == WorkspaceAccess.HashBytes(File.ReadAllBytes(a.Location)));
                if (!passed) error = "Resumed acceptance failed: " + final.Status + "/" + final.Wait + "; " + final.Turns.Last().StopReason;
            }
            catch (Exception exception) { error = Sanitize(exception.GetType().Name + ": " + exception.Message, key); }
            final = await store.LoadAsync(id, default);
            string tag = Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(Path.Combine(directory, "resumed-task-" + tag + ".json"), Sanitize(JsonSerializer.Serialize(final, TaskJson.Options), key));
            var report = new { checkedAtUtc = DateTimeOffset.UtcNow, protocol, realModel = true, resumedExistingTask = true, taskId = id, passed, error,
                beforeCalls = before.Budget.ModelCalls, beforeTokens = before.Budget.Usage.TotalTokens,
                afterCalls = final?.Budget.ModelCalls, afterTokens = final?.Budget.Usage.TotalTokens,
                committedCompactions = final?.Compactions.Count(c => c.Status == CompactionStatus.Committed),
                callsByPurpose = final?.ModelInvocations.GroupBy(i => i.Purpose).Select(g => new InvocationTotal { Purpose = g.Key, Calls = g.Count(), Tokens = g.Sum(i => i.Usage.TotalTokens) }) };
            string reportPath = Path.Combine(directory, "resume-report-" + tag + ".json");
            await File.WriteAllTextAsync(reportPath, Sanitize(JsonSerializer.Serialize(report, TaskJson.Options), key));
            Console.WriteLine(Sanitize(JsonSerializer.Serialize(report, TaskJson.Options), key)); Console.WriteLine("Resume report: " + reportPath);
            return passed ? 0 : 1;
        }
        private static async Task<int> AuditContextRecoveryAsync(string[] directories)
        {
            var results = new List<object>(); bool all = true;
            foreach (string directory in directories)
            {
                string id = Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(directory, "tasks"), "task_*.json").Single());
                var task = (await new FileTaskStore(Path.Combine(directory, "tasks")).LoadAsync(id, default))!;
                using var http = new HttpClient(); var options = new ModelOptions { Model = task.Model.Model, Protocol = task.Model.Protocol };
                IModelRequestSizer sizer = task.Model.Protocol == "Responses" ? new ResponsesModelProvider(http, options) : new ChatCompletionsModelProvider(http, options);
                string workspace = Path.Combine(directory, "workspace"), sample = Path.Combine(Directory.GetCurrentDirectory(), "examples", "sample-dotnet-project");
                var checks = new Dictionary<string, bool>
                {
                    ["completed"] = task.Status == AgentTaskStatus.Completed,
                    ["known_costs"] = task.Budget.Usage.Known && task.ModelInvocations.All(i => i.UsageApplied && i.Usage.Known && i.Status == ModelInvocationStatus.Responded),
                    ["call_aggregate"] = task.Budget.ModelCalls == task.LegacyModelCalls + task.ModelInvocations.Count,
                    ["token_aggregate"] = task.Budget.Usage.TotalTokens == task.LegacyUsage.TotalTokens + task.ModelInvocations.Sum(i => i.Usage.TotalTokens),
                    ["summary_bindings"] = task.Compactions.Where(c => c.Status == CompactionStatus.Committed).All(c => MiniHarness.Harness.Context.ModelContextProjection.ValidSummary(task, c)),
                    ["active_pointer"] = task.Compactions.Any(c => c.Id == task.ContextMemory.SummaryId && c.Status == CompactionStatus.Committed),
                    ["wire_limits"] = task.ModelInvocations.All(i => sizer.MeasureCharacters(i.Request) <= 26000),
                    ["summary_no_tools"] = task.ModelInvocations.Where(i => i.Purpose == ModelInvocationPurpose.Summary).All(i => i.Request.Tools.Count == 0 && i.Request.Options.ToolChoice == "none"),
                    ["current_evidence"] = task.Evidence.Any(e => e.Passed && e.ExitCode == 0 && e.WorkspaceRevision == task.Facts.Workspace.Revision),
                    ["artifact_hashes"] = task.Artifacts.All(a => a.OwnerTaskId == task.Id && a.ContentHash == WorkspaceAccess.HashBytes(File.ReadAllBytes(a.Location))),
                    ["protected_sample"] = Directory.GetFiles(sample, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != "Calculator.cs" &&
                        !Path.GetRelativePath(sample, p).Split(Path.DirectorySeparatorChar).Any(s => s == "bin" || s == "obj"))
                        .All(p => WorkspaceAccess.HashBytes(File.ReadAllBytes(p)) == WorkspaceAccess.HashBytes(File.ReadAllBytes(Path.Combine(workspace, Path.GetRelativePath(sample, p))))),
                    ["only_calculator_mutated"] = task.Operations.Where(o => (o.Call.Name == "apply_patch" || o.Call.Name == "create_file") && o.Outcome?.Effect == EffectState.Confirmed)
                        .All(o => JsonDocument.Parse(o.Call.Arguments).RootElement.GetProperty("path").GetString() == "Calculator.cs")
                };
                string current = await new DirectoryWorkspaceRevision().GetRevisionAsync(task.Facts.Workspace, default);
                checks["actual_revision"] = current == task.Facts.Workspace.Revision;
                checks["notes_unchanged"] = true;
                foreach (var operation in task.Operations.Where(o => o.Call.Name == "read_file" && o.Outcome?.Status == ToolCallStatus.Succeeded))
                {
                    using var output = JsonDocument.Parse(operation.Outcome!.Output);
                    if (output.RootElement.TryGetProperty("path", out var path) && path.GetString()!.StartsWith("context-", StringComparison.Ordinal))
                        checks["notes_unchanged"] &= output.RootElement.GetProperty("hash").GetString() == WorkspaceAccess.HashBytes(File.ReadAllBytes(Path.Combine(workspace, path.GetString()!)));
                }
                string marker = Directory.GetFiles(workspace, "context-*.txt").Select(File.ReadAllText).Select(t => System.Text.RegularExpressions.Regex.Match(t, @"V3_KEEP_[a-f0-9]{8}")).First(m => m.Success).Value;
                checks["remembered_literal"] = task.Turns.Single().LastResponse!.Message.Text.Contains(marker, StringComparison.Ordinal);
                bool passed = checks.Values.All(v => v); all &= passed;
                var report = new { checkedAtUtc = DateTimeOffset.UtcNow, directory, taskId = id, task.Model.Protocol, passed, checks,
                    task.Budget.ModelCalls, task.Budget.ToolCalls, task.Budget.Usage, committed = task.Compactions.Count(c => c.Status == CompactionStatus.Committed),
                    purposes = task.ModelInvocations.GroupBy(i => i.Purpose).Select(g => new InvocationTotal { Purpose = g.Key, Calls = g.Count(), Tokens = g.Sum(i => i.Usage.TotalTokens) }) };
                await File.WriteAllTextAsync(Path.Combine(directory, "independent-audit.json"), JsonSerializer.Serialize(report, TaskJson.Options));
                results.Add(report);
            }
            Console.WriteLine(JsonSerializer.Serialize(results, TaskJson.Options)); return all ? 0 : 1;
        }
        private sealed class InvocationTotal
        {
            public ModelInvocationPurpose Purpose { get; set; }
            public int Calls { get; set; }
            public long Tokens { get; set; }
        }
        private sealed class ContextRecoveryResult
        {
            public string Scenario { get; set; } = "";
            public string TaskId { get; set; } = "";
            public string Prompt { get; set; } = "";
            public string? Steering { get; set; }
            public bool Passed { get; set; }
            public string? Error { get; set; }
            public string? Status { get; set; }
            public string? OriginalTurnId { get; set; }
            public int BeforeControlModelCalls { get; set; }
            public int CommittedCompactions { get; set; }
            public int ModelCalls { get; set; }
            public int ToolCalls { get; set; }
            public TokenUsage Usage { get; set; } = new TokenUsage();
            public List<InvocationTotal> CallsByPurpose { get; set; } = new List<InvocationTotal>();
            public RecoveryRecord? Recovery { get; set; }
        }
    }
}
