using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using MiniHarness.Abstractions;

namespace MiniHarness.Protocols.OpenAI
{
    public sealed class ApiRequest
    {
        public ModelRequest ModelRequest { get; set; } = new ModelRequest();
        public bool Stream { get; set; }
        public bool IncludeUsage { get; set; }
        public bool Store { get; set; } = true;
        public bool ToolsSpecified { get; set; }
        public string? PreviousResponseId { get; set; }
    }

    public interface IOpenAiProtocol
    {
        string Path { get; }
        ApiRequest ReadRequest(JsonElement json);
        JsonObject WriteRequest(ModelRequest request);
        ModelResponse ReadResponse(JsonElement json);
        JsonObject WriteResponse(RunState state, string publicModel);
    }

    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    internal static class WireJson
    {
        public static string String(JsonElement value, string name, string? fallback = null)
        {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null)
                return fallback ?? throw new ProtocolException("Missing field: " + name);
            if (field.ValueKind != JsonValueKind.String) throw new ProtocolException(name + " must be a string.");
            return field.GetString()!;
        }
        public static bool Boolean(JsonElement value, string name, bool fallback = false)
        {
            if (!value.TryGetProperty(name, out var field)) return fallback;
            if (field.ValueKind != JsonValueKind.True && field.ValueKind != JsonValueKind.False)
                throw new ProtocolException(name + " must be boolean.");
            return field.GetBoolean();
        }
        public static void CheckFields(JsonElement value, params string[] allowed)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ProtocolException("Expected JSON object.");
            var names = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Contains(property.Name)) throw new ProtocolException("Unsupported field: " + property.Name);
        }
        public static int PositiveInt(JsonElement root, string name, int fallback)
        {
            if (!root.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < 1)
                throw new ProtocolException(name + " must be a positive integer.");
            return number;
        }
        public static string Text(JsonElement content, params string[] types)
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
            if (content.ValueKind == JsonValueKind.Null) return "";
            if (content.ValueKind != JsonValueKind.Array) throw new ProtocolException("Content must be text or text parts.");
            var result = new System.Text.StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                string type = String(part, "type");
                if (Array.IndexOf(types, type) < 0) throw new ProtocolException("Unsupported content type: " + type);
                result.Append(String(part, "text"));
            }
            return result.ToString();
        }
        public static void ReadOptions(JsonElement json, ModelRequest request, bool responses)
        {
            request.Model = String(json, "model");
            if (string.IsNullOrWhiteSpace(request.Model)) throw new ProtocolException("model must not be empty.");
            request.Options.MaxOutputTokens = PositiveInt(json, responses ? "max_output_tokens" : "max_completion_tokens", 1024);
            if (!responses && json.TryGetProperty("max_tokens", out _))
            {
                if (json.TryGetProperty("max_completion_tokens", out _)) throw new ProtocolException("Specify only one output token limit.");
                request.Options.MaxOutputTokens = PositiveInt(json, "max_tokens", 1024);
            }
            if (json.TryGetProperty("temperature", out var temperature))
            {
                if (!temperature.TryGetDouble(out double number) || number < 0 || number > 2)
                    throw new ProtocolException("temperature must be between 0 and 2.");
                request.Options.Temperature = number;
            }
            if (json.TryGetProperty("tool_choice", out var choice))
            {
                if (choice.ValueKind == JsonValueKind.String)
                {
                    string text = choice.GetString()!;
                    if (text != "auto" && text != "none" && text != "required") throw new ProtocolException("Invalid tool_choice.");
                    request.Options.ToolChoice = text;
                }
                else
                {
                    if (String(choice, "type") != "function") throw new ProtocolException("Only function tools are supported.");
                    request.Options.ToolChoice = String(responses ? choice : choice.GetProperty("function"), "name");
                }
            }
            if (json.TryGetProperty("tools", out var tools))
            {
                if (tools.ValueKind != JsonValueKind.Array) throw new ProtocolException("tools must be an array.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tool in tools.EnumerateArray())
                {
                    if (String(tool, "type") != "function") throw new ProtocolException("Only function tools are supported.");
                    var function = responses ? tool : tool.GetProperty("function");
                    string name = String(function, "name");
                    if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) throw new ProtocolException("Empty or duplicate tool name.");
                    var parameters = function.GetProperty("parameters");
                    if (parameters.ValueKind != JsonValueKind.Object) throw new ProtocolException("Tool parameters must be a JSON Schema object.");
                    request.Tools.Add(new ToolDefinition
                    {
                        Name = name, Description = String(function, "description", ""), Parameters = parameters.Clone(),
                        Strict = function.TryGetProperty("strict", out _) ? Boolean(function, "strict") : null
                    });
                }
            }
        }
        public static void WriteOptions(JsonObject root, ModelRequest request, bool responses)
        {
            root["model"] = request.Model;
            root[responses ? "max_output_tokens" : "max_completion_tokens"] = request.Options.MaxOutputTokens;
            root["stream"] = false;
            if (request.Options.Temperature.HasValue) root["temperature"] = request.Options.Temperature.Value;
            var tools = new JsonArray();
            foreach (var definition in request.Tools)
            {
                var function = new JsonObject
                {
                    ["name"] = definition.Name, ["description"] = definition.Description,
                    ["parameters"] = JsonNode.Parse(definition.Parameters.GetRawText())
                };
                if (definition.Strict.HasValue) function["strict"] = definition.Strict.Value;
                if (responses) { function["type"] = "function"; tools.Add(function); }
                else tools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            if (tools.Count > 0)
            {
                root["tools"] = tools;
                string choice = request.Options.ToolChoice;
                if (choice == "auto" || choice == "none" || choice == "required") root["tool_choice"] = choice;
                else root["tool_choice"] = responses
                    ? new JsonObject { ["type"] = "function", ["name"] = choice }
                    : new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = choice } };
            }
        }
        public static TokenUsage Usage(JsonElement json, bool responses)
        {
            if (!json.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
                return new TokenUsage { Known = false };
            string input = responses ? "input_tokens" : "prompt_tokens";
            string output = responses ? "output_tokens" : "completion_tokens";
            if (!usage.TryGetProperty(input, out var i) || !usage.TryGetProperty(output, out var o) ||
                !i.TryGetInt64(out long inputCount) || !o.TryGetInt64(out long outputCount) || inputCount < 0 || outputCount < 0)
                throw new ModelException("Invalid upstream token usage.");
            return new TokenUsage { InputTokens = inputCount, OutputTokens = outputCount };
        }
        public static JsonObject? Usage(TokenUsage usage, bool responses)
        {
            if (!usage.Known) return null;
            return new JsonObject
            {
                [responses ? "input_tokens" : "prompt_tokens"] = usage.InputTokens,
                [responses ? "output_tokens" : "completion_tokens"] = usage.OutputTokens,
                ["total_tokens"] = usage.TotalTokens
            };
        }
        public static string Finish(RunState state)
        {
            if (state.Status == RunStatus.WaitingForTools) return "tool_calls";
            if (state.Status == RunStatus.BudgetExhausted || state.LastResponse?.Finish == ModelFinish.Length) return "length";
            if (state.LastResponse?.Finish == ModelFinish.ContentFiltered) return "content_filter";
            return "stop";
        }
    }
}
