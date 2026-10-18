using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;

namespace MiniHarness.Tests
{
    internal static partial class TaskStageTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[1] legacy migration, second turn, ownership and schema round trip", StorageScenario));
            tests.Add(("[v0-storage] concurrent checkpoint readers and publication across store instances", ConcurrentStoreScenario));
            tests.Add(("[v0-storage] accepted background checkpoint failure reaches host without replay", BackgroundFailureScenario));
            tests.Add(("[2] persistent task, concurrent inputs, dedup, detached wait and cumulative budget", RuntimeScenario));
            tests.Add(("[3] approval continuation, contextual ownership and uncertain effect recovery", OperationsScenario));
            tests.Add(("[4-5] cancelled preparation, frozen step and current-workspace evidence gate", PreparationEvidenceScenario));
            tests.Add(("[6] HTTP task submission, observer reconnect, history fork and restart", TaskHttpScenario));
            tests.Add(("[v0-control] duplicate controls, conflict isolation, prompt interruption and restart", ControlBoundaryScenario));
            tests.Add(("[v0-replan] edit then inspect invalidation, fresh approval and continuation", ReplanBoundaryScenario));
            tests.Add(("[v0-resource] running resource query, confirmed cancellation and reload", ResourceBoundaryScenario));
            tests.Add(("[v0-child] unified child runtime, result delivery and budget settlement", ChildBoundaryScenario));
        }
        private static async Task StorageScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-stage1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var request = HarnessTests.Request();
                var native = JsonNode.Parse("{\"type\":\"reasoning\",\"id\":\"rs_old\",\"encrypted_content\":\"opaque\",\"summary\":[]}")!;
                using var json = JsonDocument.Parse(native.ToJsonString());
                request.Messages.Add(new Message { Role = "assistant", ProviderItemsAreComplete = true, ProviderItems = { json.RootElement.Clone() } });
                var legacy = new RunState
                {
                    Request = request, Status = RunStatus.Completed, ModelCalls = 2, ToolCalls = 1,
                    Usage = new TokenUsage { InputTokens = 100, OutputTokens = 20 }, LastResponse = HarnessTests.Answer()
                };
                var oldStore = new FileRunStore(directory);
                await oldStore.SaveAsync(legacy, CancellationToken.None);
                string oldPath = Path.Combine(directory, legacy.Id + ".json");
                string original = await File.ReadAllTextAsync(oldPath);
                var store = new FileTaskStore(directory);
                await using (var lease = await store.AcquireAsync(legacy.Id, CancellationToken.None))
                {
                    var task = (await lease.LoadAsync(CancellationToken.None))!;
                    Check.Equal(0L, task.Revision); Check.Equal(120L, task.Budget.Usage.TotalTokens);
                    var second = new TaskTurn { Settings = HarnessTests.Request(), Status = TurnStatus.Completed };
                    second.Settings.Messages.Clear(); task.Turns.Add(second);
                    task.History.Add(new HistoryEntry { TurnId = second.Id, Message = new Message { Text = "second input" } });
                    task.Budget.ModelCalls++; task.Budget.Usage.Add(new TokenUsage { InputTokens = 5, OutputTokens = 3 });
                    await lease.CommitAsync(task, 0, CancellationToken.None);
                    await Check.ThrowsAsync<TaskConflictException>(async () =>
                    {
                        await using var other = await new FileTaskStore(directory).AcquireAsync(task.Id, CancellationToken.None);
                    });
                    await Check.ThrowsAsync<TaskConflictException>(() => lease.CommitAsync(task, 0, CancellationToken.None));
                }
                var loaded = (await new FileTaskStore(directory).LoadAsync(legacy.Id, CancellationToken.None))!;
                Check.Equal(1L, loaded.Revision); Check.Equal(2, loaded.Turns.Count);
                Check.Equal(3, loaded.Budget.ModelCalls); Check.Equal(1, loaded.Budget.ToolCalls);
                Check.Equal(128L, loaded.Budget.Usage.TotalTokens);
                Check.Equal("test", loaded.Facts.Goal);
                Check.Equal("second input", loaded.History[2].Message.Text);
                Check.True(loaded.History[1].Message.ProviderItemsAreComplete);
                Check.True(JsonNode.DeepEquals(native, JsonNode.Parse(loaded.History[1].Message.ProviderItems[0].GetRawText())));
                Check.Equal(loaded.Turns[1].Id, (await store.ResolveResponseAsync(loaded.Turns[1].ResponseId, CancellationToken.None))!.TurnId);
                Check.Equal(original, await File.ReadAllTextAsync(oldPath));
                var future = TaskJson.Clone(loaded); future.SchemaVersion = 999;
                await File.WriteAllTextAsync(Path.Combine(directory, loaded.Id + ".json"), JsonSerializer.Serialize(future, TaskJson.Options));
                await Check.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(loaded.Id, CancellationToken.None));
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
