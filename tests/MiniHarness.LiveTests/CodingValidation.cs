using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Coding;
using MiniHarness.Harness;
using MiniHarness.Model.OpenAI;
using MiniHarness.Server;

namespace MiniHarness.LiveTests
{
    public static partial class Program
    {
        // Two real decisions-to-effects scenarios. Production composition and
        // tools are reused; only isolated fixtures and acceptance assertions live here.
        private static async Task<int> ValidateCodingAsync(string[] args)
        {
            string root = Path.Combine(MiniHarness.Testing.ValidationPaths.Root, "v1-coding-" +
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            string key = "";
            var results = new List<CodingProbe>();
            string? blocked = null;
            bool offline = args.FirstOrDefault() == "Offline";
            string protocol = args.FirstOrDefault() ?? "ChatCompletions";
            string modelName = "";
            string? selectedScenario = args.Skip(1).FirstOrDefault();
            try
            {
                if (!offline && protocol != "Responses" && protocol != "ChatCompletions") throw new ArgumentException("Expected Responses or ChatCompletions.");
                if (selectedScenario != null && selectedScenario != "fix_existing" && selectedScenario != "regression_then_fix")
                    throw new ArgumentException("Expected fix_existing or regression_then_fix.");
                if (!offline) key = Setting("CODING_PLAN_APIKEY");
                var modelOptions = new ModelOptions { Protocol = protocol, MaxRetries = 0, TimeoutSeconds = 120 };
                if (offline) modelOptions.Model = "fixture";
                else
                {
                    if (!CodingPlanConfiguration.ApplyWhenUnconfigured(modelOptions)) throw new ArgumentException("Missing complete CODING_PLAN configuration.");
                    modelOptions.Validate();
                }
                modelName = modelOptions.Model;
                using var capture = new CaptureHandler();
                using var client = new HttpClient(capture) { Timeout = Timeout.InfiniteTimeSpan };
                IModelProvider provider = offline ? new CodingFixtureModel() : protocol == "Responses" ? new ResponsesModelProvider(client, modelOptions)
                    : new ChatCompletionsModelProvider(client, modelOptions);
                // One bounded check: do not burn task budgets on an unavailable endpoint.
                try
                {
                    if (!offline)
                    {
                        var ping = Request(modelOptions.Model, "Reply OK."); ping.Options.MaxOutputTokens = 32;
                        await provider.GenerateAsync(ping, CancellationToken.None);
                    }
                }
                catch (Exception ex) { blocked = "Model preflight failed: " + ex.GetType().Name + ": " + ex.Message; }
                if (blocked == null)
                {
                    foreach (bool regressionFirst in new[] { false, true })
                    {
                        var result = new CodingProbe { Scenario = regressionFirst ? "regression_then_fix" : "fix_existing" };
                        if (selectedScenario != null && selectedScenario != result.Scenario) continue;
                        results.Add(result);
                        var timer = Stopwatch.StartNew();
                        string scenario = Path.Combine(root, result.Scenario);
                        string workspace = Path.Combine(scenario, "workspace");
                        CopySample(Path.Combine(Directory.GetCurrentDirectory(), "examples", "sample-dotnet-project"), workspace);
                        if (regressionFirst)
                        {
                            foreach (string path in Directory.GetFiles(Path.Combine(workspace, "tests"), "*.case")) File.Delete(path);
                            await File.WriteAllTextAsync(Path.Combine(workspace, "tests", "base.case"), "100 10 90\n");
                        }
                        string initialSource = await File.ReadAllTextAsync(Path.Combine(workspace, "Calculator.cs"));
                        var protectedFiles = Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
                            .Where(p => Path.GetFileName(p) != "Calculator.cs")
                            .ToDictionary(p => p, p => WorkspaceAccess.HashBytes(File.ReadAllBytes(p)));
                        var coding = new CodingComposition(new CodingOptions
                        {
                            Enabled = true, ArtifactDirectory = Path.Combine(scenario, "artifacts"), RequiredVerification = new[] { "unit_tests" },
                            Profiles = new[] { new VerificationProfileOptions
                            {
                                Name = "unit_tests", FileName = "dotnet", Arguments = new[] { "run", "-c", "Release", "--", "self-test" },
                                MinimumTests = regressionFirst ? 2 : 3, TestCountRegex = @"Tests run:\s*(\d+)", TimeoutSeconds = 120
                            } }
                        }, scenario);
                        result.Prompt = "Fix the discount calculation in this C# project. A percent discount means price minus that percentage of price. " +
                            "Keep the public API and the test runner unchanged. Read the project, use the provided tools to edit, run unit_tests and inspect get_changes. " +
                            (regressionFirst ? "FIRST create tests/regression.case with one case that exposes the incorrect calculation and has an exact whole-number discounted price. Fractional-price rounding is outside this task's specification; do not add rounding edge cases. Then run unit_tests and observe the new case failing BEFORE editing Calculator.cs. After that fix Calculator.cs and run all tests again. Do not weaken base.case or the regression test." :
                            "Run unit_tests to observe the existing failing test BEFORE fixing Calculator.cs. Do not modify the existing test cases.");
                        var limits = new HarnessOptions
                        {
                            MaxModelCalls = 24, MaxToolCalls = 48, MaxTotalTokens = 90000, MaxOutputTokens = 2048,
                            MaxContextCharacters = 200000, SystemInstructions = "You are a coding agent. Use tools for real changes and verification. " +
                                "Allowed verification profile: unit_tests. Read_file provides the expected_hash for apply_patch. " +
                                "If an operation is invalidated after a workspace change, replan and issue a fresh call. Never claim unexecuted tests passed."
                        };
                        var binding = new ModelBinding { Model = modelOptions.Model, Protocol = protocol,
                            ConfigurationVersion = TaskJson.Hash(modelOptions.BaseUrl + modelOptions.Model + protocol) };
                        var store = new FileTaskStore(Path.Combine(scenario, "checkpoints"));
                        var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = new ModelRequest
                        {
                            Model = modelOptions.Model, Messages = { new Message { Role = "user", Text = result.Prompt } },
                            Options = new GenerationOptions { MaxOutputTokens = 2048 }
                        }, Facts = new TaskFacts { Goal = result.Prompt, Workspace = new WorkspaceScope { RootPath = workspace } } };
                        new CodingTaskSetup(coding).Apply(start.Facts);
                        result.TaskId = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                        await using (var runtime = new TaskRuntime(provider, store, limits, binding, tools: new ToolRegistry(coding.Tools),
                            toolPolicy: new AllowListToolPolicy(coding.Tools.Select(t => t.Definition.Name)),
                            workspaceRevision: coding.Revision, completion: coding.Completion))
                        {
                            try
                            {
                                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
                                var running = runtime.ExecuteAsync(start, CancellationToken.None);
                                AgentTaskState state;
                                try { state = (await running.WaitAsync(deadline.Token)).State; }
                                catch (OperationCanceledException)
                                {
                                    await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = result.TaskId }, CancellationToken.None);
                                    await running;
                                    throw new TimeoutException("Coding scenario exceeded 12 minutes.");
                                }
                                result.Status = state.Status.ToString(); result.Usage = state.Budget.Usage;
                                result.ModelCalls = state.Budget.ModelCalls; result.ToolCalls = state.Budget.ToolCalls;
                                result.FinalRevision = state.Facts.Workspace.Revision;
                                var checks = state.Operations.Where(o => o.Call.Name == "run_verification" && o.Outcome?.Evidence.Count > 0).ToList();
                                var last = checks.LastOrDefault();
                                bool currentPass = last?.Outcome!.Evidence.Any(e => e.Passed && e.ExitCode == 0 && e.WorkspaceRevision == result.FinalRevision) == true;
                                bool failedFirst = checks.Any(o => o.Outcome!.Evidence.Any(e => !e.Passed && e.ExitCode == 1));
                                var patch = state.Operations.FirstOrDefault(o => o.Call.Name == "apply_patch" && o.Outcome?.Effect == EffectState.Confirmed);
                                bool failBeforePatch = patch != null && checks.Any(o => state.Operations.IndexOf(o) < state.Operations.IndexOf(patch) &&
                                    o.Outcome!.Evidence.Any(e => !e.Passed && e.ExitCode == 1) && (!regressionFirst || o.Outcome.Output.Contains("regression.case", StringComparison.Ordinal)));
                                bool newCase = !regressionFirst || File.Exists(Path.Combine(workspace, "tests", "regression.case"));
                                if (regressionFirst && newCase)
                                {
                                    var created = state.Operations.FirstOrDefault(o => o.Call.Name == "create_file" && o.Outcome?.Effect == EffectState.Confirmed &&
                                        JsonSerializer.Deserialize<JsonElement>(o.Call.Arguments).GetProperty("path").GetString()?.Replace('\\', '/') == "tests/regression.case");
                                    newCase = created != null && await File.ReadAllTextAsync(Path.Combine(workspace, "tests", "regression.case")) ==
                                        JsonSerializer.Deserialize<JsonElement>(created.Call.Arguments).GetProperty("content").GetString();
                                }
                                bool unchangedTests = protectedFiles.All(p => File.Exists(p.Key) && WorkspaceAccess.HashBytes(File.ReadAllBytes(p.Key)) == p.Value);
                                result.Passed = state.Status == AgentTaskStatus.Completed && currentPass && failedFirst && failBeforePatch && newCase &&
                                    unchangedTests && initialSource != await File.ReadAllTextAsync(Path.Combine(workspace, "Calculator.cs"));
                                if (!result.Passed) result.Error = "Acceptance failed: completed=" + (state.Status == AgentTaskStatus.Completed) +
                                    ", currentPass=" + currentPass + ", failBeforePatch=" + failBeforePatch + ", regressionExists=" + newCase + ", protectedFilesUnchanged=" + unchangedTests;
                                var changes = await new GetChangesTool(coding.Ledger).ExecuteAsync(JsonSerializer.SerializeToElement(new { }),
                                    new ToolExecutionContext { TaskId = state.Id, Workspace = state.Facts.Workspace }, CancellationToken.None);
                                await File.WriteAllTextAsync(Path.Combine(scenario, "changes.json"), changes.Output);
                            }
                            catch (Exception ex) { result.Passed = false; result.Error = ex.GetType().Name + ": " + ex.Message; }
                            var saved = await store.LoadAsync(result.TaskId, CancellationToken.None);
                            if (saved != null)
                            {
                                result.Status = saved.Status.ToString(); result.Usage = saved.Budget.Usage;
                                result.ModelCalls = saved.Budget.ModelCalls; result.ToolCalls = saved.Budget.ToolCalls;
                                result.FinalRevision = saved.Facts.Workspace.Revision;
                                await File.WriteAllTextAsync(Path.Combine(scenario, "task.json"), Sanitize(JsonSerializer.Serialize(saved, TaskJson.Options), key));
                            }
                        }
                        result.ElapsedMs = timer.ElapsedMilliseconds;
                        Console.WriteLine(Sanitize(JsonSerializer.Serialize(result, TaskJson.Options), key));
                        // No automatic scenario retries; retain every failure as evidence.
                        if (capture.Requests.Any(r => r.Status == 401 || r.Status == 403 || r.Status == 429)) break;
                    }
                }
                await File.WriteAllTextAsync(Path.Combine(root, "http.json"), Sanitize(JsonSerializer.Serialize(capture.Requests, TaskJson.Options), key));
            }
            catch (Exception ex) { blocked = ex.GetType().Name + ": " + ex.Message; }
            await File.WriteAllTextAsync(Path.Combine(root, "report.json"), Sanitize(JsonSerializer.Serialize(new
            { checkedAtUtc = DateTimeOffset.UtcNow, realModel = !offline, protocol, model = modelName,
                outputCapPerCall = 2048, retries = 0, blocked, results }, TaskJson.Options), key));
            Console.WriteLine("Coding report: " + root);
            if (blocked != null) Console.WriteLine(Sanitize(blocked, key));
            return blocked == null && results.Count == (selectedScenario == null ? 2 : 1) && results.All(r => r.Passed) ? 0 : 1;
        }
        private static void CopySample(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (string directory in Directory.GetDirectories(source))
                if (Path.GetFileName(directory) != "bin" && Path.GetFileName(directory) != "obj")
                    CopySample(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
        private sealed class CodingProbe
        {
            public string Scenario { get; set; } = "";
            public string Prompt { get; set; } = "";
            public string TaskId { get; set; } = "";
            public bool Passed { get; set; }
            public string? Status { get; set; }
            public string? Error { get; set; }
            public string? FinalRevision { get; set; }
            public int ModelCalls { get; set; }
            public int ToolCalls { get; set; }
            public TokenUsage? Usage { get; set; }
            public long ElapsedMs { get; set; }
        }
        // Exercises the acceptance driver offline. This is explicitly labelled
        // fixture evidence and can never be reported as real-model acceptance.
        private sealed class CodingFixtureModel : IModelProvider
        {
            private string prompt = "";
            private int index;
            private string hash = "";
            private int call;
            public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
            {
                string current = request.Messages.First(m => m.Role == "user").Text;
                if (current != prompt) { prompt = current; index = 0; hash = ""; }
                else
                {
                    var observation = request.Messages.LastOrDefault(m => m.Role == "tool");
                    var result = observation == null ? null : JsonSerializer.Deserialize<ToolResult>(observation.Text, TaskJson.Options);
                    if (result != null && !result.Output.Contains("Operation invalidated", StringComparison.Ordinal))
                    {
                        if (result.Status != ToolCallStatus.Succeeded) throw new InvalidOperationException("Fixture tool failed: " + result.Output);
                        using var output = JsonDocument.Parse(result.Output);
                        if (output.RootElement.TryGetProperty("hash", out var value)) hash = value.GetString()!;
                        index++;
                    }
                }
                var plan = new List<(string, object)>();
                if (prompt.Contains("FIRST", StringComparison.Ordinal)) plan.Add(("create_file", new { path = "tests/regression.case", content = "200 10 180\n" }));
                plan.Add(("run_verification", new { profile = "unit_tests" }));
                plan.Add(("read_file", new { path = "Calculator.cs" }));
                plan.Add(("apply_patch", new { path = "Calculator.cs", expected_hash = hash, old_text = "=> price - percent;", new_text = "=> price - price * percent / 100;" }));
                plan.Add(("run_verification", new { profile = "unit_tests" }));
                plan.Add(("get_changes", new { }));
                var response = new ModelResponse { Finish = index >= plan.Count ? ModelFinish.Stop : ModelFinish.ToolCalls,
                    Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1 }, Message = new Message { Role = "assistant", Text = index >= plan.Count ? "Fixture completed." : "" } };
                if (index < plan.Count) response.Message.ToolCalls.Add(new ToolCall { Id = "fixture_" + ++call, Name = plan[index].Item1,
                    Arguments = JsonSerializer.Serialize(plan[index].Item2) });
                return Task.FromResult(response);
            }
        }
    }
}
