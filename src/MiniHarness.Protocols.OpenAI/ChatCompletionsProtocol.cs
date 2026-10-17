using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using MiniHarness.Abstractions;

namespace MiniHarness.Protocols.OpenAI
{
    public sealed class ChatCompletionsProtocol : IOpenAiProtocol
    {
        public string Path => "chat/completions";

        public ApiRequest ReadRequest(JsonElement json)
        {
            WireJson.CheckFields(json, "model", "messages", "tools", "tool_choice", "temperature", "max_tokens",
                "max_completion_tokens", "stream", "stream_options", "n");
            if (WireJson.PositiveInt(json, "n", 1) != 1) throw new ProtocolException("Only n=1 is supported.");
            var api = new ApiRequest { Stream = WireJson.Boolean(json, "stream"), ToolsSpecified = json.TryGetProperty("tools", out _) };
            if (json.TryGetProperty("stream_options", out var streamOptions))
            {
                WireJson.CheckFields(streamOptions, "include_usage");
                api.IncludeUsage = WireJson.Boolean(streamOptions, "include_usage");
            }
            WireJson.ReadOptions(json, api.ModelRequest, false);
            var messages = json.GetProperty("messages");
            if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
                throw new ProtocolException("messages must be a non-empty array.");
            foreach (var item in messages.EnumerateArray()) api.ModelRequest.Messages.Add(ReadMessage(item));
            return api;
        }

        private static Message ReadMessage(JsonElement item)
        {
            WireJson.CheckFields(item, "role", "content", "tool_calls", "tool_call_id");
            string role = WireJson.String(item, "role");
            if (role != "system" && role != "developer" && role != "user" && role != "assistant" && role != "tool")
                throw new ProtocolException("Unsupported role: " + role);
            var message = new Message { Role = role };
            if (item.TryGetProperty("content", out var content)) message.Text = WireJson.Text(content, "text");
            if (role == "tool") message.ToolCallId = WireJson.String(item, "tool_call_id");
            if (item.TryGetProperty("tool_calls", out var calls))
            {
                if (role != "assistant" || calls.ValueKind != JsonValueKind.Array)
                    throw new ProtocolException("tool_calls require an assistant message and an array.");
                foreach (var call in calls.EnumerateArray())
                {
                    if (WireJson.String(call, "type") != "function") throw new ProtocolException("Only function calls are supported.");
                    var function = call.GetProperty("function");
                    message.ToolCalls.Add(new ToolCall
                    {
                        Id = WireJson.String(call, "id"), Name = WireJson.String(function, "name"),
                        Arguments = WireJson.String(function, "arguments")
                    });
                }
            }
            return message;
        }

        public JsonObject WriteRequest(ModelRequest request)
        {
            var messages = new JsonArray();
            foreach (var message in request.Messages) messages.Add(WriteMessage(message));
            var root = new JsonObject { ["messages"] = messages };
            WireJson.WriteOptions(root, request, false);
            return root;
        }

        internal static JsonObject WriteMessage(Message message)
        {
            if (message.ProviderItems.Count > 0)
                throw new ProtocolException("Responses provider items cannot be translated losslessly to Chat Completions.");
            var item = new JsonObject { ["role"] = message.Role, ["content"] = message.Text };
            if (message.Role == "tool") item["tool_call_id"] = message.ToolCallId;
            if (message.ToolCalls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["id"] = call.Id, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments }
                    });
                item["tool_calls"] = calls;
                if (message.Text.Length == 0) item["content"] = null;
            }
            return item;
        }

        public ModelResponse ReadResponse(JsonElement json)
        {
            var choices = json.GetProperty("choices");
            if (choices.GetArrayLength() != 1) throw new ModelException("Expected exactly one model choice.");
            var choice = choices[0];
            var wireMessage = choice.GetProperty("message");
            // Provider response messages may contain extra server metadata.
            var message = new Message { Role = "assistant" };
            if (wireMessage.TryGetProperty("content", out var content)) message.Text = WireJson.Text(content, "text");
            if (wireMessage.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null)
                foreach (var call in calls.EnumerateArray())
                {
                    if (WireJson.String(call, "type") != "function") throw new ModelException("Unsupported upstream tool call.");
                    var function = call.GetProperty("function");
                    message.ToolCalls.Add(new ToolCall
                    {
                        Id = WireJson.String(call, "id"), Name = WireJson.String(function, "name"),
                        Arguments = WireJson.String(function, "arguments")
                    });
                }
            string finish = WireJson.String(choice, "finish_reason");
            ModelFinish result;
            switch (finish)
            {
                case "stop": result = ModelFinish.Stop; break;
                case "tool_calls": result = ModelFinish.ToolCalls; break;
                case "length": result = ModelFinish.Length; break;
                case "content_filter": result = ModelFinish.ContentFiltered; break;
                default: throw new ModelException("Unsupported upstream finish reason: " + finish);
            }
            if (wireMessage.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String)
            {
                result = ModelFinish.Refused;
                message.Text = refusal.GetString()!;
            }
            if ((result == ModelFinish.ToolCalls) != (message.ToolCalls.Count > 0) && result != ModelFinish.Length)
                throw new ModelException("Tool calls and upstream finish reason disagree.");
            return new ModelResponse { Message = message, Finish = result, Usage = WireJson.Usage(json, false) };
        }

        public JsonObject WriteResponse(RunState state, string publicModel)
        {
            var message = state.LastResponse?.Message ?? new Message { Role = "assistant" };
            // Reasoning continuation stays private to the model adapter and checkpoint.
            var visible = new Message { Role = "assistant", Text = message.Text };
            if (state.Status == RunStatus.WaitingForTools) visible.ToolCalls.AddRange(message.ToolCalls);
            var wireMessage = WriteMessage(visible);
            if (state.LastResponse?.Finish == ModelFinish.Refused)
            {
                wireMessage["content"] = null;
                wireMessage["refusal"] = message.Text;
            }
            return new JsonObject
            {
                ["id"] = "chatcmpl_" + state.Id.Substring(5), ["object"] = "chat.completion",
                ["created"] = state.CreatedAt, ["model"] = publicModel,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0, ["message"] = wireMessage, ["finish_reason"] = WireJson.Finish(state), ["logprobs"] = null
                }),
                ["usage"] = WireJson.Usage(state.Usage, false)
            };
        }
    }
}
