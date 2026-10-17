using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;

namespace MiniHarness.Tests
{
    internal static partial class TaskStageTests
    {
        private static async Task RuntimeScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-stage2-" + Guid.NewGuid().ToString("N"));
            try
            {
                var model = new GatedModel();
                await using var runtime = new TaskRuntime(model, new FileTaskStore(directory),
                    new HarnessOptions { MaxModelCalls = 2 }, new ModelBinding { Model = "test", Protocol = "Responses", ConfigurationVersion = "v1" });
                var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() };
                string taskId = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                using var clientWait = new CancellationTokenSource();
                var firstWait = runtime.ExecuteAsync(start, clientWait.Token);
                await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                clientWait.Cancel();
                await Check.ThrowsAsync<OperationCanceledException>(() => firstWait);
                var second = new TaskCommand { TaskId = taskId, Kind = TaskCommandKind.Input, Request = HarnessTests.Request() };
                second.Request.Messages[0].Text = "second input";
                var advancing = runtime.ExecuteAsync(second, CancellationToken.None);
                var duplicate = runtime.ExecuteAsync(second, CancellationToken.None);
                model.Release.TrySetResult(true);
                var accepted = await advancing;
                var replay = await duplicate;
                Check.Equal(1, model.MaxConcurrent); Check.Equal(2, model.Calls);
                Check.Equal(accepted.Receipt.TurnId, replay.Receipt.TurnId);
                Check.Equal(accepted.Receipt.AcceptedRevision, replay.Receipt.AcceptedRevision);
                Check.Equal(taskId, accepted.State.Id); Check.Equal(2, accepted.State.Turns.Count);
                Check.Equal(2, accepted.State.Budget.ModelCalls); Check.Equal(10L, accepted.State.Budget.Usage.TotalTokens);
                Check.Equal(1, accepted.State.History.Count(h => h.Message.Text == "second input"));
                Check.Equal(AgentTaskStatus.Waiting, accepted.State.Status); Check.Equal(WaitReason.Input, accepted.State.Wait);
                second.Request.Messages[0].Text = "different payload";
                await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(second, CancellationToken.None));
                var invalid = HarnessTests.Request(); invalid.Options.MaxOutputTokens = 0;
                await Check.ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(new TaskCommand
                { TaskId = taskId, Kind = TaskCommandKind.Input, Request = invalid }, CancellationToken.None));
                Check.Equal(2, (await runtime.GetAsync(taskId, CancellationToken.None))!.Turns.Count);
                var third = await runtime.ExecuteAsync(new TaskCommand { TaskId = taskId, Kind = TaskCommandKind.Input, Request = HarnessTests.Request() }, CancellationToken.None);
                Check.Equal(WaitReason.Budget, third.State.Wait); Check.Equal(2, model.Calls);
                var loaded = (await new FileTaskStore(directory).LoadAsync(taskId, CancellationToken.None))!;
                Check.Equal(2, loaded.Budget.ModelCalls); Check.Equal(3, loaded.Receipts.Count);
                Check.True(loaded.Events.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, loaded.Events.Count).Select(i => (long)i)));
                Check.True(loaded.Events.All(e => e.Revision > 0 && e.TaskId == taskId));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private sealed class GatedModel : IModelProvider
        {
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Calls { get; private set; }
            public int MaxConcurrent { get; private set; }
            private int running;
            public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
            {
                Calls++; running++; MaxConcurrent = Math.Max(MaxConcurrent, running);
                try
                {
                    if (Calls == 1) { Entered.TrySetResult(true); await Release.Task.WaitAsync(cancellationToken); }
                    return HarnessTests.Answer("answer");
                }
                finally { running--; }
            }
        }
    }
}
