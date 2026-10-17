using System;
using System.Collections.Concurrent;
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
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new ConcurrentQueue<JsonElement>();
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
            var app = MiniHarness.Server.Program.Build(args);
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
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                long cursor;
                using (var observation = await client.GetAsync("/agent/tasks/" + taskId + "/events?follow=true", HttpCompletionOption.ResponseHeadersRead))
                {
                    using var reader = new StreamReader(await observation.Content.ReadAsStreamAsync());
                    string? first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
                app = MiniHarness.Server.Program.Build(args); await app.StartAsync();
                using var afterRestart = new HttpClient { BaseAddress = new Uri(HttpTests.Address(app)) };
                afterRestart.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
                var continued = await HttpTests.PostJson(afterRestart, "/agent/tasks/commands", NativeCommand("Input", taskId, "plain after restart"));
                Check.Equal(taskId, continued["state"]!["id"]!.GetValue<string>());
                Check.Equal(4, continued["state"]!["budget"]!["modelCalls"]!.GetValue<int>());
                Check.Equal(20L, continued["state"]!["budget"]!["usage"]!["totalTokens"]!.GetValue<long>());
                var replayed = (JsonArray)JsonNode.Parse(await afterRestart.GetStringAsync("/agent/tasks/" + taskId + "/events?after=" + previousSequence))!;
                Check.True(replayed.Count > 0 && replayed.All(e => e!["sequence"]!.GetValue<long>() > previousSequence));
            }
            finally
            {
                release.TrySetResult(true); await app.StopAsync(); await app.DisposeAsync(); await upstream.StopAsync();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
        private static JsonObject NativeCommand(string kind, string? taskId, string text) => new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["kind"] = kind, ["taskId"] = taskId,
            ["request"] = new JsonObject { ["model"] = "mini-agent", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = text }) }
        };
        private static async Task<JsonObject> ReadTask(HttpClient client, string id) => (JsonObject)JsonNode.Parse(await client.GetStringAsync("/agent/tasks/" + id))!;
        private static async Task<JsonObject> WaitForTurn(HttpClient client, string id, int turns)
        {
            for (int i = 0; i < 100; i++)
            {
                var state = await ReadTask(client, id);
                if (state["turns"]!.AsArray().Count == turns && state["turns"]![turns - 1]!["status"]!.GetValue<string>() == "Completed") return state;
                if (state["status"]!.GetValue<string>() == "Failed") throw new Exception(state.ToJsonString());
                await Task.Delay(50);
            }
            throw new Exception("Task did not finish the expected turn.");
        }
    }
}
