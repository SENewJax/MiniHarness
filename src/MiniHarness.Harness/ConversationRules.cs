using System;
using System.Collections.Generic;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public static class ConversationRules
    {
        public static void Validate(IEnumerable<Message> messages)
        {
            var pending = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                if (message.Role == "tool")
                {
                    if (message.ToolCallId == null || !pending.Remove(message.ToolCallId)) throw new ArgumentException("Tool result has no matching pending call.");
                    continue;
                }
                if (pending.Count > 0 && message.ToolCalls.Count == 0 && message.ProviderItems.Count == 0)
                    throw new ArgumentException("Supply all tool results before the next message.");
                foreach (var call in message.ToolCalls)
                {
                    if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name) || !seen.Add(call.Id))
                        throw new ArgumentException("Invalid or duplicate historical tool call.");
                    pending.Add(call.Id);
                }
            }
            if (pending.Count > 0) throw new ArgumentException("Supply all pending tool results before continuing.");
        }
    }
}
