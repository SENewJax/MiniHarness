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
        private static async Task ConcurrentStoreScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-store-readers-" + Guid.NewGuid().ToString("N"));
            using var stop = new CancellationTokenSource();
            Task[] readers = Array.Empty<Task>();
            try
            {
                var store = new FileTaskStore(directory);
                var state = new AgentTaskState { Turns = { new TaskTurn() } };
                await using var lease = await store.AcquireAsync(state.Id, CancellationToken.None);
                await lease.CommitAsync(state, 0, CancellationToken.None);
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int ready = 0;
                readers = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
                {
                    // Separate instances must coordinate the same physical path.
                    var observer = new FileTaskStore(directory);
                    long last = 0;
                    bool announced = false;
                    do
                    {
                        var snapshot = await observer.LoadAsync(state.Id, CancellationToken.None);
                        Check.True(snapshot != null && snapshot.Revision >= last, "Readers must see complete, monotonic snapshots.");
                        last = snapshot!.Revision;
                        if (!announced)
                        {
                            announced = true;
                            if (Interlocked.Increment(ref ready) == 6) entered.TrySetResult(true);
                        }
                        await Task.Yield();
                    } while (!stop.IsCancellationRequested);
                })).ToArray();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                for (int index = 0; index < 100; index++)
                {
                    state.Facts.Goal = "revision " + index;
                    await lease.CommitAsync(state, state.Revision, CancellationToken.None);
                }
                stop.Cancel(); await Task.WhenAll(readers);
                Check.Equal(101L, (await store.LoadAsync(state.Id, CancellationToken.None))!.Revision);
                Check.Equal(state.Id, (await store.ResolveResponseAsync(state.Turns.Single().ResponseId, CancellationToken.None))!.TaskId);
            }
            finally
            {
                stop.Cancel();
                try { await Task.WhenAll(readers); } catch { /* Preserve the primary failure. */ }
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
        private static async Task BackgroundFailureScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-background-failure-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileTaskStore(directory);
                var counter = new ContextCounter();
                var failure = new TaskCompletionSource<(string Id, Exception Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var runtime = new TaskRuntime(new ScriptedModel(HarnessTests.Call("counter", "{}")), new OutcomeFailureStore(store),
                    new HarnessOptions(), TestBinding(), tools: new ToolRegistry(new[] { counter }), toolPolicy: new AllowListToolPolicy(new[] { "counter" }),
                    executionFailed: (id, error) => { failure.TrySetResult((id, error)); throw new Exception("Broken diagnostic sink"); });
                var receipt = await runtime.SubmitAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, CancellationToken.None);
                var observed = await failure.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check.Equal(receipt.TaskId, observed.Id);
                Check.True(observed.Error is CheckpointWriteException && observed.Error.InnerException is IOException);
                Check.Equal(1, counter.Count);
                var saved = (await store.LoadAsync(receipt.TaskId, CancellationToken.None))!;
                Check.Equal(OperationStatus.Started, saved.Operations.Single().Status);
                // The failure remains uncertain on disk; no fabricated Finished
                // record and no automatic replay is introduced by diagnostics.
                Check.Equal(1, saved.Budget.ToolCalls);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
