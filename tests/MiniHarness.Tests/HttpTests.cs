using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MiniHarness.Abstractions;
using MiniHarness.Protocols.OpenAI;

namespace MiniHarness.Tests
{
    internal static class HttpTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("real HTTP: both entry protocols with Responses upstream", () => Matrix("Responses")));
            tests.Add(("real HTTP: both entry protocols with Chat Completions upstream", () => Matrix("ChatCompletions")));
        }

        private static async Task Matrix(string upstreamProtocol)
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-http-" + Guid.NewGuid().ToString("N"));
            var seen = new ConcurrentQueue<JsonElement>();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var upstream = builder.Build();
            IOpenAiProtocol wire = upstreamProtocol == "Responses" ? new ResponsesProtocol() : new ChatCompletionsProtocol();
            upstream.MapPost("/v1/" + wire.Path, async (HttpContext http) =>
            {
                using var input = await JsonDocument.ParseAsync(http.Request.Body);
                var root = input.RootElement;
                seen.Enqueue(root.Clone());
                Check.Equal("upstream-test-model", root.GetProperty("model").GetString());
                string history = root.GetProperty(upstreamProtocol == "Responses" ? "input" : "messages").GetRawText();
                bool hasResult = upstreamProtocol == "Responses"
                    ? history.Contains("function_call_output", StringComparison.Ordinal)
                    : history.Contains("\"tool\"", StringComparison.Ordinal);
                if (hasResult && upstreamProtocol == "Responses") Check.True(history.Contains("opaque"), "Reasoning continuation was lost.");
                ModelResponse modelResponse;
                if (history.Contains("refuse", StringComparison.Ordinal))
                {
                    modelResponse = HarnessTests.Answer("Cannot assist."); modelResponse.Finish = ModelFinish.Refused;
                }
                else if (history.Contains("external", StringComparison.Ordinal) && !hasResult)
                    modelResponse = HarnessTests.Call("weather", "{\"city\":\"Shanghai\"}");
                else if (history.Contains("calculate", StringComparison.Ordinal) && !hasResult)
                    modelResponse = HarnessTests.Call();
                else modelResponse = HarnessTests.Answer(hasResult ? "5" : "hello 😀");
                var state = new RunState
                {
                    LastResponse = modelResponse, Usage = modelResponse.Usage,
                    Status = modelResponse.Finish == ModelFinish.ToolCalls ? RunStatus.WaitingForTools : RunStatus.Completed
                };
                var output = wire.WriteResponse(state, "upstream-test-model");
                if (upstreamProtocol == "Responses")
                    ((JsonArray)output["output"]!).Insert(0, new JsonObject
                    {
                        ["id"] = "rs_1", ["type"] = "reasoning", ["summary"] = new JsonArray(), ["encrypted_content"] = "opaque"
                    });
                await http.Response.WriteAsJsonAsync(output);
            });
            await upstream.StartAsync();
            await using var app = MiniHarness.Server.Program.Build(new[]
            {
                "--Urls", "http://127.0.0.1:0", "--Model:Protocol", upstreamProtocol,
                "--Model:BaseUrl", Address(upstream) + "/v1/", "--Model:Model", "upstream-test-model",
                "--Server:ApiKey", "test-key", "--Storage:Directory", directory,
                "--Tools:AllowedNames:0", "add", "--Logging:LogLevel:Default", "None"
            });
            try
            {
                await app.StartAsync();
                using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };
                using (var unauthorized = await client.GetAsync("/v1/models")) Check.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
                using (var models = await client.GetAsync("/v1/models")) Check.Equal(HttpStatusCode.OK, models.StatusCode);

                foreach (bool responses in new[] { false, true })
                {
                    string path = responses ? "/v1/responses" : "/v1/chat/completions";
                    var request = Body(responses, "calculate");
                    var result = await PostJson(client, path, request);
                    Check.Equal("5", Text(result, responses));
                    Check.Equal(10L, result["usage"]!["total_tokens"]!.GetValue<long>());
                    var finalInput = seen.Last();
                    string history = finalInput.GetProperty(upstreamProtocol == "Responses" ? "input" : "messages").GetRawText();
                    Check.True(history.Contains("call_1") && history.Contains("Succeeded") || history.Contains("call_1") && history.Contains("\\u0022Status\\u0022:1"),
                        "Successful tool observation missing from model history: " + history);

                    request = Body(responses, "plain"); request["stream"] = true;
                    if (!responses) request["stream_options"] = new JsonObject { ["include_usage"] = true };
                    using var stream = await client.PostAsJsonAsync(path, request);
                    Check.Equal(HttpStatusCode.OK, stream.StatusCode);
                    Check.Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);
                    string events = await stream.Content.ReadAsStringAsync();
                    ValidateStream(events, responses);

                    var externalStream = ClientToolBody(responses); externalStream["stream"] = true;
                    using var externalSse = await client.PostAsJsonAsync(path, externalStream);
                    Check.Equal(HttpStatusCode.OK, externalSse.StatusCode);
                    ValidateToolStream(await externalSse.Content.ReadAsStringAsync(), responses);

                    var refusalRequest = Body(responses, "refuse"); refusalRequest["stream"] = true;
                    using var refusal = await client.PostAsJsonAsync(path, refusalRequest);
                    Check.Equal(HttpStatusCode.OK, refusal.StatusCode);
                    string refusalEvents = await refusal.Content.ReadAsStringAsync();
                    Check.True(refusalEvents.Contains(responses ? "response.refusal.delta" : "refusal"));
                    Check.True(refusalEvents.Contains(responses ? "response.completed" : "[DONE]"));

                    var bad = Body(responses, "plain"); bad["unknown_option"] = true;
                    using var invalid = await client.PostAsJsonAsync(path, bad);
                    Check.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                    var missing = Body(responses, "plain"); missing["model"] = "missing";
                    using var missingModel = await client.PostAsJsonAsync(path, missing);
                    Check.Equal(HttpStatusCode.NotFound, missingModel.StatusCode);
                }

                var first = await PostJson(client, "/v1/responses", Body(true, "plain"));
                string id = first["id"]!.GetValue<string>();
                using (var get = await client.GetAsync("/v1/responses/" + id)) Check.Equal(HttpStatusCode.OK, get.StatusCode);
                var continuation = Body(true, "another plain message"); continuation["previous_response_id"] = id;
                var continued = await PostJson(client, "/v1/responses", continuation);
                Check.True(id != continued["id"]!.GetValue<string>());

                var noStore = Body(true, "plain"); noStore["store"] = false;
                var hidden = await PostJson(client, "/v1/responses", noStore);
                using (var getHidden = await client.GetAsync("/v1/responses/" + hidden["id"]!.GetValue<string>()))
                    Check.Equal(HttpStatusCode.NotFound, getHidden.StatusCode);

                await ExternalToolContinuation(client);
                await ToolDefinitionsContinuation(client, seen, upstreamProtocol);
            }
            finally
            {
                await app.StopAsync(); await upstream.StopAsync();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static async Task ExternalToolContinuation(HttpClient client)
        {
            var request = ClientToolBody(true);
            var response = await PostJson(client, "/v1/responses", request);
            Check.Equal("function_call", response["output"]![0]!["type"]!.GetValue<string>());
            var continuation = new JsonObject
            {
                ["model"] = "mini-agent", ["previous_response_id"] = response["id"]!.DeepClone(),
                ["input"] = new JsonArray(new JsonObject
                {
                    ["type"] = "function_call_output", ["call_id"] = response["output"]![0]!["call_id"]!.DeepClone(), ["output"] = "sunny"
                })
            };
            var next = await PostJson(client, "/v1/responses", continuation);
            Check.Equal("5", Text(next, true));
            continuation["input"]![0]!["call_id"] = "wrong";
            using var bad = await client.PostAsJsonAsync("/v1/responses", continuation);
            Check.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        private static async Task ToolDefinitionsContinuation(HttpClient client, ConcurrentQueue<JsonElement> seen, string upstreamProtocol)
        {
            var seed = ClientToolBody(true); seed["input"] = "plain";
            var first = await PostJson(client, "/v1/responses", seed);
            foreach (string selection in new[] { "omitted", "empty", "replacement" })
            {
                var next = Body(true, "plain follow-up"); next["previous_response_id"] = first["id"]!.DeepClone();
                if (selection == "empty") next["tools"] = new JsonArray();
                if (selection == "replacement")
                {
                    var replacement = seed["tools"]![0]!.DeepClone(); replacement["name"] = "calendar";
                    next["tools"] = new JsonArray(replacement);
                }
                var continued = await PostJson(client, "/v1/responses", next);
                string[] names = UpstreamToolNames(seen.Last(), upstreamProtocol);
                Check.Equal(selection == "omitted", names.Contains("weather"));
                Check.Equal(selection == "replacement", names.Contains("calendar"));
                Check.True(names.Contains("add") && names.Contains("echo") && names.Contains("utc_time"), "Server tools must remain available.");
                if (selection == "empty")
                {
                    var later = Body(true, "plain again"); later["previous_response_id"] = continued["id"]!.DeepClone();
                    await PostJson(client, "/v1/responses", later);
                    Check.True(!UpstreamToolNames(seen.Last(), upstreamProtocol).Contains("weather"), "Cleared tools reappeared after checkpoint continuation.");
                }
            }
        }

        private static string[] UpstreamToolNames(JsonElement request, string protocol)
            => request.GetProperty("tools").EnumerateArray()
                .Select(t => (protocol == "Responses" ? t : t.GetProperty("function")).GetProperty("name").GetString()!).ToArray();

        private static JsonObject ClientToolBody(bool responses)
        {
            var request = Body(responses, "external tool request");
            var function = new JsonObject
            {
                ["name"] = "weather", ["description"] = "client weather",
                ["parameters"] = JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"],\"additionalProperties\":false}"),
                ["strict"] = true
            };
            if (responses) { function["type"] = "function"; request["tools"] = new JsonArray(function); }
            else request["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["function"] = function });
            return request;
        }

        private static void ValidateToolStream(string events, bool responses)
        {
            var arguments = new StringBuilder();
            bool foundName = false;
            foreach (string line in events.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)))
            {
                string payload = line.Substring(6);
                if (payload == "[DONE]") continue;
                using var json = JsonDocument.Parse(payload);
                var root = json.RootElement;
                if (responses)
                {
                    string? type = root.GetProperty("type").GetString();
                    if (type == "response.output_item.added")
                        foundName |= root.GetProperty("item").GetProperty("name").GetString() == "weather";
                    if (type == "response.function_call_arguments.delta") arguments.Append(root.GetProperty("delta").GetString());
                }
                else foreach (var choice in root.GetProperty("choices").EnumerateArray())
                {
                    if (!choice.GetProperty("delta").TryGetProperty("tool_calls", out var calls)) continue;
                    foreach (var call in calls.EnumerateArray())
                    {
                        var function = call.GetProperty("function");
                        if (function.TryGetProperty("name", out var name)) foundName |= name.GetString() == "weather";
                        if (function.TryGetProperty("arguments", out var chunk)) arguments.Append(chunk.GetString());
                    }
                }
            }
            Check.True(foundName);
            using var decoded = JsonDocument.Parse(arguments.ToString());
            Check.Equal("Shanghai", decoded.RootElement.GetProperty("city").GetString());
        }

        private static void ValidateStream(string events, bool responses)
        {
            var data = events.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
                .Select(line => line.Substring(6)).ToList();
            Check.True(data.Count > 2);
            if (responses)
            {
                int expected = 0;
                foreach (string payload in data)
                {
                    using var json = JsonDocument.Parse(payload);
                    Check.Equal(expected++, json.RootElement.GetProperty("sequence_number").GetInt32());
                }
                Check.True(events.Contains("event: response.completed"));
                Check.True(events.Contains("response.output_text.delta") && events.Contains("response.output_item.done"));
            }
            else
            {
                Check.Equal("[DONE]", data.Last());
                using var usage = JsonDocument.Parse(data[data.Count - 2]);
                Check.Equal(0, usage.RootElement.GetProperty("choices").GetArrayLength());
                Check.Equal(5L, usage.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64());
            }
            var text = new StringBuilder();
            foreach (string payload in data.Where(p => p != "[DONE]"))
            {
                using var json = JsonDocument.Parse(payload);
                if (responses && json.RootElement.GetProperty("type").GetString() == "response.output_text.delta")
                    text.Append(json.RootElement.GetProperty("delta").GetString());
                if (!responses)
                    foreach (var choice in json.RootElement.GetProperty("choices").EnumerateArray())
                        if (choice.GetProperty("delta").TryGetProperty("content", out var content)) text.Append(content.GetString());
            }
            Check.Equal("hello 😀", text.ToString());
        }

        internal static JsonObject Body(bool responses, string text)
        {
            var body = new JsonObject { ["model"] = "mini-agent" };
            if (responses) body["input"] = text;
            else body["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text });
            return body;
        }
        private static string Text(JsonObject result, bool responses)
            => responses ? result["output"]![0]!["content"]![0]!["text"]!.GetValue<string>()
                : result["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        internal static string Address(WebApplication app)
            => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
        internal static async Task<JsonObject> PostJson(HttpClient client, string path, JsonObject request)
        {
            using var response = await client.PostAsJsonAsync(path, request);
            string body = await response.Content.ReadAsStringAsync();
            Check.True(response.StatusCode == HttpStatusCode.OK, "HTTP " + (int)response.StatusCode + ": " + body);
            return (JsonObject)JsonNode.Parse(body)!;
        }
    }
}
