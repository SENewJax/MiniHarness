using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace MiniHarness.Server
{
    // Buffered SSE: the agent completes before these events are emitted. No hidden
    // intermediate tool-call fragments are exposed as if they were the final answer.
    internal static class SseWriter
    {
        public static async Task WriteAsync(HttpResponse http, bool responses, JsonObject result, bool includeUsage, CancellationToken ct)
        {
            http.ContentType = "text/event-stream";
            http.Headers.CacheControl = "no-cache";
            http.Headers["X-Accel-Buffering"] = "no";
            if (responses) await WriteResponsesAsync(http, result, ct).ConfigureAwait(false);
            else await WriteChatAsync(http, result, includeUsage, ct).ConfigureAwait(false);
        }

        private static async Task WriteChatAsync(HttpResponse http, JsonObject result, bool includeUsage, CancellationToken ct)
        {
            var choice = result["choices"]![0]!;
            var message = choice["message"]!;
            JsonObject Chunk(JsonObject delta, string? finish = null) => new JsonObject
            {
                ["id"] = result["id"]!.DeepClone(), ["object"] = "chat.completion.chunk",
                ["created"] = result["created"]!.DeepClone(), ["model"] = result["model"]!.DeepClone(),
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish, ["logprobs"] = null })
            };
            await SendAsync(http, null, Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "" }), ct).ConfigureAwait(false);
            foreach (string text in Chunks(message["content"]?.GetValue<string>() ?? ""))
                await SendAsync(http, null, Chunk(new JsonObject { ["content"] = text }), ct).ConfigureAwait(false);
            foreach (string refusal in Chunks(message["refusal"]?.GetValue<string>() ?? ""))
                await SendAsync(http, null, Chunk(new JsonObject { ["refusal"] = refusal }), ct).ConfigureAwait(false);
            if (message["tool_calls"] is JsonArray calls)
                for (int i = 0; i < calls.Count; i++)
                {
                    var call = calls[i]!;
                    await SendAsync(http, null, Chunk(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = i, ["id"] = call["id"]!.DeepClone(), ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call["function"]!["name"]!.DeepClone(), ["arguments"] = "" }
                    }) }), ct).ConfigureAwait(false);
                    foreach (string arguments in Chunks(call["function"]!["arguments"]!.GetValue<string>()))
                        await SendAsync(http, null, Chunk(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["index"] = i, ["function"] = new JsonObject { ["arguments"] = arguments }
                        }) }), ct).ConfigureAwait(false);
                }
            await SendAsync(http, null, Chunk(new JsonObject(), choice["finish_reason"]!.GetValue<string>()), ct).ConfigureAwait(false);
            if (includeUsage)
            {
                var usage = Chunk(new JsonObject());
                usage["choices"] = new JsonArray();
                usage["usage"] = result["usage"]?.DeepClone();
                await SendAsync(http, null, usage, ct).ConfigureAwait(false);
            }
            await http.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
            await http.Body.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task WriteResponsesAsync(HttpResponse http, JsonObject result, CancellationToken ct)
        {
            int sequence = 0;
            Task Event(string type, JsonObject payload)
            {
                payload["type"] = type;
                payload["sequence_number"] = sequence++;
                return SendAsync(http, type, payload, ct);
            }
            JsonObject Snapshot()
            {
                var snapshot = (JsonObject)result.DeepClone();
                snapshot["status"] = "in_progress"; snapshot["output"] = new JsonArray();
                snapshot["usage"] = null; snapshot["incomplete_details"] = null;
                return snapshot;
            }
            await Event("response.created", new JsonObject { ["response"] = Snapshot() }).ConfigureAwait(false);
            await Event("response.in_progress", new JsonObject { ["response"] = Snapshot() }).ConfigureAwait(false);
            var output = (JsonArray)result["output"]!;
            for (int i = 0; i < output.Count; i++)
            {
                var item = output[i]!;
                string id = item["id"]!.GetValue<string>();
                bool isMessage = item["type"]!.GetValue<string>() == "message";
                var initial = (JsonObject)item.DeepClone(); initial["status"] = "in_progress";
                if (isMessage) initial["content"] = new JsonArray();
                else initial["arguments"] = "";
                await Event("response.output_item.added", new JsonObject { ["output_index"] = i, ["item"] = initial }).ConfigureAwait(false);
                if (isMessage)
                {
                    var part = item["content"]![0]!;
                    bool refusal = part["type"]!.GetValue<string>() == "refusal";
                    string textKey = refusal ? "refusal" : "text";
                    string eventPrefix = refusal ? "response.refusal" : "response.output_text";
                    var emptyPart = (JsonObject)part.DeepClone(); emptyPart[textKey] = "";
                    await Event("response.content_part.added", new JsonObject
                    {
                        ["item_id"] = id, ["output_index"] = i, ["content_index"] = 0, ["part"] = emptyPart
                    }).ConfigureAwait(false);
                    foreach (string text in Chunks(part[textKey]!.GetValue<string>()))
                        await Event(eventPrefix + ".delta", new JsonObject
                        {
                            ["item_id"] = id, ["output_index"] = i, ["content_index"] = 0, ["delta"] = text
                        }).ConfigureAwait(false);
                    await Event(eventPrefix + ".done", new JsonObject
                    {
                        ["item_id"] = id, ["output_index"] = i, ["content_index"] = 0, [textKey] = part[textKey]!.DeepClone()
                    }).ConfigureAwait(false);
                    await Event("response.content_part.done", new JsonObject
                    {
                        ["item_id"] = id, ["output_index"] = i, ["content_index"] = 0, ["part"] = part.DeepClone()
                    }).ConfigureAwait(false);
                }
                else
                {
                    foreach (string arguments in Chunks(item["arguments"]!.GetValue<string>()))
                        await Event("response.function_call_arguments.delta", new JsonObject
                        {
                            ["item_id"] = id, ["output_index"] = i, ["delta"] = arguments
                        }).ConfigureAwait(false);
                    await Event("response.function_call_arguments.done", new JsonObject
                    {
                        ["item_id"] = id, ["output_index"] = i, ["arguments"] = item["arguments"]!.DeepClone()
                    }).ConfigureAwait(false);
                }
                await Event("response.output_item.done", new JsonObject { ["output_index"] = i, ["item"] = item.DeepClone() }).ConfigureAwait(false);
            }
            string terminal = result["status"]!.GetValue<string>() == "incomplete" ? "response.incomplete" : "response.completed";
            await Event(terminal, new JsonObject { ["response"] = result.DeepClone() }).ConfigureAwait(false);
        }

        private static async Task SendAsync(HttpResponse http, string? name, JsonObject payload, CancellationToken ct)
        {
            if (name != null) await http.WriteAsync("event: " + name + "\n", ct).ConfigureAwait(false);
            await http.WriteAsync("data: " + payload.ToJsonString() + "\n\n", ct).ConfigureAwait(false);
            await http.Body.FlushAsync(ct).ConfigureAwait(false);
        }

        private static IEnumerable<string> Chunks(string text)
        {
            for (int offset = 0; offset < text.Length;)
            {
                int size = Math.Min(128, text.Length - offset);
                if (offset + size < text.Length && char.IsHighSurrogate(text[offset + size - 1])) size--;
                yield return text.Substring(offset, size);
                offset += size;
            }
        }
    }
}
