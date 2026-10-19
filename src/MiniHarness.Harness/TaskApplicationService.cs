using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    // One application boundary for protocol and native requests. The runtime
    // owns execution; this service owns host initialization and caller intent.
    public sealed class TaskApplicationService : ITaskApplicationService, IResourceOutputAccess
    {
        private readonly ITaskRuntime runtime;
        private readonly ITaskInitializer? initializer;
        public TaskApplicationService(ITaskRuntime runtime, ITaskInitializer? initializer = null)
        { this.runtime = runtime; this.initializer = initializer; }
        private TaskCommand Prepare(TaskCommand command)
        {
            var copy = TaskJson.Clone(command);
            if (copy.Kind == TaskCommandKind.Start) initializer?.Initialize(copy);
            return copy;
        }
        public Task<TaskCommandResult> ExecuteAsync(TaskCommand command, CancellationToken cancellationToken)
            => ExecuteAsync(command, new TaskExecutionOptions(), cancellationToken);
        public Task<TaskSubmission> SubmitAsync(TaskCommand command, CancellationToken cancellationToken)
            => SubmitAsync(command, new TaskExecutionOptions(), cancellationToken);
        public async Task<TaskCommandResult> ExecuteAsync(TaskCommand command, TaskExecutionOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = Prepare(command);
            var execution = runtime.ExecuteAsync(prepared, CancellationToken.None);
            _ = execution.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            try { return await execution.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (options.CancelTaskWhenWaitCancelled) await CancelAcceptedAsync(prepared).ConfigureAwait(false);
                throw;
            }
        }
        public async Task<TaskSubmission> SubmitAsync(TaskCommand command, TaskExecutionOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = Prepare(command);
            var acceptance = runtime.SubmitAsync(prepared, CancellationToken.None);
            _ = acceptance.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            try { return await acceptance.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (options.CancelTaskWhenWaitCancelled) await CancelAcceptedAsync(prepared).ConfigureAwait(false);
                throw;
            }
        }
        private async Task CancelAcceptedAsync(TaskCommand command)
        {
            // Start must be durable before Cancel can find its task. Other commands
            // may be queued behind long work, so never wait for their acceptance.
            string id = command.Kind == TaskCommandKind.Start ?
                (await runtime.SubmitAsync(command, CancellationToken.None).ConfigureAwait(false)).TaskId : command.TaskId!;
            try
            {
                await runtime.ExecuteAsync(new TaskCommand { Id = TaskJson.Hash("cancel-wait:" + command.Id),
                    Kind = TaskCommandKind.Cancel, TaskId = id }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TaskConflictException)
            {
                var state = await runtime.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
                if (state == null || (state.Status != AgentTaskStatus.Completed && state.Status != AgentTaskStatus.Cancelled && state.Status != AgentTaskStatus.Failed)) throw;
            }
        }
        public Task<AgentTaskState?> GetAsync(string id, CancellationToken ct) => runtime.GetAsync(id, ct);
        public Task<IReadOnlyList<TaskEvent>> ObserveAsync(string id, long after, CancellationToken ct) => runtime.ObserveAsync(id, after, ct);
        public Task<ProcessOutputPage> ReadResourceOutputAsync(string taskId, string resourceId, long after, int maxBytes, CancellationToken ct)
            => runtime is IResourceOutputAccess access ? access.ReadResourceOutputAsync(taskId, resourceId, after, maxBytes, ct)
                : throw new ArgumentException("Resource output is unavailable.");
    }
}
