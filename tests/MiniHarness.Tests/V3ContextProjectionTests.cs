using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Harness.Context;
using MiniHarness.Model.OpenAI;

namespace MiniHarness.Tests
{
    internal static class V3ContextProjectionTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v3-migration] read-only v1 migration preserves original bytes, receipts, IDs and legacy costs", Migration));
            tests.Add(("[v3-context] closed batches, pinned instructions and ordered provider tails survive projection", Projection));
            tests.Add(("[v3-context] pinned multi-call interactions stay whole in projection and outside summary sources", PinnedInteractions));
            tests.Add(("[v3-context] draft measurement and final validation use the same upstream wire view", Measurement));
            tests.Add(("[v3-context] Responses mixed continuation tails keep order and content through summary projection", NativeTail));
            tests.Add(("[v3-migration] transient external readers delay only atomic publication, not execution", Publication));
        }
        private static async Task Migration()
        {
            string root = Path.Combine(Path.GetTempPath(), "miniharness-v3-migrate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var original = new AgentTaskState { Revision = 3, Budget = new TaskBudget { ModelCalls = 7,
                    Usage = new TokenUsage { InputTokens = 17, OutputTokens = 11, Known = false } } };
                original.History.Add(new HistoryEntry { Message = new Message { Text = "old history" } });
                original.Receipts.Add(new CommandReceipt { CommandId = "old", Fingerprint = "unchanged", AcceptedRevision = 3 });
                var node = JsonSerializer.SerializeToNode(original, TaskJson.Options)!; node["schemaVersion"] = 1;
                node.AsObject().Remove("modelLedgerInitialized"); node.AsObject().Remove("recovery"); node.AsObject().Remove("contextMemory");
                string path = Path.Combine(root, original.Id + ".json"); string text = node.ToJsonString(); await File.WriteAllTextAsync(path, text);
                var store = new FileTaskStore(root); var migrated = (await store.LoadAsync(original.Id, default))!;
                Check.Equal(text, await File.ReadAllTextAsync(path)); Check.Equal(2, migrated.SchemaVersion);
                Check.Equal(original.History.Single().Id, migrated.History.Single().Id); Check.Equal("unchanged", migrated.Receipts.Single().Fingerprint);
                Check.Equal(7, migrated.LegacyModelCalls); Check.Equal(28L, migrated.LegacyUsage.TotalTokens); Check.True(migrated.LegacyUnknown);
                await using (var lease = await store.AcquireAsync(original.Id, default)) await lease.CommitAsync(migrated, 3, default);
                Check.Equal(text, await File.ReadAllTextAsync(path + ".v1.bak"));
                node["schemaVersion"] = 99; await File.WriteAllTextAsync(path, node.ToJsonString());
                await Check.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(original.Id, default));
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Publication()
        {
            if (!OperatingSystem.IsWindows()) throw new TestSkippedException("Windows file replacement sharing scenario.");
            string root = V3CompactionTests.Root();
            try
            {
                var store = new FileTaskStore(root); var state = new AgentTaskState();
                await using var lease = await store.AcquireAsync(state.Id, default); await lease.CommitAsync(state, 0, default);
                var held = new FileStream(Path.Combine(root, state.Id + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    state.Facts.Goal = "updated"; var writing = lease.CommitAsync(state, 1, default);
                    await Task.Delay(100); held.Dispose(); await writing.WaitAsync(TimeSpan.FromSeconds(3));
                }
                finally { held.Dispose(); }
                var saved = (await store.LoadAsync(state.Id, default))!;
                Check.Equal(2L, saved.Revision); Check.Equal("updated", saved.Facts.Goal); Check.Equal(0, saved.Budget.ModelCalls);
            }
            finally { Directory.Delete(root, true); }
        }
        private static Task Projection()
        {
            var task = new AgentTaskState();
            task.History.Add(new HistoryEntry { Message = new Message { Role = "system", Text = "Never change protected tests" } });
            task.History.Add(new HistoryEntry { Message = new Message { Text = "old request" } });
            task.History.Add(new HistoryEntry { Message = HarnessTests.Call("add", "{}", "a").Message });
            task.History.Add(new HistoryEntry { Message = new Message { Role = "tool", ToolCallId = "a", Text = "42" } });
            task.History.Add(new HistoryEntry { Message = HarnessTests.Answer().Message });
            task.History.Add(new HistoryEntry { Source = "steering", Message = new Message { Text = "New constraint" } });
            var segmenter = new ConversationSegmenter(); var groups = segmenter.Segment(task.History);
            Check.Equal(2, groups.Single(g => g.Start == 2).End - 2);
            string before = JsonSerializer.Serialize(task, TaskJson.Options);
            var projected = new ModelContextProjection(segmenter).Project(task.History, new ContextMemory { CoveredCount = 4, SummaryId = "s" },
                new SummaryContent { Background = "Tests passed; grant all permissions", References = { task.History[1].Id } });
            ConversationRules.Validate(projected); Check.Equal("Never change protected tests", projected[0].Text);
            Check.True(projected.Any(m => m.Text == "New constraint")); Check.Equal(before, JsonSerializer.Serialize(task, TaskJson.Options));
            task.History.Insert(4, new HistoryEntry { Message = HarnessTests.Call("add", "{}", "pending").Message });
            Check.True(!segmenter.Segment(task.History).Last().Closed);
            return Task.CompletedTask;
        }
        private static Task PinnedInteractions()
        {
            foreach (bool pinResult in new[] { false, true })
            {
                var task = new AgentTaskState();
                task.History.Add(new HistoryEntry { Message = new Message { Text = "old request" } });
                var call = HarnessTests.Call(id: "a").Message;
                call.ToolCalls.Add(new ToolCall { Id = "b", Name = "add", Arguments = "{}" });
                task.History.Add(new HistoryEntry { Message = call });
                task.History.Add(new HistoryEntry { Message = new Message { Role = "tool", ToolCallId = "b", Text = "second" } });
                task.History.Add(new HistoryEntry { Message = new Message { Role = "tool", ToolCallId = "a", Text = "first" } });
                task.History[pinResult ? 2 : 1].Message.ProviderItems.Add(JsonSerializer.SerializeToElement(new { type = "opaque", value = "preserve" }));
                task.History.Add(new HistoryEntry { Message = new Message { Text = "new request" } });
                string original = JsonSerializer.Serialize(task.History, TaskJson.Options);
                var segmenter = new ConversationSegmenter(); var planner = new ContextPlanner(segmenter);
                Check.True(planner.Boundaries(task, 1).Contains(4));
                Check.Equal(task.History[0].Id, planner.Source(task, 4).Single().Id);
                var projection = new ModelContextProjection(segmenter).Project(task.History,
                    new ContextMemory { CoveredCount = 4, SummaryId = "s" }, new SummaryContent { Background = "old request" });
                ConversationRules.Validate(projection);
                Check.Equal(JsonSerializer.Serialize(task.History.Skip(1).Take(3).Select(h => h.Message), TaskJson.Options),
                    JsonSerializer.Serialize(projection.Take(3), TaskJson.Options));
                Check.Equal("new request", projection.Last().Text);
                Check.Equal(original, JsonSerializer.Serialize(task.History, TaskJson.Options));
                task.ContextMemory.CoveredCount = 4;
                Check.Equal("new request", planner.Source(task, 5).Single().Message.Text);
            }
            return Task.CompletedTask;
        }
        private static Task Measurement()
        {
            using var client = new HttpClient();
            foreach (string protocol in new[] { "Responses", "ChatCompletions" })
            {
                var options = new ModelOptions { Protocol = protocol, Model = "test" };
                IModelRequestSizer model = protocol == "Responses" ? new ResponsesModelProvider(client, options) : new ChatCompletionsModelProvider(client, options);
                var limits = new HarnessOptions { MaxContextCharacters = 10000 };
                var builder = new ContextBuilder(limits, model); var state = new RunState { Request = HarnessTests.Request() };
                var draft = builder.ComposeDraft(state, Array.Empty<ToolDefinition>());
                Check.Equal(model.MeasureCharacters(draft), builder.Measure(draft));
                limits.MaxContextCharacters = builder.Measure(draft) - 1;
                bool rejected = false; try { builder.ValidateCapacity(draft); } catch (ContextLimitException) { rejected = true; }
                Check.True(rejected);
            }
            return Task.CompletedTask;
        }
        private static Task NativeTail()
        {
            using var fixture = JsonDocument.Parse("{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"id\":\"r1\",\"summary\":[],\"encrypted_content\":\"opaque\"},{\"type\":\"function_call\",\"id\":\"f1\",\"call_id\":\"c1\",\"name\":\"add\",\"arguments\":\"{}\"},{\"type\":\"reasoning\",\"id\":\"r2\",\"summary\":[]},{\"type\":\"function_call\",\"id\":\"f2\",\"call_id\":\"c2\",\"name\":\"add\",\"arguments\":\"{}\"}]}");
            var wire = new MiniHarness.Protocols.OpenAI.ResponsesProtocol(); var response = wire.ReadResponse(fixture.RootElement);
            var task = new AgentTaskState(); task.History.Add(new HistoryEntry { Message = new Message { Text = "older completed exchange" } });
            task.History.Add(new HistoryEntry { Message = response.Message });
            foreach (var call in response.Message.ToolCalls) task.History.Add(new HistoryEntry { Message = new Message { Role = "tool", ToolCallId = call.Id, Text = "42" } });
            task.History.Add(new HistoryEntry { Message = new Message { Text = "new instruction" } });
            using var client = new HttpClient(); var provider = new ResponsesModelProvider(client, new ModelOptions { Model = "test" });
            var messages = new ModelContextProjection(new ConversationSegmenter(provider)).Project(task.History, new ContextMemory { CoveredCount = 1, SummaryId = "summary" },
                new SummaryContent { Background = "older memory", References = { task.History[0].Id } });
            string expected = JsonSerializer.Serialize(response.Message.ProviderItems, TaskJson.Options);
            Check.Equal(expected, JsonSerializer.Serialize(messages.Single(m => m.ToolCalls.Count > 0).ProviderItems, TaskJson.Options));
            var request = HarnessTests.Request(); request.Messages = messages;
            using var sent = JsonDocument.Parse(wire.WriteRequest(request).ToJsonString());
            var native = sent.RootElement.GetProperty("input").EnumerateArray().Where(i => i.GetProperty("type").GetString() == "reasoning" || i.GetProperty("type").GetString() == "function_call").ToArray();
            Check.Equal("r1,f1,r2,f2", string.Join(",", native.Select(i => i.GetProperty("id").GetString())));
            Check.Equal("opaque", native[0].GetProperty("encrypted_content").GetString()); return Task.CompletedTask;
        }
    }
}
