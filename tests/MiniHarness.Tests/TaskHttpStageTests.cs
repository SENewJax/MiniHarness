using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
using MiniHarness.Protocols.OpenAI;

namespace MiniHarness.Tests
{
    internal static partial class TaskStageTests
    {
        private static async Task TaskHttpScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-stage6-" + Guid.NewGuid().ToString("N"));
            var scenarioClock = Stopwatch.StartNew();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new ConcurrentQueue<JsonElement>();
            var serverErrors = new ServerErrors();
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var upstream = builder.Build();
            upstream.MapPost("/v1/responses", async (HttpContext http) =>
            {
                using var json = await JsonDocument.ParseAsync(http.Request.Body);
                requests.Enqueue(json.RootElement.Clone());
                string history = json.RootElement.GetProperty("input").GetRawText();
                if (requests.Count == 1) { entered.TrySetResult(true); await release.Task.WaitAsync(http.RequestAborted); }
                var result = history.Contains("function_call_output", StringComparison.Ordinal) || history.Contains("plain", StringComparison.Ordinal)
                    ? HarnessTests.Answer("done") : HarnessTests.Call();
                var frame = new ResponsesProtocol().WriteResponse(new RunState
                {
                    LastResponse = result, Usage = result.Usage,
                    Status = result.Message.ToolCalls.Count > 0 ? RunStatus.WaitingForTools : RunStatus.Completed
                }, "upstream-test-model");
                ((JsonArray)frame["output"]!).Insert(0, JsonNode.Parse("{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[],\"encrypted_content\":\"opaque\"}"));
                await http.Response.WriteAsJsonAsync(frame);
            });
            await upstream.StartAsync();
            string[] args =
            {
                "--Urls", "http://127.0.0.1:0", "--Model:Protocol", "Responses", "--Model:BaseUrl", HttpTests.Address(upstream) + "/v1/",
                "--Model:Model", "upstream-test-model", "--Server:ApiKey", "test-key", "--Storage:Directory", directory,
                "--Tools:AllowedNames:0", "add", "--Harness:MaxModelCalls", "4", "--Logging:LogLevel:Default", "None"
            };
            WebApplication BuildServer() => MiniHarness.Server.Program.Build(args, host =>
            {
                host.Logging.AddProvider(serverErrors);
                host.Logging.AddFilter<ServerErrors>((category, level) => level >= LogLevel.Warning);
            });
            var app = BuildServer();
            try
            {
                await app.StartAsync();
                using var client = new HttpClient { BaseAddress = new Uri(HttpTests.Address(app)) };
                using (var denied = await client.GetAsync("/agent/tasks/task_" + new string('a', 32))) Check.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
                var start = NativeCommand("Start", null, "calculate");
                using var accepted = await client.PostAsJsonAsync("/agent/tasks/commands?wait=false", start);
                Check.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                var submission = JsonNode.Parse(await accepted.Content.ReadAsStringAsync())!;
                string taskId = submission["taskId"]!.GetValue<string>();
                await AwaitStage(entered.Task, TimeSpan.FromSeconds(10), "first upstream request",
                    () => "upstreamRequests=" + requests.Count + ", elapsed=" + scenarioClock.ElapsedMilliseconds + "ms");
                long cursor;
                using (var observation = await client.GetAsync("/agent/tasks/" + taskId + "/events?follow=true", HttpCompletionOption.ResponseHeadersRead))
                {
                    using var reader = new StreamReader(await observation.Content.ReadAsStreamAsync());
                    string? first = await AwaitStage(reader.ReadLineAsync(), TimeSpan.FromSeconds(10),
                        "first SSE event frame",
                        () => "upstreamRequests=" + requests.Count + ", elapsed=" + scenarioClock.ElapsedMilliseconds + "ms");
                    Check.True(first!.StartsWith("id: ", StringComparison.Ordinal)); cursor = long.Parse(first.Substring(4));
                }
                var running = await ReadTask(client, taskId);
                Check.Equal("Running", running["status"]!.GetValue<string>());
                release.TrySetResult(true);
                var firstDone = await WaitForTurn(client, taskId, 1);
                Check.Equal(2, firstDone["budget"]!["modelCalls"]!.GetValue<int>());
                string responseId = firstDone["turns"]![0]!["responseId"]!.GetValue<string>();
                var tail = (JsonArray)JsonNode.Parse(await client.GetStringAsync("/agent/tasks/" + taskId + "/events?after=" + cursor))!;
                Check.True(tail.Count > 0 && tail.All(e => e!["sequence"]!.GetValue<long>() > cursor));
                var input = NativeCommand("Input", taskId, "plain second");
                var second = await HttpTests.PostJson(client, "/agent/tasks/commands", input);
                var duplicate = await HttpTests.PostJson(client, "/agent/tasks/commands", input);
                Check.Equal(second["receipt"]!["turnId"]!.GetValue<string>(), duplicate["receipt"]!["turnId"]!.GetValue<string>());
                Check.Equal(2, ((JsonArray)duplicate["state"]!["turns"]!).Count);
                Check.Equal(3, duplicate["state"]!["budget"]!["modelCalls"]!.GetValue<int>());
                var branch = HttpTests.Body(true, "plain branch"); branch["previous_response_id"] = responseId;
                var response = await HttpTests.PostJson(client, "/v1/responses", branch);
                Check.True(response["id"]!.GetValue<string>() != responseId);
                Check.True(!requests.Last().GetRawText().Contains("plain second", StringComparison.Ordinal));
                Check.Equal(3, (await ReadTask(client, taskId))["budget"]!["modelCalls"]!.GetValue<int>());
                long previousSequence = (await ReadTask(client, taskId))["events"]!.AsArray().Last()!["sequence"]!.GetValue<long>();
                await app.StopAsync(); await app.DisposeAsync();
                app = BuildServer(); await app.StartAsync();
                using var afterRestart = new HttpClient { BaseAddress = new Uri(HttpTests.Address(app)) };
                afterRestart.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
                var continued = await AwaitStage(
                    HttpTests.PostJson(afterRestart, "/agent/tasks/commands", NativeCommand("Input", taskId, "plain after restart")),
                    TimeSpan.FromSeconds(60), "post-restart command continuation",
                    () => "upstreamRequests=" + requests.Count + ", elapsed=" + scenarioClock.ElapsedMilliseconds + "ms");
                Check.Equal(taskId, continued["state"]!["id"]!.GetValue<string>());
                Check.Equal(4, continued["state"]!["budget"]!["modelCalls"]!.GetValue<int>());
                Check.Equal(20L, continued["state"]!["budget"]!["usage"]!["totalTokens"]!.GetValue<long>());
                var replayed = (JsonArray)JsonNode.Parse(await afterRestart.GetStringAsync("/agent/tasks/" + taskId + "/events?after=" + previousSequence))!;
                Check.True(replayed.Count > 0 && replayed.All(e => e!["sequence"]!.GetValue<long>() > previousSequence));
            }
            catch (Exception ex)
            {
                try
                {
                string diagnostic = Path.Combine(MiniHarness.Testing.ValidationPaths.Root, "http-diagnostics",
                    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(diagnostic)!);
                var checkpoints = Directory.Exists(directory) ? Directory.GetFiles(directory, "task_*.json")
                    .Select(path => new { path, content = File.ReadAllText(path) }).ToArray() : Array.Empty<object>();
                await File.WriteAllTextAsync(diagnostic, JsonSerializer.Serialize(new
                {
                    capturedAtUtc = DateTimeOffset.UtcNow, elapsedMs = scenarioClock.ElapsedMilliseconds,
                    error = ex.ToString(), serverErrors = serverErrors.Messages.ToArray(), upstreamRequests = requests.ToArray(), checkpoints,
                    threadPoolThreads = ThreadPool.ThreadCount, pendingWork = ThreadPool.PendingWorkItemCount
                }, TaskJson.Options));
                Console.WriteLine("HTTP failure diagnostics: " + diagnostic);
                }
                catch (Exception diagnosticError) { Console.WriteLine("Could not persist HTTP diagnostics: " + diagnosticError.GetType().Name); }
                throw;
            }
            finally
            {
                release.TrySetResult(true); await app.StopAsync(); await app.DisposeAsync(); await upstream.StopAsync();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
        private sealed class ServerErrors : ILoggerProvider
        {
            public ConcurrentQueue<string> Messages { get; } = new ConcurrentQueue<string>();
            public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
            public void Dispose() { }
            private sealed class Capture : ILogger
            {
                private readonly ServerErrors owner;
                private readonly string category;
                public Capture(ServerErrors owner, string category) { this.owner = owner; this.category = category; }
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
                public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
                {
                    if (IsEnabled(level)) owner.Messages.Enqueue(DateTimeOffset.UtcNow.ToString("o") + " " + category + " " +
                        formatter(state, error) + "\n" + error);
                }
            }
        }
        private static JsonObject NativeCommand(string kind, string? taskId, string text) => new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["kind"] = kind, ["taskId"] = taskId,
            ["request"] = new JsonObject { ["model"] = "mini-agent", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = text }) }
        };
        private static async Task<T> AwaitStage<T>(Task<T> stage, TimeSpan timeout, string stageName, Func<string> context)
        {
            var sw = Stopwatch.StartNew();
            try { return await stage.WaitAsync(timeout); }
            catch (TimeoutException)
            {
                // Preserve a diagnosable record instead of a bare timed-out: which
                // phase missed its deadline, how long, and the observable state.
                throw new Exception("HTTP stage timed out: " + stageName + " after " + sw.ElapsedMilliseconds
                    + "ms (deadline " + (int)timeout.TotalSeconds + "s); " + context());
            }
        }
        private static async Task AwaitStage(Task stage, TimeSpan timeout, string stageName, Func<string> context)
        {
            var sw = Stopwatch.StartNew();
            try { await stage.WaitAsync(timeout); }
            catch (TimeoutException)
            {
                throw new Exception("HTTP stage timed out: " + stageName + " after " + sw.ElapsedMilliseconds
                    + "ms (deadline " + (int)timeout.TotalSeconds + "s); " + context());
            }
        }
        private static async Task<JsonObject> ReadTask(HttpClient client, string id) => (JsonObject)JsonNode.Parse(await client.GetStringAsync("/agent/tasks/" + id))!;
        private static async Task<JsonObject> WaitForTurn(HttpClient client, string id, int turns)
        {
            // The turn runs two in-process upstream round-trips; on a cold JIT the
            // hosted server's first startup/DI path can take several seconds. Poll by
            // condition with a generous deadline instead of a wall-clock budget that
            // depends on machine warm-up. The expected turn returns immediately.
            JsonObject state = null!;
            for (int i = 0; i < 600; i++)
            {
                state = await ReadTask(client, id);
                if (state["turns"]!.AsArray().Count == turns && state["turns"]![turns - 1]!["status"]!.GetValue<string>() == "Completed") return state;
                if (state["status"]!.GetValue<string>() == "Failed") throw new Exception(state.ToJsonString());
                await Task.Delay(50);
            }
            throw new Exception("Task did not finish the expected turn. Final state: " + state.ToJsonString());
        }
    }
}
