using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Model.OpenAI;
using MiniHarness.Protocols.OpenAI;

namespace MiniHarness.Tests
{
    internal static class ConsistencyTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[consistency] three entry points share initialization, outcomes and event traces across both upstreams", EntryMatrix));
            tests.Add(("[consistency] wait cancellation explicitly confirms Cancel only when requested", CancellationIntent));
            tests.Add(("[consistency] request timeout and cancellation policy match all three entry points", TimeoutMatrix));
            tests.Add(("[consistency] capacity uses actual wire view and excludes duplicated storage projections", ContextCapacity));
        }
        private static IOpenAiProtocol Wire(string name) => name == "Responses" ? new ResponsesProtocol() : new ChatCompletionsProtocol();
        private static async Task<WebApplication> Upstream(string protocol, int delayMs = 0)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build(); var wire = Wire(protocol);
            app.MapPost("/v1/" + wire.Path, async (HttpContext http) =>
            {
                using var json = await JsonDocument.ParseAsync(http.Request.Body);
                string history = json.RootElement.GetProperty(protocol == "Responses" ? "input" : "messages").GetRawText();
                bool observed = protocol == "Responses" ? history.Contains("function_call_output", StringComparison.Ordinal) :
                    json.RootElement.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "tool");
                if (delayMs > 0) await Task.Delay(delayMs, http.RequestAborted);
                string name = history.Contains("coding_probe", StringComparison.Ordinal) ? "read_file" :
                    history.Contains("unknown_probe", StringComparison.Ordinal) ? "missing" : "echo";
                string args = name == "read_file" ? "{\"path\":\"sample.txt\"}" : "{\"text\":\"same\"}";
                bool call = !observed && !history.Contains("plain_probe", StringComparison.Ordinal) && delayMs == 0;
                var result = call ? HarnessTests.Call(name, args) : HarnessTests.Answer("finished");
                await http.Response.WriteAsJsonAsync(wire.WriteResponse(new RunState { LastResponse = result, Usage = result.Usage,
                    Status = call ? RunStatus.WaitingForTools : RunStatus.Completed }, "test"));
            });
            await app.StartAsync(); return app;
        }
        private static WebApplication Host(WebApplication upstream, string protocol, string storage, string workspace, bool coding,
            int timeout = 30, bool cancel = false, int calls = 3)
            => MiniHarness.Server.Program.Build(new[]
            {
                "--Urls", "http://127.0.0.1:0", "--Model:Protocol", protocol, "--Model:BaseUrl", HttpTests.Address(upstream) + "/v1/",
                "--Model:Model", "test", "--Model:ApiKey", "test", "--Model:MaxRetries", "0", "--Server:ApiKey", "",
                "--Storage:Directory", storage, "--Server:WorkspaceRoot", workspace, "--Coding:Enabled", coding.ToString(),
                "--Coding:ArtifactDirectory", storage + "-artifacts", "--Coding:RequiredVerification:0", "unit_tests",
                "--Tools:AllowedNames:0", "echo", "--Harness:MaxModelCalls", calls.ToString(),
                "--Server:RequestTimeoutSeconds", timeout.ToString(), "--Server:CancelOnDisconnect", cancel.ToString(),
                "--Logging:LogLevel:Default", "None"
            });
        private static (string Path, JsonObject Body) Request(string entry, string text)
        {
            if (entry == "native") return ("/agent/tasks/commands", new JsonObject { ["kind"] = "Start",
                ["request"] = new JsonObject { ["model"] = "mini-agent", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = text }) } });
            bool responses = entry == "responses";
            return (responses ? "/v1/responses" : "/v1/chat/completions", HttpTests.Body(responses, text));
        }
        private static string Signature(AgentTaskState state) => JsonSerializer.Serialize(new
        {
            state.Facts, state.Status, state.Wait, state.Budget.ModelCalls, state.Budget.ToolCalls, state.Budget.Usage,
            Turns = state.Turns.Select(t => new { t.Status, t.Wait, t.StopReason }),
            Operations = state.Operations.Select(o => new { o.Call.Name, o.Status, o.Effect, o.Outcome }),
            Events = state.Events.Select(e => new { e.Type, e.Detail })
        }, TaskJson.Options);
        private static async Task EntryMatrix()
        {
            string root = Path.Combine(Path.GetTempPath(), "miniharness-consistency-regression-" + Guid.NewGuid().ToString("N"));
            string workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(Path.Combine(workspace, "sample.txt"), "sentinel");
            try
            {
                foreach (string protocol in new[] { "Responses", "ChatCompletions" })
                {
                    await using var upstream = await Upstream(protocol);
                    foreach (string scenario in new[] { "plain_probe", "echo_probe", "unknown_probe", "budget_probe", "coding_probe" })
                    {
                        string? expected = null;
                        foreach (string entry in new[] { "chat", "responses", "native" })
                        {
                            bool coding = scenario == "coding_probe";
                            string storage = Path.Combine(root, protocol, scenario, entry);
                            await using var host = Host(upstream, protocol, storage, coding ? workspace : "", coding, calls: scenario == "budget_probe" ? 1 : 3);
                            await host.StartAsync();
                            using var client = new HttpClient { BaseAddress = new Uri(HttpTests.Address(host)) };
                            var request = Request(entry, scenario);
                            using var response = await client.PostAsJsonAsync(request.Path, request.Body);
                            Check.True((int)response.StatusCode < 500, await response.Content.ReadAsStringAsync());
                            var store = new FileTaskStore(storage);
                            var state = (await store.LoadAsync(Path.GetFileNameWithoutExtension(Directory.GetFiles(storage, "task_*.json").Single()), default))!;
                            if (coding)
                            {
                                Check.Equal(workspace, state.Facts.Workspace.RootPath);
                                Check.True(new[] { "read", "write", "execute" }.All(state.Facts.Workspace.Grants.Contains));
                                Check.True(state.Facts.RequiredEvidence.Contains("verification:unit_tests"));
                                Check.Equal(ToolCallStatus.Succeeded, state.Operations.Single().Outcome!.Status);
                                Check.Equal(WaitReason.Budget, state.Wait); // Model's completion claim cannot bypass evidence.
                            }
                            string actual = Signature(state);
                            if (expected == null) expected = actual; else Check.Equal(expected, actual);
                        }
                    }
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private static async Task TimeoutMatrix()
        {
            string root = Path.Combine(Path.GetTempPath(), "miniharness-timeout-regression-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var upstream = await Upstream("ChatCompletions", delayMs: 1600);
                foreach (bool cancel in new[] { false, true })
                    foreach (string entry in new[] { "chat", "responses", "native" })
                    {
                        string storage = Path.Combine(root, cancel.ToString(), entry);
                        await using var host = Host(upstream, "ChatCompletions", storage, "", false, timeout: 1, cancel: cancel);
                        await host.StartAsync();
                        using var client = new HttpClient { BaseAddress = new Uri(HttpTests.Address(host)) };
                        var request = Request(entry, "plain_probe");
                        using var response = await client.PostAsJsonAsync(request.Path, request.Body);
                        Check.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
                        var store = new FileTaskStore(storage);
                        string id = Path.GetFileNameWithoutExtension(Directory.GetFiles(storage, "task_*.json").Single());
                        AgentTaskState state = null!;
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        do
                        {
                            state = (await store.LoadAsync(id, deadline.Token))!;
                            if (state.Status != AgentTaskStatus.Running) break;
                            await Task.Delay(20, deadline.Token);
                        } while (true);
                        Check.Equal(cancel ? AgentTaskStatus.Cancelled : AgentTaskStatus.Waiting, state.Status);
                        Check.Equal(cancel ? TurnStatus.Interrupted : TurnStatus.Completed, state.Turns.Single().Status);
                    }
                foreach (bool cancel in new[] { false, true })
                    foreach (string entry in new[] { "chat", "responses", "native" })
                    {
                        string storage = Path.Combine(root, "disconnect", cancel.ToString(), entry);
                        await using var host = Host(upstream, "ChatCompletions", storage, "", false, cancel: cancel);
                        await host.StartAsync();
                        using var client = new HttpClient { BaseAddress = new Uri(HttpTests.Address(host)) };
                        using var disconnect = new CancellationTokenSource();
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        var request = Request(entry, "plain_probe");
                        var pending = client.PostAsJsonAsync(request.Path, request.Body, disconnect.Token);
                        var store = new FileTaskStore(storage);
                        AgentTaskState? state = null;
                        string? id = null;
                        while (state?.Steps.Any(s => s.Status == StepStatus.Running) != true)
                        {
                            var file = Directory.GetFiles(storage, "task_*.json").SingleOrDefault();
                            if (file != null) { id = Path.GetFileNameWithoutExtension(file); state = await store.LoadAsync(id, deadline.Token); }
                            await Task.Delay(10, deadline.Token);
                        }
                        disconnect.Cancel();
                        await Check.ThrowsAsync<OperationCanceledException>(async () => { using var response = await pending; });
                        do
                        {
                            state = await store.LoadAsync(id!, deadline.Token);
                            if (state!.Status != AgentTaskStatus.Running) break;
                            await Task.Delay(20, deadline.Token);
                        } while (true);
                        Check.Equal(cancel ? AgentTaskStatus.Cancelled : AgentTaskStatus.Waiting, state.Status);
                    }
                // Explicit background submission acknowledges admission only.
                // Disconnecting after 202 must not cancel the running task.
                {
                    string storage = Path.Combine(root, "background");
                    await using var host = Host(upstream, "ChatCompletions", storage, "", false, timeout: 1, cancel: true);
                    await host.StartAsync();
                    string id;
                    using (var client = new HttpClient { BaseAddress = new Uri(HttpTests.Address(host)) })
                    {
                        var request = Request("native", "plain_probe");
                        using var response = await client.PostAsJsonAsync(request.Path + "?wait=false", request.Body);
                        Check.Equal(HttpStatusCode.Accepted, response.StatusCode);
                        id = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["taskId"]!.GetValue<string>();
                    }
                    var store = new FileTaskStore(storage);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    AgentTaskState state;
                    do { state = (await store.LoadAsync(id, deadline.Token))!; if (state.Turns.Single().Status == TurnStatus.Completed) break;
                        await Task.Delay(20, deadline.Token); } while (true);
                    Check.Equal(AgentTaskStatus.Waiting, state.Status);
                    Check.Equal(1, state.Receipts.Count);
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private static async Task CancellationIntent()
        {
            string root = Path.Combine(Path.GetTempPath(), "miniharness-cancel-intent-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (bool cancel in new[] { false, true })
                {
                    var model = new BlockingModel(); var store = new FileTaskStore(Path.Combine(root, cancel.ToString()));
                    var binding = new ModelBinding { Model = "test", Protocol = "Responses" };
                    await using var runtime = new TaskRuntime(model, store, new HarnessOptions(), binding);
                    var spy = new CountingRuntime(runtime);
                    var service = new ProtocolAgentService(spy, store, binding);
                    using var wait = new CancellationTokenSource();
                    var input = new AgentInput { Request = HarnessTests.Request(), CancelTaskWhenWaitCancelled = cancel };
                    var running = service.RespondAsync(input, wait.Token);
                    await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); wait.Cancel();
                    await Check.ThrowsAsync<OperationCanceledException>(() => running);
                    Check.Equal(cancel ? 1 : 0, spy.Cancels);
                    string id = "task_" + TaskJson.Hash(input.CommandId).Substring(0, 32);
                    if (!cancel) model.Release.TrySetResult(true);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    AgentTaskState state;
                    do { state = (await runtime.GetAsync(id, deadline.Token))!; if (state.Status != AgentTaskStatus.Running) break; await Task.Delay(10, deadline.Token); } while (true);
                    Check.Equal(cancel ? AgentTaskStatus.Cancelled : AgentTaskStatus.Waiting, state.Status);
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private sealed class BlockingModel : IModelProvider
        {
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
            { Entered.TrySetResult(true); await Release.Task.WaitAsync(ct); return HarnessTests.Answer(); }
        }
        private sealed class CountingRuntime : ITaskRuntime
        {
            private readonly ITaskRuntime inner;
            public int Cancels { get; private set; }
            public CountingRuntime(ITaskRuntime inner) { this.inner = inner; }
            public Task<TaskCommandResult> ExecuteAsync(TaskCommand command, CancellationToken ct)
            { if (command.Kind == TaskCommandKind.Cancel) Cancels++; return inner.ExecuteAsync(command, ct); }
            public Task<TaskSubmission> SubmitAsync(TaskCommand command, CancellationToken ct) => inner.SubmitAsync(command, ct);
            public Task<AgentTaskState?> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
            public Task<IReadOnlyList<TaskEvent>> ObserveAsync(string id, long after, CancellationToken ct) => inner.ObserveAsync(id, after, ct);
        }
        private static async Task ContextCapacity()
        {
            using var http = new HttpClient();
            foreach (string protocol in new[] { "Responses", "ChatCompletions" })
            {
                var wire = Wire(protocol);
                var response = HarnessTests.Call("echo", "{\"text\":\"same\"}"); response.Message.Text = new string('x', 1000);
                var frame = wire.WriteResponse(new RunState { LastResponse = response, Usage = response.Usage, Status = RunStatus.WaitingForTools }, "test");
                var assistant = wire.ReadResponse(JsonSerializer.SerializeToElement(frame)).Message;
                var request = HarnessTests.Request(); request.Messages.Add(assistant);
                request.Messages.Add(new Message { Role = "tool", ToolCallId = "call_1", Text = "same" });
                var options = new ModelOptions { Model = "test", Protocol = protocol };
                IModelRequestSizer sizer = protocol == "Responses" ? new ResponsesModelProvider(http, options) : new ChatCompletionsModelProvider(http, options);
                var limits = new HarnessOptions { SystemInstructions = "" };
                var state = new RunState { Request = request };
                var view = new ContextBuilder(limits, sizer).Build(state, Array.Empty<ToolDefinition>());
                int wireLength = wire.WriteRequest(view).ToJsonString().Length;
                Check.Equal(wireLength, sizer.MeasureCharacters(view));
                limits.MaxContextCharacters = wireLength;
                new ContextBuilder(limits, sizer).Build(state, Array.Empty<ToolDefinition>());
                if (protocol == "Responses")
                {
                    assistant.Text += new string('y', 5000); // Storage projection is not replayed.
                    new ContextBuilder(limits, sizer).Build(state, Array.Empty<ToolDefinition>());
                }
                limits.MaxContextCharacters--;
                bool rejected = false;
                try { new ContextBuilder(limits, sizer).Build(state, Array.Empty<ToolDefinition>()); } catch (ContextLimitException) { rejected = true; }
                Check.True(rejected, "Actual outgoing view above capacity must stop.");
            }
            string root = Path.Combine(Path.GetTempPath(), "miniharness-final-sizing-" + Guid.NewGuid().ToString("N"));
            try
            {
                var model = new SizingModel();
                await using var runtime = new TaskRuntime(model, new FileTaskStore(root), new HarnessOptions { MaxTotalTokens = 9 },
                    new ModelBinding { Model = "test" });
                await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, default);
                Check.Equal(9, model.MeasuredOutputTokens);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private sealed class SizingModel : IModelProvider, IModelRequestSizer
        {
            public int MeasuredOutputTokens { get; private set; }
            public int MeasureCharacters(ModelRequest request)
            { MeasuredOutputTokens = request.Options.MaxOutputTokens; return JsonSerializer.Serialize(request).Length; }
            public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
            { Check.Equal(MeasuredOutputTokens, request.Options.MaxOutputTokens); return Task.FromResult(HarnessTests.Answer()); }
        }
    }
}
