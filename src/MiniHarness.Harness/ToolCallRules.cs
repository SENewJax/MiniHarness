using System;
using System.Collections.Generic;
using System.Linq;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public static class ToolCallRules
    {
        public static void ValidateChoice(string choice, IReadOnlyList<ToolCall> calls)
        {
            if (choice == "auto") return;
            if (choice == "none" && calls.Count == 0) return;
            if (choice == "required" && calls.Count > 0) return;
            if (choice != "none" && choice != "required" && calls.Count > 0 && calls.All(c => c.Name == choice)) return;
            throw new ModelException("Model response violated tool_choice=" + choice + ".");
        }
        public static void ValidateIdentity(IEnumerable<ToolCall> calls, IEnumerable<Message> history)
        {
            var ids = new HashSet<string>(history.SelectMany(m => m.ToolCalls).Select(c => c.Id), StringComparer.Ordinal);
            foreach (var call in calls)
                if (string.IsNullOrWhiteSpace(call.Id) || !ids.Add(call.Id) || string.IsNullOrWhiteSpace(call.Name))
                    throw new ModelException("Invalid or duplicate tool call identity.");
        }
    }
}
