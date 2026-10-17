using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Model.OpenAI;
using MiniHarness.Harness;
using MiniHarness.Protocols.OpenAI;

namespace MiniHarness.Tests
{
    internal static class ProtocolTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("both codecs encode schemas/call IDs and parse usage", Codecs));
            tests.Add(("Responses retains encrypted reasoning for subsequent requests", Reasoning));
            tests.Add(("truncated tool arguments never become executable output", Truncated));
            tests.Add(("HTTP transient errors retry but authentication errors do not", Retries));
            tests.Add(("HTTP cancellation propagates and malformed upstream data fails", TransportErrors));
            tests.Add(("refusal and moderation filtering preserve distinct protocol semantics", Refusals));
            tests.Add(("retrieving a response preserves function item IDs", StableItemIdentity));
            tests.Add(("Responses mixed output survives checkpoint replay in exact order", OrderedItems));
            tests.Add(("legacy supplemental provider items still retain text and calls", LegacyItems));
        }

        private static Task Codecs()
        {
            IOpenAiProtocol[] protocols = { new ChatCompletionsProtocol(), new ResponsesProtocol() };
            foreach (var protocol in protocols)
            {
                var request = HarnessTests.Request();
                request.Tools.Add(new MiniHarness.Server.AddTool().Definition);
                request.Messages.Add(HarnessTests.Call().Message);
                request.Messages.Add(new Message { Role = "tool", ToolCallId = "call_1", Text = "5" });
                string body = protocol.WriteRequest(request).ToJsonString();
                Check.True(body.Contains("call_1") && body.Contains("parameters") && body.Contains("function"));
                var wire = protocol.WriteResponse(new RunState { Request = request, LastResponse = HarnessTests.Call(), Status = RunStatus.WaitingForTools }, "test");
                // Model-facing usage is one step; public response usage can be cumulative.
                using var json = JsonDocument.Parse(wire.ToJsonString());
                var result = protocol.ReadResponse(json.RootElement);
                Check.Equal("call_1", result.Message.ToolCalls[0].Id);
                Check.Equal(ModelFinish.ToolCalls, result.Finish);
                Check.True(result.Usage.Known);
            }
            return Task.CompletedTask;
        }

        private static Task Reasoning()
        {
            const string fixture = "{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[],\"encrypted_content\":\"opaque\"},{\"type\":\"function_call\",\"call_id\":\"c1\",\"name\":\"add\",\"arguments\":\"{}\"}],\"usage\":{\"input_tokens\":2,\"output_tokens\":3}}";
            var protocol = new ResponsesProtocol();
            using var json = JsonDocument.Parse(fixture);
            var response = protocol.ReadResponse(json.RootElement);
            var request = HarnessTests.Request(); request.Messages.Add(response.Message);
            string body = protocol.WriteRequest(request).ToJsonString();
            Check.True(body.Contains("encrypted_content") && body.Contains("opaque"));
            bool rejected = false;
            try { new ChatCompletionsProtocol().WriteRequest(request); }
            catch (ProtocolException) { rejected = true; }
            Check.True(rejected, "Lossy reasoning conversion should be rejected.");
            return Task.CompletedTask;
        }

        private static Task Truncated()
        {
            using var chat = JsonDocument.Parse("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"c\",\"type\":\"function\",\"function\":{\"name\":\"add\",\"arguments\":\"{\"}}]}}]}");
            Check.Equal(ModelFinish.Length, new ChatCompletionsProtocol().ReadResponse(chat.RootElement).Finish);
            using var responses = JsonDocument.Parse("{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"output\":[{\"type\":\"function_call\",\"call_id\":\"c\",\"name\":\"add\",\"arguments\":\"{\"}]}");
            Check.Equal(ModelFinish.Length, new ResponsesProtocol().ReadResponse(responses.RootElement).Finish);
            return Task.CompletedTask;
        }

        private static Task Refusals()
        {
            foreach (IOpenAiProtocol protocol in new IOpenAiProtocol[] { new ChatCompletionsProtocol(), new ResponsesProtocol() })
            {
                var response = HarnessTests.Answer("Cannot assist with that."); response.Finish = ModelFinish.Refused;
                var state = new RunState { LastResponse = response, Status = RunStatus.WaitingForUser };
                using var json = JsonDocument.Parse(protocol.WriteResponse(state, "test").ToJsonString());
                var parsed = protocol.ReadResponse(json.RootElement);
                Check.Equal(ModelFinish.Refused, parsed.Finish);
                Check.Equal(response.Message.Text, parsed.Message.Text);
                if (protocol is ChatCompletionsProtocol)
                    Check.Equal("stop", json.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
                else Check.Equal("completed", json.RootElement.GetProperty("status").GetString());
                response.Finish = ModelFinish.ContentFiltered;
                using var filtered = JsonDocument.Parse(protocol.WriteResponse(state, "test").ToJsonString());
                Check.Equal(ModelFinish.ContentFiltered, protocol.ReadResponse(filtered.RootElement).Finish);
            }
            return Task.CompletedTask;
        }

        private static Task StableItemIdentity()
        {
            var state = new RunState { LastResponse = HarnessTests.Call(), Status = RunStatus.WaitingForTools };
            var protocol = new ResponsesProtocol();
            Check.Equal(protocol.WriteResponse(state, "test").ToJsonString(), protocol.WriteResponse(state, "test").ToJsonString());
            return Task.CompletedTask;
        }

        private static async Task OrderedItems()
        {
            var original = new JsonArray(
                JsonNode.Parse("{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[],\"encrypted_content\":\"opaque_1\"}"),
                JsonNode.Parse("{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"c1\",\"name\":\"add\",\"arguments\":\"{}\",\"status\":\"completed\"}"),
                JsonNode.Parse("{\"type\":\"message\",\"id\":\"m1\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"first\",\"annotations\":[]}],\"status\":\"completed\"}"),
                JsonNode.Parse("{\"type\":\"reasoning\",\"id\":\"rs_2\",\"summary\":[],\"encrypted_content\":\"opaque_2\"}"),
                JsonNode.Parse("{\"type\":\"function_call\",\"id\":\"fc_2\",\"call_id\":\"c2\",\"name\":\"add\",\"arguments\":\"{}\",\"status\":\"completed\"}"),
                JsonNode.Parse("{\"type\":\"message\",\"id\":\"m2\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"second\",\"annotations\":[]}],\"status\":\"completed\"}"));
            var protocol = new ResponsesProtocol();
            ModelResponse response;
            using (var json = JsonDocument.Parse(new JsonObject { ["status"] = "completed", ["output"] = original.DeepClone() }.ToJsonString()))
                response = protocol.ReadResponse(json.RootElement);
            Check.Equal("firstsecond", response.Message.Text); Check.Equal(2, response.Message.ToolCalls.Count);
            var request = HarnessTests.Request(); request.Messages.Add(response.Message);
            foreach (var call in response.Message.ToolCalls)
                request.Messages.Add(new Message { Role = "tool", ToolCallId = call.Id, Text = "5" });
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-order-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileRunStore(directory); var state = new RunState { Request = request, LastResponse = response };
                await store.SaveAsync(state, CancellationToken.None);
                var loaded = await store.LoadAsync(state.Id, CancellationToken.None);
                var input = (JsonArray)protocol.WriteRequest(loaded!.Request)["input"]!;
                Check.Equal(1 + original.Count + 2, input.Count);
                for (int i = 0; i < original.Count; i++)
                    Check.True(JsonNode.DeepEquals(original[i], input[i + 1]), "Provider output item moved or changed at index " + i);
                Check.Equal("function_call_output", input[original.Count + 1]!["type"]!.GetValue<string>());
                Check.Equal("c1", input[original.Count + 1]!["call_id"]!.GetValue<string>());
            }
            finally { Directory.Delete(directory, true); }
        }

        private static Task LegacyItems()
        {
            // This shape predates the complete ordered provider snapshot marker.
            using var json = JsonDocument.Parse("{\"type\":\"reasoning\",\"id\":\"old_rs\",\"summary\":[]}");
            var message = HarnessTests.Call().Message; message.Text = "legacy text";
            message.ProviderItems.Add(json.RootElement.Clone());
            var request = HarnessTests.Request(); request.Messages.Add(message);
            var input = (JsonArray)new ResponsesProtocol().WriteRequest(request)["input"]!;
            Check.Equal(4, input.Count);
            Check.Equal("reasoning", input[1]!["type"]!.GetValue<string>());
            Check.Equal("legacy text", input[2]!["content"]![0]!["text"]!.GetValue<string>());
            Check.Equal("call_1", input[3]!["call_id"]!.GetValue<string>());
            return Task.CompletedTask;
        }

        private static async Task Retries()
        {
            var handler = new TestHandler((index, _) =>
            {
                if (index == 1)
                {
                    var transient = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    transient.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                    return Task.FromResult(transient);
                }
                return Task.FromResult(Success());
            });
            using var client = new HttpClient(handler);
            var provider = new ChatCompletionsModelProvider(client, new ModelOptions { MaxRetries = 2 });
            var result = await provider.GenerateAsync(HarnessTests.Request(), CancellationToken.None);
            Check.Equal("ok", result.Message.Text); Check.Equal(2, handler.Calls);
            var denied = new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            using var deniedClient = new HttpClient(denied);
            await Check.ThrowsAsync<ModelException>(() => new ChatCompletionsModelProvider(deniedClient, new ModelOptions())
                .GenerateAsync(HarnessTests.Request(), CancellationToken.None));
            Check.Equal(1, denied.Calls);
        }

        private static async Task TransportErrors()
        {
            var waiting = new TestHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Success(); });
            using var client = new HttpClient(waiting);
            using var cancel = new CancellationTokenSource(30);
            await Check.ThrowsAsync<OperationCanceledException>(() => new ChatCompletionsModelProvider(client, new ModelOptions())
                .GenerateAsync(HarnessTests.Request(), cancel.Token));
            var malformed = new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[]}") }));
            using var badClient = new HttpClient(malformed);
            await Check.ThrowsAsync<ModelException>(() => new ChatCompletionsModelProvider(badClient, new ModelOptions())
                .GenerateAsync(HarnessTests.Request(), CancellationToken.None));
        }

        private static HttpResponseMessage Success() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}")
        };
        private sealed class TestHandler : HttpMessageHandler
        {
            private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> respond;
            public int Calls { get; private set; }
            public TestHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) { this.respond = respond; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => respond(++Calls, cancellationToken);
        }
    }
}
