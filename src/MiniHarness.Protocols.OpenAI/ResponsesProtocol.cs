using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using MiniHarness.Abstractions;

namespace MiniHarness.Protocols.OpenAI
{
    public sealed class ResponsesProtocol : IOpenAiProtocol
    {
        public string Path => "responses";

        public ApiRequest ReadRequest(JsonElement json)
        {
            WireJson.CheckFields(json, "model", "input", "instructions", "tools", "tool_choice", "temperature",
                "max_output_tokens", "stream", "store", "previous_response_id");
            var api = new ApiRequest
            {
                Stream = WireJson.Boolean(json, "stream"), Store = WireJson.Boolean(json, "store", true),
                ToolsSpecified = json.TryGetProperty("tools", out _),
                PreviousResponseId = json.TryGetProperty("previous_response_id", out var previous) && previous.ValueKind != JsonValueKind.Null
                    ? WireJson.String(json, "previous_response_id") : null
            };
            WireJson.ReadOptions(json, api.ModelRequest, true);
            if (json.TryGetProperty("instructions", out var instructions) && instructions.ValueKind != JsonValueKind.Null)
                api.ModelRequest.Instructions = WireJson.String(json, "instructions");
            var input = json.GetProperty("input");
            if (input.ValueKind == JsonValueKind.String)
                api.ModelRequest.Messages.Add(new Message { Text = input.GetString()! });
            else if (input.ValueKind == JsonValueKind.Array)
                foreach (var item in input.EnumerateArray()) ReadItem(item, api.ModelRequest);
            else throw new ProtocolException("input must be text or an item array.");
            if (api.ModelRequest.Messages.Count == 0) throw new ProtocolException("input must not be empty.");
            return api;
        }

        private static void ReadItem(JsonElement item, ModelRequest request)
        {
            string type = WireJson.String(item, "type", "message");
            switch (type)
            {
                case "message":
                    string role = WireJson.String(item, "role");
                    if (role != "user" && role != "assistant" && role != "system" && role != "developer")
                        throw new ProtocolException("Unsupported message role.");
                    request.Messages.Add(new Message { Role = role, Text = WireJson.Text(item.GetProperty("content"), "input_text", "output_text") });
                    break;
                case "function_call":
                    request.Messages.Add(new Message
                    {
                        Role = "assistant", ToolCalls = { new ToolCall
                        {
                            Id = WireJson.String(item, "call_id"), Name = WireJson.String(item, "name"),
                            Arguments = WireJson.String(item, "arguments")
                        } }
                    });
                    break;
                case "function_call_output":
                    request.Messages.Add(new Message { Role = "tool", ToolCallId = WireJson.String(item, "call_id"), Text = WireJson.String(item, "output") });
                    break;
                case "reasoning":
                    request.Messages.Add(new Message { Role = "assistant", ProviderItems = { item.Clone() } });
                    break;
                default: throw new ProtocolException("Unsupported input item: " + type);
            }
        }

