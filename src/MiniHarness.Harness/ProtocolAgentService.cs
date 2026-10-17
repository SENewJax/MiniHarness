using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    // OpenAI history branches start new tasks. Persistent task continuation uses
    // ITaskRuntime.Input explicitly, so branching cannot silently append to a goal.
    public sealed class ProtocolAgentService : IAgentService, IAgentRunner
    {
        private readonly ITaskRuntime runtime;
        private readonly ITaskStore store;
        private readonly ModelBinding binding;
        public ProtocolAgentService(ITaskRuntime runtime, ITaskStore store, ModelBinding binding)
        { this.runtime = runtime; this.store = store; this.binding = TaskJson.Clone(binding); }
        public Task<RunState> RunAsync(ModelRequest request, CancellationToken cancellationToken)
            => RespondAsync(new AgentInput { Request = request }, cancellationToken);
        public async Task<RunState> RespondAsync(AgentInput input, CancellationToken waitCancellation)
        {
            var request = TaskJson.Clone(input.Request);
            if (input.PreviousResponseId != null)
            {
                var previous = await ReadResponseAsync(input.PreviousResponseId, waitCancellation).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("Previous response not found.");
                if (previous.Status != RunStatus.Completed && previous.Status != RunStatus.WaitingForTools)
                    throw new TaskConflictException("Previous response cannot be continued automatically.");
                var original = (await store.LoadAsync(previous.TaskId!, waitCancellation).ConfigureAwait(false))!;
                if (original.Model.Model != binding.Model || (original.Model.Protocol != "Unspecified" && original.Model.Protocol != binding.Protocol) ||
                    (original.Model.ConfigurationVersion.Length > 0 && original.Model.ConfigurationVersion != "legacy" && original.Model.ConfigurationVersion != binding.ConfigurationVersion))
                    throw new TaskConflictException("Previous response belongs to another provider configuration.");
                request.Messages.InsertRange(0, previous.Request.Messages);
                if (!input.ToolsSpecified) request.Tools = TaskJson.Clone(previous.Request.Tools);
            }
            ConversationRules.Validate(request.Messages);
            var command = new TaskCommand { Id = input.CommandId, Kind = TaskCommandKind.Start, Request = request, ForkedFromResponseId = input.PreviousResponseId };
            waitCancellation.ThrowIfCancellationRequested();
            string taskId = "task_" + TaskJson.Hash(command.Id).Substring(0, 32);
            using var registration = waitCancellation.Register(() =>
            {
                if (input.CancelTaskWhenWaitCancelled)
                    _ = runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = taskId }, CancellationToken.None);
            });
            var result = await runtime.ExecuteAsync(command, waitCancellation).ConfigureAwait(false);
            return TaskProjection.ToRun(result.State, result.State.Turns.Single(t => t.Id == result.Receipt.TurnId));
        }
        public async Task<RunState?> ReadResponseAsync(string responseId, CancellationToken cancellationToken)
        {
            var reference = await store.ResolveResponseAsync(responseId, cancellationToken).ConfigureAwait(false);
            if (reference == null) return null;
            var task = await store.LoadAsync(reference.TaskId, cancellationToken).ConfigureAwait(false);
            var turn = task?.Turns.SingleOrDefault(t => t.Id == reference.TurnId);
            return turn == null || !turn.Settings.StoreResponse ? null : TaskProjection.ToRun(task!, turn);
        }
    }
}
