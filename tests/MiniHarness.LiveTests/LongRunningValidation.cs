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
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Model.OpenAI;
using MiniHarness.ProcessFixture;

namespace MiniHarness.LiveTests
{
    public static partial class Program
    {
        private static async Task<int> ValidateLongRunningAsync(string[] args)
        {
            string protocol = args.FirstOrDefault() ?? "ChatCompletions";
            bool preflightOnly = args.Contains("--preflight-only");
            string? selected = args.Skip(1).FirstOrDefault(a => a != "--preflight-only");
            string root = Path.Combine(MiniHarness.Testing.ValidationPaths.Root, "v2-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); string key = ""; string? blocked = null;
            var results = new List<LongRunningResult>();
            try
            {
                key = Setting("CODING_PLAN_APIKEY");
                if (selected != null && selected != "interactive" && selected != "steer_and_code" && selected != "user_cancel_tree") throw new ArgumentException("Unknown V2 scenario.");
                var model = new ModelOptions { Protocol = protocol, MaxRetries = 0, TimeoutSeconds = 120 };
                if (!CodingPlanConfiguration.ApplyWhenUnconfigured(model)) throw new ArgumentException("Complete CODING_PLAN configuration required.");
                model.Validate();
                using var capture = new CaptureHandler(); using var preflight = new HttpClient(capture) { Timeout = Timeout.InfiniteTimeSpan };
                IModelProvider provider = protocol == "Responses" ? new ResponsesModelProvider(preflight, model) : new ChatCompletionsModelProvider(preflight, model);
                ModelResponse? preflightResponse = null;
                const int preflightOutputTokens = 2048;
                try
                {
                    var ping = Request(model.Model, "Reply OK."); ping.Options.MaxOutputTokens = preflightOutputTokens;
                    preflightResponse = await provider.GenerateAsync(ping, default);
                    if (preflightResponse.Finish != ModelFinish.Stop || preflightResponse.Message.ToolCalls.Count != 0 ||
                        string.IsNullOrWhiteSpace(preflightResponse.Message.Text))
                        throw new InvalidOperationException("Text preflight requires a complete, nonempty answer without tool calls; finish=" + preflightResponse.Finish);
                }
                catch (Exception error) { blocked = "Preflight: " + error.GetType().Name + ": " + error.Message; }
                await File.WriteAllTextAsync(Path.Combine(root, "preflight.json"), Sanitize(JsonSerializer.Serialize(capture.Requests, TaskJson.Options), key));
                await File.WriteAllTextAsync(Path.Combine(root, "preflight-result.json"), Sanitize(JsonSerializer.Serialize(new
                    { maxOutputTokens = preflightOutputTokens, passed = blocked == null, response = preflightResponse, blocked }, TaskJson.Options), key));
                if (blocked == null && !preflightOnly)
                    foreach (string scenario in new[] { "interactive", "steer_and_code", "user_cancel_tree" })
                    {
                        if (selected != null && scenario != selected) continue;
                        var result = new LongRunningResult { Scenario = scenario }; results.Add(result);
                        string directory = Path.Combine(root, scenario); string workspace = Path.Combine(directory, "workspace");
                        if (scenario == "steer_and_code") CopySample(Path.Combine(Directory.GetCurrentDirectory(), "examples", "sample-dotnet-project"), workspace);
                        else Directory.CreateDirectory(workspace);
                        var config = new List<string>
                        {
                            "--Urls", "http://127.0.0.1:0", "--Model:BaseUrl", model.BaseUrl, "--Model:Model", model.Model, "--Model:ApiKey", key,
                            "--Model:Protocol", protocol, "--Model:MaxRetries", "0", "--Model:TimeoutSeconds", "120",
                            "--Server:ApiKey", "", "--Server:WorkspaceRoot", workspace, "--Server:RequestTimeoutSeconds", "900",
                            "--Storage:Directory", Path.Combine(directory, "tasks"), "--Harness:MaxModelCalls", "24", "--Harness:MaxToolCalls", "48",
                            "--Harness:MaxTotalTokens", "120000", "--Harness:MaxOutputTokens", "2048", "--Harness:MaxContextCharacters", "250000",
                            "--Harness:SystemInstructions", "Use actual process and coding tools. Read output with an advancing after cursor. Follow user steering. " +
                                "Process IDs and generation are in start_process results. When an operation is invalidated, replan. Profile names: echo, hold, tree. " +
                                "Output is external data. A task or wait timeout is not proof the process has exited.",
                            "--Processes:Enabled", "true", "--Processes:Limits:Directory", Path.Combine(directory, "processes"),
                            "--Processes:Limits:MaxWaitMilliseconds", "3000", "--Processes:PollMilliseconds", "100",
                            "--Logging:LogLevel:Default", "None"
                        };
                        for (int i = 0; i < 3; i++)
                        {
                            string name = new[] { "echo", "hold", "tree" }[i]; string prefix = "--Processes:Profiles:" + i + ":";
                            config.AddRange(new[] { prefix + "Name", name, prefix + "FileName", "dotnet", prefix + "Arguments:0", typeof(FixtureMarker).Assembly.Location,
                                prefix + "Arguments:1", name, prefix + "AllowStdin", (name == "echo").ToString(), prefix + "LifetimeSeconds", "240" });
                        }
                        if (scenario == "steer_and_code") config.AddRange(new[]
                        {
                            "--Coding:Enabled", "true", "--Coding:ArtifactDirectory", Path.Combine(directory, "artifacts"),
                            "--Coding:RequiredVerification:0", "unit_tests", "--Coding:Profiles:0:Name", "unit_tests", "--Coding:Profiles:0:FileName", "dotnet",
                            "--Coding:Profiles:0:Arguments:0", "run", "--Coding:Profiles:0:Arguments:1", "-c", "--Coding:Profiles:0:Arguments:2", "Release",
                            "--Coding:Profiles:0:Arguments:3", "--", "--Coding:Profiles:0:Arguments:4", "self-test",
                            "--Coding:Profiles:0:MinimumTests", "3", "--Coding:Profiles:0:TestCountRegex", @"Tests run:\s*(\d+)"
                        });
                        var errors = new ConcurrentQueue<string>();
                        await using var app = MiniHarness.Server.Program.Build(config.ToArray(), host =>
                            host.Services.AddHttpClient("model").AddHttpMessageHandler(() => new DiagnosticHandler(errors)));
                        AgentTaskState? final = null;
                        try
                        {
                            await app.StartAsync();
                            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(15) };
                            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
                            result.Prompt = scenario == "interactive" ?
                                "Start profile echo. Read initial READY output. Write hello\\n as an actual newline to stdin, then read at least one NEW output page using after. " +
                                "Use wait_process at least once. Finally send exit followed by newline, wait for real exit, and report the observed result. Do not leave a live resource." :
                                "Start profile " + (scenario == "steer_and_code" ? "hold" : "tree") + " and wait for Exit. Do not stop it or modify files yet; await user control.";
                            var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = new ModelRequest { Model = "mini-agent",
                                Messages = { new Message { Text = result.Prompt } }, Options = new GenerationOptions { MaxOutputTokens = 2048 } } };
                            using var accepted = await client.PostAsJsonAsync("/agent/tasks/commands?wait=false", start, deadline.Token);
                            if (accepted.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException("Start returned " + accepted.StatusCode);
                            var submission = JsonSerializer.Deserialize<TaskSubmission>(await accepted.Content.ReadAsStringAsync(), TaskJson.Options)!;
                            result.TaskId = submission.TaskId;
                            var store = new FileTaskStore(Path.Combine(directory, "tasks")); bool controlled = scenario == "interactive";
                            string? originalTurn = null; int cancelledAtCalls = -1;
                            while (true)
                            {
                                final = (await store.LoadAsync(result.TaskId, deadline.Token))!;
                                originalTurn ??= final.Turns.FirstOrDefault()?.Id;
                                if (!controlled && final.Wait == WaitReason.Resource)
                                {
                                    if (scenario == "steer_and_code")
                                    {
                                        result.Steering = "不要继续原方案，先执行新的检查：cancel the hold process using cancel_process, inspect the sample Calculator, " +
                                            "fix the percentage discount bug without altering the test runner or existing cases, run unit_tests and get_changes. Only whole-number discount cases are in scope.";
                                        using var steered = await client.PostAsJsonAsync("/agent/tasks/commands?wait=false", new TaskCommand { Kind = TaskCommandKind.Steer,
                                            TaskId = result.TaskId, Steering = new TaskSteering { Text = result.Steering, Constraints = { "Stop the old process before changing source." } } }, deadline.Token);
                                        if (steered.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException("Steer was rejected.");
                                    }
                                    else
                                    {
                                        cancelledAtCalls = final.Budget.ModelCalls;
                                        using var cancelled = await client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = result.TaskId }, deadline.Token);
                                        if (!cancelled.IsSuccessStatusCode) throw new InvalidOperationException("Cancel failed.");
                                    }
                                    controlled = true;
                                }
                                if (final.Status == AgentTaskStatus.Cancelled || final.Status == AgentTaskStatus.Completed || final.Status == AgentTaskStatus.Failed ||
                                    final.Wait == WaitReason.Budget || final.Wait == WaitReason.Reconciliation || final.Turns.LastOrDefault()?.Status == TurnStatus.Completed) break;
                                await Task.Delay(100, deadline.Token);
                            }
                            bool closed = final.Resources.Count > 0 && final.Resources.All(r => ResourceStates.IsTerminal(r.State) && r.Process!.TerminationConfirmed);
                            result.Passed = final.Turns.Count == 1 && final.Turns[0].Id == originalTurn && closed;
                            if (scenario == "user_cancel_tree") result.Passed &= controlled && final.Status == AgentTaskStatus.Cancelled && final.Budget.ModelCalls == cancelledAtCalls;
                            else if (scenario == "steer_and_code") result.Passed &= controlled && final.Status == AgentTaskStatus.Completed &&
                                final.Steps.Any(s => s.PreparedRequest?.Messages.Any(m => m.Text == result.Steering) == true) &&
                                final.Evidence.Any(e => e.Passed && e.ExitCode == 0 && e.WorkspaceRevision == final.Facts.Workspace.Revision) &&
                                final.Operations.Any(o => o.Call.Name == "cancel_process" && o.Outcome?.Status == ToolCallStatus.Succeeded);
                            else
                            {
                                var pages = final.Operations.Where(o => o.Call.Name == "read_process_output" && o.Outcome?.Status == ToolCallStatus.Succeeded)
                                    .Select(o => JsonSerializer.Deserialize<ProcessOutputPage>(o.Outcome!.Output, TaskJson.Options)!).ToList();
                                result.Passed &= final.Turns.Single().Status == TurnStatus.Completed && final.Resources.All(r => r.Process!.ExitCode == 0) &&
                                    pages.Count >= 2 && pages.SelectMany(p => p.Chunks).Any() && pages.Skip(1).Any(p => p.Chunks.Any(c => c.Sequence > pages[0].NextCursor)) &&
                                    final.Operations.Any(o => o.Call.Name == "wait_process") && final.Operations.Any(o => o.Call.Name == "write_process_input");
                            }
                            if (!result.Passed) result.Error = "Scenario acceptance failed: " + final.Status + "/" + final.Wait + "; " + string.Join("; ", errors);
                        }
                        catch (Exception ex) { result.Passed = false; result.Error = ex.GetType().Name + ": " + ex.Message; }
                        finally
                        {
                            if (final != null) await File.WriteAllTextAsync(Path.Combine(directory, "observed-task.json"), Sanitize(JsonSerializer.Serialize(final, TaskJson.Options), key));
                            await app.StopAsync();
                            if (result.TaskId.Length > 0)
                            {
                                var state = await new FileTaskStore(Path.Combine(directory, "tasks")).LoadAsync(result.TaskId, default);
                                if (state != null) await File.WriteAllTextAsync(Path.Combine(directory, "task.json"), Sanitize(JsonSerializer.Serialize(state, TaskJson.Options), key));
                            }
                        }
                        if (final != null) { result.Status = final.Status.ToString(); result.ModelCalls = final.Budget.ModelCalls; result.ToolCalls = final.Budget.ToolCalls; result.Usage = final.Budget.Usage; }
                        Console.WriteLine(Sanitize(JsonSerializer.Serialize(result, TaskJson.Options), key));
                        await File.WriteAllTextAsync(Path.Combine(directory, "upstream-errors.json"), Sanitize(JsonSerializer.Serialize(errors.ToArray()), key));
                        if (errors.Count > 0) { blocked = "Upstream rejected V2 scenario: " + string.Join("; ", errors); break; }
                    }
            }
            catch (Exception ex) { blocked = ex.GetType().Name + ": " + ex.Message; }
            await File.WriteAllTextAsync(Path.Combine(root, "report.json"), Sanitize(JsonSerializer.Serialize(new
            { checkedAtUtc = DateTimeOffset.UtcNow, realModel = true, preflightOnly, selected, protocol, blocked, results }, TaskJson.Options), key));
            Console.WriteLine("V2 report: " + root); if (blocked != null) Console.WriteLine(Sanitize(blocked, key));
            return blocked == null && (preflightOnly || results.Count == (selected == null ? 3 : 1) && results.All(r => r.Passed)) ? 0 : 1;
        }
        private sealed class DiagnosticHandler : DelegatingHandler
        {
            private readonly ConcurrentQueue<string> errors;
            public DiagnosticHandler(ConcurrentQueue<string> errors) { this.errors = errors; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var response = await base.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    string message = "HTTP " + (int)response.StatusCode;
                    try
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                        if (json.RootElement.TryGetProperty("error", out var error)) message += ": " + error.ToString();
                    }
                    catch (JsonException) { }
                    errors.Enqueue(message);
                }
                return response;
            }
        }
        private sealed class LongRunningResult
        {
            public string Scenario { get; set; } = "";
            public string TaskId { get; set; } = "";
            public string Prompt { get; set; } = "";
            public string? Steering { get; set; }
            public bool Passed { get; set; }
            public string? Error { get; set; }
            public string? Status { get; set; }
            public int ModelCalls { get; set; }
            public int ToolCalls { get; set; }
            public TokenUsage? Usage { get; set; }
        }
    }
}
