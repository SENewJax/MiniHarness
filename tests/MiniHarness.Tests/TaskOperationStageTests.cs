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
        private static async Task OperationsScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-stage3-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileTaskStore(directory); var tool = new ContextCounter(); var policy = new ApprovalPolicy();
                await using (var runtime = OperationRuntime(new ScriptedModel(HarnessTests.Call("counter", "{}"), HarnessTests.Answer()), store, tool, policy))
                {
                    var started = await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, CancellationToken.None);
                    Check.Equal(WaitReason.Approval, started.State.Wait); Check.Equal(0, tool.Count);
                    var operation = started.State.Operations.Single();
                    var approval = new TaskCommand { Kind = TaskCommandKind.Approval, TaskId = started.State.Id, OperationId = operation.Id, Approve = true };
                    var completed = await runtime.ExecuteAsync(approval, CancellationToken.None);
                    await runtime.ExecuteAsync(approval, CancellationToken.None);
                    Check.Equal(1, tool.Count); Check.Equal(TurnStatus.Completed, completed.State.Turns.Single().Status);
                    Check.Equal(2, policy.Seen.Count);
                    Check.Equal(policy.Seen[0].OperationId, tool.LastContext!.OperationId);
                    Check.Equal(policy.Seen[0].Capabilities.Version, tool.LastContext.Capabilities.Version);
                    Check.Equal(completed.State.Id, completed.State.Resources.Single().OwnerTaskId);
                    Check.Equal(operation.Id, completed.State.Resources.Single().OwnerOperationId);
                    Check.Equal(1, completed.State.History.Count(h => h.OperationId == operation.Id));
                }
                var fault = new OutcomeFailureStore(store); var uncertainTool = new ContextCounter();
                var changedTool = new ContextCounter();
                await using (var changed = OperationRuntime(new ScriptedModel(HarnessTests.Call("counter", "{}"), HarnessTests.Answer("replanned")), store, changedTool, new ApprovalPolicy()))
                {
                    var pending = await changed.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, CancellationToken.None);
                    changedTool.Version = "changed-after-proposal";
                    var blocked = await changed.ExecuteAsync(new TaskCommand
                    { Kind = TaskCommandKind.Approval, TaskId = pending.State.Id, OperationId = pending.State.Operations.Single().Id, Approve = true }, CancellationToken.None);
                    Check.Equal(TurnStatus.Completed, blocked.State.Turns.Single().Status);
                    Check.Equal(ToolCallStatus.Rejected, blocked.State.Operations.Single().Outcome!.Status); Check.Equal(0, changedTool.Count);
                }
                var cancelledTool = new ContextCounter();
                await using (var cancelled = OperationRuntime(new ScriptedModel(HarnessTests.Call("counter", "{}")), store, cancelledTool, new ApprovalPolicy()))
                {
                    var pending = await cancelled.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, CancellationToken.None);
                    await cancelled.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = pending.State.Id }, CancellationToken.None);
                    await Check.ThrowsAsync<TaskConflictException>(() => cancelled.ExecuteAsync(new TaskCommand
                    { Kind = TaskCommandKind.Approval, TaskId = pending.State.Id, OperationId = pending.State.Operations.Single().Id, Approve = true }, CancellationToken.None));
                    Check.Equal(0, cancelledTool.Count);
                }
                string taskId;
                await using (var failing = OperationRuntime(new ScriptedModel(HarnessTests.Call("counter", "{}")), fault, uncertainTool))
                {
                    var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() };
                    taskId = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                    await Check.ThrowsAsync<CheckpointWriteException>(() => failing.ExecuteAsync(start, CancellationToken.None));
                    Check.Equal(1, uncertainTool.Count);
                    Check.Equal(OperationStatus.Started, (await store.LoadAsync(taskId, CancellationToken.None))!.Operations.Single().Status);
                }
                var recoveryModel = new ScriptedModel(HarnessTests.Answer("confirmed"));
                await using (var recovered = OperationRuntime(recoveryModel, store, uncertainTool))
                {
                    var resumed = await recovered.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = taskId }, CancellationToken.None);
                    Check.Equal(WaitReason.Reconciliation, resumed.State.Wait); Check.Equal(1, uncertainTool.Count);
                    Check.Equal(0, recoveryModel.Inputs.Count);
                    Check.Equal(EffectState.Unknown, resumed.State.Operations.Single().Effect);
                    var reconciled = await recovered.ExecuteAsync(new TaskCommand
                    {
                        Kind = TaskCommandKind.Reconcile, TaskId = taskId, OperationId = resumed.State.Operations.Single().Id,
                        ReconciledOutcome = new ToolOutcome { Status = ToolCallStatus.Succeeded, Output = "confirmed effect", Effect = EffectState.Confirmed }
                    }, CancellationToken.None);
                    Check.Equal(TurnStatus.Completed, reconciled.State.Turns.Single().Status);
                    Check.Equal(1, uncertainTool.Count); Check.Equal(2, reconciled.State.Budget.ModelCalls);
                }
                var finishedTool = new ContextCounter();
                string finishedId;
                await using (var stoppedAfterCommit = OperationRuntime(new ScriptedModel(HarnessTests.Call("counter", "{}")),
                    new OutcomeFailureStore(store, afterCommit: true), finishedTool))
                {
                    var request = HarnessTests.Request(); request.Options.ToolChoice = "counter";
                    var command = new TaskCommand { Kind = TaskCommandKind.Start, Request = request };
                    finishedId = "task_" + TaskJson.Hash(command.Id).Substring(0, 32);
                    await Check.ThrowsAsync<CheckpointWriteException>(() => stoppedAfterCommit.ExecuteAsync(command, CancellationToken.None));
                }
                var nextModel = new ScriptedModel(HarnessTests.Answer("finished"));
                await using (var resumeAfterCommit = OperationRuntime(nextModel, store, finishedTool))
                {
                    var continued = await resumeAfterCommit.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = finishedId }, CancellationToken.None);
                    Check.Equal(1, finishedTool.Count); Check.Equal("auto", nextModel.Inputs.Single().Options.ToolChoice);
                    Check.Equal(TurnStatus.Completed, continued.State.Turns.Single().Status);
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private static TaskRuntime OperationRuntime(IModelProvider model, ITaskStore store, ContextCounter tool, IContextualToolPolicy? policy = null)
            => new TaskRuntime(model, store, new HarnessOptions(), new ModelBinding { Model = "test", Protocol = "Responses", ConfigurationVersion = "v1" },
                tools: new ToolRegistry(new[] { tool }), toolPolicy: new AllowListToolPolicy(new[] { "counter" }), policy: policy);
        private sealed class ContextCounter : IContextualTool, IVersionedTool
        {
            public string Version { get; set; } = "1";
            public int Count { get; private set; }
            public ToolExecutionContext? LastContext { get; private set; }
            public ToolDefinition Definition { get; }
            public ContextCounter()
            {
                using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
                Definition = new ToolDefinition { Name = "counter", Parameters = schema.RootElement.Clone() };
            }
            public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) => throw new Exception("Missing execution context.");
            public Task<ToolOutcome> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
            {
                Count++; LastContext = TaskJson.Clone(context);
                return Task.FromResult(new ToolOutcome
                {
                    Status = ToolCallStatus.Succeeded, Effect = EffectState.Confirmed, Output = Count.ToString(),
                    Resources = { new ResourceReference { Id = "resource_" + context.OperationId, Kind = "test-reference", State = "finished" } }
                });
            }
        }
        private sealed class ApprovalPolicy : IContextualToolPolicy
        {
            public string Version => "approval-v1";
            public List<ToolExecutionContext> Seen { get; } = new List<ToolExecutionContext>();
            public Task<ToolAdmission> CheckAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
            {
                Seen.Add(TaskJson.Clone(context));
                return Task.FromResult(new ToolAdmission { Allowed = context.Approval == ApprovalState.Approved,
                    NeedsUser = context.Approval == ApprovalState.None, Reason = "approval required" });
            }
        }
        private sealed class OutcomeFailureStore : ITaskStore
        {
            private readonly ITaskStore inner;
            private bool failed;
            private readonly bool afterCommit;
            public OutcomeFailureStore(ITaskStore inner, bool afterCommit = false) { this.inner = inner; this.afterCommit = afterCommit; }
            public Task<AgentTaskState?> LoadAsync(string id, CancellationToken ct) => inner.LoadAsync(id, ct);
            public Task<TaskResponseReference?> ResolveResponseAsync(string id, CancellationToken ct) => inner.ResolveResponseAsync(id, ct);
            public async Task<ITaskLease> AcquireAsync(string id, CancellationToken ct) => new FaultLease(this, await inner.AcquireAsync(id, ct));
            private sealed class FaultLease : ITaskLease
            {
                private readonly OutcomeFailureStore owner;
                private readonly ITaskLease lease;
                public FaultLease(OutcomeFailureStore owner, ITaskLease lease) { this.owner = owner; this.lease = lease; }
                public Task<AgentTaskState?> LoadAsync(CancellationToken ct) => lease.LoadAsync(ct);
                public async Task CommitAsync(AgentTaskState state, long expectedRevision, CancellationToken ct)
                {
                    if (!owner.failed && state.Operations.Any(o => o.Status == OperationStatus.Finished))
                    {
                        owner.failed = true;
                        if (owner.afterCommit) await lease.CommitAsync(state, expectedRevision, ct);
                        throw new CheckpointWriteException(new IOException("Injected failure after effect."));
                    }
                    await lease.CommitAsync(state, expectedRevision, ct);
                }
                public ValueTask DisposeAsync() => lease.DisposeAsync();
            }
        }
    }
}