        public JsonObject WriteRequest(ModelRequest request)
        {
            var input = new JsonArray();
            foreach (var message in request.Messages)
            {
                foreach (var native in message.ProviderItems) input.Add(JsonNode.Parse(native.GetRawText()));
                // Full snapshots already contain text and calls in their original order.
                // Supplemental items in older checkpoints still need the projection below.
                if (message.ProviderItemsAreComplete) continue;
                if (message.Role == "tool")
                    input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = message.ToolCallId, ["output"] = message.Text });
                else
                {
                    if (message.Text.Length > 0)
                        input.Add(new JsonObject
                        {
                            ["role"] = message.Role, ["type"] = "message",
                            ["content"] = new JsonArray(new JsonObject
                            {
                                ["type"] = message.Role == "assistant" ? "output_text" : "input_text", ["text"] = message.Text
                            })
                        });
                    foreach (var call in message.ToolCalls) input.Add(WriteCall(call));
                }
            }
            var root = new JsonObject
            {
                ["input"] = input, ["store"] = false,
                ["include"] = new JsonArray("reasoning.encrypted_content")
            };
            WireJson.WriteOptions(root, request, true);
            return root;
        }

        internal static JsonObject WriteCall(ToolCall call)
            => new JsonObject { ["type"] = "function_call", ["call_id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Arguments };

        public ModelResponse ReadResponse(JsonElement json)
        {
            string status = WireJson.String(json, "status");
            if (status != "completed" && status != "incomplete") throw new ModelException("Upstream response did not complete: " + status);
            var message = new Message { Role = "assistant", ProviderItemsAreComplete = true };
            bool refused = false;
            foreach (var item in json.GetProperty("output").EnumerateArray())
            {
                string type = WireJson.String(item, "type");
                switch (type)
                {
                    case "message":
                        foreach (var part in item.GetProperty("content").EnumerateArray())
                        {
                            string partType = WireJson.String(part, "type");
                            if (partType == "output_text") message.Text += WireJson.String(part, "text");
                            else if (partType == "refusal") { refused = true; message.Text += WireJson.String(part, "refusal"); }
                            else throw new ModelException("Unsupported upstream message content: " + partType);
                        }
                        break;
                    case "function_call":
                        message.ToolCalls.Add(new ToolCall
                        {
                            Id = WireJson.String(item, "call_id"), Name = WireJson.String(item, "name"), Arguments = WireJson.String(item, "arguments")
                        });
                        break;
                    case "reasoning": break;
                    default: throw new ModelException("Unsupported upstream output item: " + type);
                }
                message.ProviderItems.Add(item.Clone());
            }
            var finish = message.ToolCalls.Count > 0 ? ModelFinish.ToolCalls : ModelFinish.Stop;
            if (refused) finish = ModelFinish.Refused;
            if (status == "incomplete")
            {
                string reason = WireJson.String(json.GetProperty("incomplete_details"), "reason");
                if (reason == "max_output_tokens" || reason == "length") finish = ModelFinish.Length;
                else if (reason == "content_filter") finish = ModelFinish.ContentFiltered;
                else throw new ModelException("Unsupported incomplete reason: " + reason);
            }
            return new ModelResponse { Message = message, Finish = finish, Usage = WireJson.Usage(json, true) };
        }

        public JsonObject WriteResponse(RunState state, string publicModel)
        {
            var output = new JsonArray();
            var message = state.LastResponse?.Message;
            bool refused = state.LastResponse?.Finish == ModelFinish.Refused;
            bool incomplete = state.Status == RunStatus.BudgetExhausted || state.LastResponse?.Finish == ModelFinish.Length ||
                state.LastResponse?.Finish == ModelFinish.ContentFiltered;
            if (message != null && message.Text.Length > 0)
                output.Add(new JsonObject
                {
                    ["id"] = "msg_" + state.Id.Substring(5), ["type"] = "message", ["role"] = "assistant", ["status"] = incomplete ? "incomplete" : "completed",
                    ["content"] = new JsonArray(refused
                        ? new JsonObject { ["type"] = "refusal", ["refusal"] = message.Text }
                        : new JsonObject { ["type"] = "output_text", ["text"] = message.Text, ["annotations"] = new JsonArray() })
                });
            if (state.Status == RunStatus.WaitingForTools && message != null)
                foreach (var call in message.ToolCalls)
                {
                    var item = WriteCall(call);
                    item["id"] = "fc_" + state.Id.Substring(5) + "_" + output.Count;
                    item["status"] = "completed";
                    output.Add(item);
                }
            return new JsonObject
            {
                ["id"] = state.Id, ["object"] = "response", ["created_at"] = state.CreatedAt,
                ["model"] = publicModel, ["status"] = incomplete ? "incomplete" : "completed",
                ["error"] = null, ["store"] = state.Request.StoreResponse,
                ["incomplete_details"] = incomplete ? new JsonObject
                {
                    ["reason"] = state.LastResponse?.Finish == ModelFinish.ContentFiltered ? "content_filter" : "max_output_tokens"
                } : null,
                ["output"] = output, ["usage"] = WireJson.Usage(state.Usage, true),
                ["parallel_tool_calls"] = true, ["tool_choice"] = "auto", ["tools"] = new JsonArray()
            };
        }
    }
}
