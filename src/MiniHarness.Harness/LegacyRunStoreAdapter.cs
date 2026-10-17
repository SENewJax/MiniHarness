using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    // Kept per one-shot call. TaskRuntime owns all state transitions; this adapter
    // preserves old IRunStore integrations without another agent loop.
    internal sealed class LegacyRunStoreAdapter : ITaskStore
    {
        private readonly IRunStore store;
        private AgentTaskState? state;
        private bool owned;
        public LegacyRunStoreAdapter(IRunStore store) { this.store = store; }
        public Task<AgentTaskState?> LoadAsync(string id, CancellationToken ct) => Task.FromResult(state == null ? null : TaskJson.Clone(state));
        public Task<TaskResponseReference?> ResolveResponseAsync(string responseId, CancellationToken ct)
        {
            var turn = state?.Turns.SingleOrDefault(t => t.ResponseId == responseId);
            return Task.FromResult(turn == null ? null : new TaskResponseReference { TaskId = state!.Id, TurnId = turn.Id });
        }
        public Task<ITaskLease> AcquireAsync(string id, CancellationToken ct)
        {
            if (owned) throw new TaskConflictException("Task already owned.");
            owned = true; return Task.FromResult<ITaskLease>(new Lease(this));
        }
        private sealed class Lease : ITaskLease
        {
            private readonly LegacyRunStoreAdapter owner;
            public Lease(LegacyRunStoreAdapter owner) { this.owner = owner; }
            public Task<AgentTaskState?> LoadAsync(CancellationToken ct) => owner.LoadAsync("", ct);
            public async Task CommitAsync(AgentTaskState state, long expectedRevision, CancellationToken ct)
            {
                if ((owner.state?.Revision ?? 0) != expectedRevision) throw new TaskConflictException("Task revision changed.");
                state.Revision = expectedRevision + 1;
                foreach (var item in state.Events.Where(e => e.Revision == 0)) item.Revision = state.Revision;
                try { await owner.store.SaveAsync(TaskProjection.ToRun(state, state.Turns.Last()), ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { throw new CheckpointWriteException(ex); }
                owner.state = TaskJson.Clone(state);
            }
            public ValueTask DisposeAsync() { owner.owned = false; return ValueTask.CompletedTask; }
        }
    }
}
