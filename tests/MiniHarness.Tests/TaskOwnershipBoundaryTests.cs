using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;

namespace MiniHarness.Tests
{
    internal static partial class TaskStageTests
    {
        private static async Task ResourceBoundaryScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-resources-" + Guid.NewGuid().ToString("N"));
            var store = new FileTaskStore(directory); var manager = new FakeResourceManager(); var tool = new RunningResourceTool(manager);
            string taskId; string resourceId;
            try
            {
                await using (var runtime = new TaskRuntime(new ScriptedModel(HarnessTests.Call("launch", "{}"), HarnessTests.Answer()), store,
                    new HarnessOptions(), TestBinding(), tools: new ToolRegistry(new[] { tool }), toolPolicy: new AllowListToolPolicy(new[] { "launch" }),
                    resourceManagers: new[] { manager }))
                {
                    var started = await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, CancellationToken.None);
                    taskId = started.State.Id; resourceId = started.State.Resources.Single().Id;
                    Check.Equal(OperationStatus.Finished, started.State.Operations.Single().Status);
                    Check.Equal("running", started.State.Resources.Single().State);
                    var query = await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.QueryResource, TaskId = taskId, ResourceId = resourceId }, CancellationToken.None);
                    Check.Equal("running", query.State.Resources.Single().State);
                    manager.FailNextCancel = true;
                    var cancel = new TaskCommand { Kind = TaskCommandKind.CancelResource, TaskId = taskId, ResourceId = resourceId };
                    await Check.ThrowsAsync<IOException>(() => runtime.ExecuteAsync(cancel, CancellationToken.None));
                    Check.Equal("cancellation_requested", (await store.LoadAsync(taskId, CancellationToken.None))!.Resources.Single().State);
                    var cancelled = await runtime.ExecuteAsync(cancel, CancellationToken.None);
                    await runtime.ExecuteAsync(cancel, CancellationToken.None);
                    Check.Equal(1, manager.CancelEffects); Check.Equal("cancelled", cancelled.State.Resources.Single().State);
                    Check.Equal("cancel confirmed", cancelled.State.Resources.Single().Result);
                    await Check.ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.QueryResource,
                        TaskId = taskId, ResourceId = "someone-else" }, CancellationToken.None));
                }
                await using (var reloaded = new TaskRuntime(new ScriptedModel(HarnessTests.Call("launch", "{}"), HarnessTests.Answer()), store,
                    new HarnessOptions(), TestBinding(), tools: new ToolRegistry(new[] { tool }), toolPolicy: new AllowListToolPolicy(new[] { "launch" }),
                    resourceManagers: new[] { manager }))
                {
                    var reconciled = await reloaded.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ReconcileResources, TaskId = taskId }, CancellationToken.None);
                    Check.Equal("cancelled", reconciled.State.Resources.Single().State); Check.Equal(1, manager.CancelEffects);
                    Check.Equal(taskId, reconciled.State.Resources.Single().OwnerTaskId);
                    var second = await reloaded.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, CancellationToken.None);
                    var cancelledTask = await reloaded.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = second.State.Id }, CancellationToken.None);
                    Check.Equal(AgentTaskStatus.Cancelled, cancelledTask.State.Status); Check.Equal("cancelled", cancelledTask.State.Resources.Single().State);
                    Check.Equal(2, manager.CancelEffects);
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private sealed class FakeResourceManager : IResourceManager
        {
            public string Kind => "fake-running";
            public Dictionary<string, string> World { get; } = new Dictionary<string, string>();
            public int CancelEffects { get; private set; }
            public bool FailNextCancel { get; set; }
            public Task<ResourceReference> ExecuteAsync(ResourceReference resource, ResourceAction action, CancellationToken ct)
            {
                Check.Equal(resource.Id, resource.RecoveryHint);
                if (action == ResourceAction.Cancel)
                {
                    if (FailNextCancel) { FailNextCancel = false; throw new IOException("fake transport failure before cancellation"); }
                    if (World[resource.Id] != "cancelled") { World[resource.Id] = "cancelled"; CancelEffects++; }
                }
                resource.State = World[resource.Id]; resource.Result = resource.State == "cancelled" ? "cancel confirmed" : "still running";
                return Task.FromResult(resource);
            }
        }
        private sealed class RunningResourceTool : IContextualTool
        {
            private readonly FakeResourceManager manager;
            public ToolDefinition Definition { get; }
            public RunningResourceTool(FakeResourceManager manager)
            {
                this.manager = manager;
                using var json = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
                Definition = new ToolDefinition { Name = "launch", Parameters = json.RootElement.Clone() };
            }
            public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct) => throw new InvalidOperationException("Context required");
            public Task<ToolOutcome> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
            {
                string id = "fake_" + context.OperationId; manager.World[id] = "running";
                return Task.FromResult(new ToolOutcome { Status = ToolCallStatus.Succeeded, Effect = EffectState.Confirmed, Output = "launched",
                    Resources = { new ResourceReference { Id = id, Kind = manager.Kind, State = "running", RecoveryHint = id } } });
            }
        }
        private static async Task ChildBoundaryScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-children-" + Guid.NewGuid().ToString("N"));
            var store = new FileTaskStore(directory); var prepare = new ControlledPreparation();
            var model = new ScriptedModel(HarnessTests.Answer("parent ready"), HarnessTests.Answer("child result"), HarnessTests.Answer("parent integrated result"));
            try
            {
                string parentId; string childId;
                await using (var runtime = new TaskRuntime(model, new ChildCollectionFailureStore(store), new HarnessOptions { MaxModelCalls = 4, MaxToolCalls = 2, MaxTotalTokens = 100 },
                    TestBinding(), preparation: prepare))
                {
                    var parent = await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request(),
                        Facts = new TaskFacts { Goal = "parent goal", Constraints = { "keep scope" }, Workspace = new WorkspaceScope { Grants = { "read" } } } }, CancellationToken.None);
                    parentId = parent.State.Id;
                    prepare.BlockNext();
                    var create = new TaskCommand { Kind = TaskCommandKind.StartChild, TaskId = parentId, Request = HarnessTests.Request(),
                        ChildBudget = new TaskBudget { MaxModelCalls = 2, MaxToolCalls = 1, MaxTokens = 20 }, Facts = new TaskFacts { Workspace = new WorkspaceScope { Grants = { "admin" } } } };
                    var created = await runtime.ExecuteAsync(create, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    childId = created.State.Children.Single().TaskId;
                    await prepare.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await runtime.ExecuteAsync(create, CancellationToken.None);
                    var child = (await runtime.GetAsync(childId, CancellationToken.None))!;
                    Check.Equal(parentId, child.ParentTaskId); Check.Equal(2, child.Budget.MaxModelCalls);
                    Check.Equal("read", child.Facts.Workspace.Grants.Single()); Check.Equal("keep scope", child.Facts.Constraints.Single());
                    Check.Equal(2, created.State.Budget.ReservedModelCalls); Check.Equal(1, created.State.Budget.ModelCalls);
                    await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.StartChild, TaskId = parentId,
                        Request = HarnessTests.Request(), ChildBudget = new TaskBudget { MaxModelCalls = 2, MaxToolCalls = 0, MaxTokens = 10 } }, CancellationToken.None));
                    await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.CollectChild,
                        TaskId = parentId, ChildTaskId = childId }, CancellationToken.None));
                    prepare.Release.TrySetResult(true);
                    // A duplicate child Start awaits the same running command and
                    // proves that dispatch uses the runtime's ordinary command path.
                    await runtime.ExecuteAsync(new TaskCommand { Id = created.State.Children.Single().StartCommandId, Kind = TaskCommandKind.Start,
                        ParentTaskId = parentId, Request = HarnessTests.Request() }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    var collect = new TaskCommand { Kind = TaskCommandKind.CollectChild, TaskId = parentId, ChildTaskId = childId };
                    await Check.ThrowsAsync<CheckpointWriteException>(() => runtime.ExecuteAsync(collect, CancellationToken.None));
                    Check.Equal(AgentTaskStatus.Completed, (await store.LoadAsync(childId, CancellationToken.None))!.Status);
                    Check.Equal(2, (await store.LoadAsync(parentId, CancellationToken.None))!.Budget.ReservedModelCalls);
                    var collected = await runtime.ExecuteAsync(collect, CancellationToken.None);
                    await runtime.ExecuteAsync(collect, CancellationToken.None);
                    var again = await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.CollectChild, TaskId = parentId, ChildTaskId = childId }, CancellationToken.None);
                    Check.Equal(2, again.State.Budget.ModelCalls); Check.Equal(10L, again.State.Budget.Usage.TotalTokens);
                    Check.Equal(0, again.State.Budget.ReservedModelCalls); Check.Equal(0L, again.State.Budget.ReservedTokens);
                    Check.Equal("child result", collected.State.Children.Single().Result);
                    Check.Equal(1, again.State.History.Count(h => h.Message.Text.StartsWith("Child task result:")));
                    await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Input, TaskId = childId,
                        Request = HarnessTests.Request() }, CancellationToken.None));
                    var next = await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Input, TaskId = parentId, Request = HarnessTests.Request() }, CancellationToken.None);
                    Check.Equal(3, next.State.Budget.ModelCalls); Check.Equal(15L, next.State.Budget.Usage.TotalTokens);
                    Check.True(model.Inputs.Last().Messages.Any(m => m.Text.Contains("child result")));
                }
                var restored = (await new FileTaskStore(directory).LoadAsync(parentId, CancellationToken.None))!;
                Check.True(restored.Children.Single().Collected); Check.Equal(1, restored.Children.Single().Usage!.ModelCalls);
                Check.Equal(3, restored.Budget.ModelCalls); Check.Equal(parentId, (await store.LoadAsync(childId, CancellationToken.None))!.ParentTaskId);
                prepare = new ControlledPreparation();
                await using (var cancelledRuntime = new TaskRuntime(new ScriptedModel(HarnessTests.Answer("parent")), store,
                    new HarnessOptions(), TestBinding(), preparation: prepare))
                {
                    var parent = await cancelledRuntime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, CancellationToken.None);
                    prepare.BlockNext();
                    var created = await cancelledRuntime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.StartChild, TaskId = parent.State.Id,
                        Request = HarnessTests.Request(), ChildBudget = new TaskBudget { MaxModelCalls = 1, MaxToolCalls = 0, MaxTokens = 20 } }, CancellationToken.None);
                    await prepare.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var cancel = new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = parent.State.Id };
                    var result = await cancelledRuntime.ExecuteAsync(cancel, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    await cancelledRuntime.ExecuteAsync(cancel, CancellationToken.None);
                    Check.Equal(AgentTaskStatus.Cancelled, result.State.Status); Check.True(result.State.Children.Single().Collected);
                    Check.Equal(0, result.State.Budget.ReservedModelCalls); Check.Equal(1, result.State.Budget.ModelCalls);
                    Check.Equal(AgentTaskStatus.Cancelled, (await store.LoadAsync(created.State.Children.Single().TaskId, CancellationToken.None))!.Status);
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private sealed class ChildCollectionFailureStore : ITaskStore
        {
            private readonly ITaskStore inner;
            private bool failed;
            public ChildCollectionFailureStore(ITaskStore inner) { this.inner = inner; }
            public Task<AgentTaskState?> LoadAsync(string id, CancellationToken ct) => inner.LoadAsync(id, ct);
            public Task<TaskResponseReference?> ResolveResponseAsync(string id, CancellationToken ct) => inner.ResolveResponseAsync(id, ct);
            public async Task<ITaskLease> AcquireAsync(string id, CancellationToken ct) => new Lease(this, await inner.AcquireAsync(id, ct));
            private sealed class Lease : ITaskLease
            {
                private readonly ChildCollectionFailureStore owner;
                private readonly ITaskLease inner;
                public Lease(ChildCollectionFailureStore owner, ITaskLease inner) { this.owner = owner; this.inner = inner; }
                public Task<AgentTaskState?> LoadAsync(CancellationToken ct) => inner.LoadAsync(ct);
                public Task CommitAsync(AgentTaskState state, long revision, CancellationToken ct)
                {
                    if (!owner.failed && state.Children.Any(c => c.Collected))
                    { owner.failed = true; throw new CheckpointWriteException(new IOException("Child closed before parent settlement commit")); }
                    return inner.CommitAsync(state, revision, ct);
                }
                public ValueTask DisposeAsync() => inner.DisposeAsync();
            }
        }
    }
}
