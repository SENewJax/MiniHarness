using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    // Source-compatible one-shot facade. All execution uses the persistent-task
    // engine; the legacy store is only an output/checkpoint projection adapter.
    public sealed class AgentRunner : IAgentRunner
    {
        private readonly IModelProvider model;
        private readonly IContextBuilder context;
        private readonly IToolRegistry tools;
        private readonly IToolPolicy policy;
        private readonly IToolExecutor executor;
        private readonly ICompletionPolicy completion;
        private readonly IRunStore store;
        private readonly HarnessOptions options;
        public AgentRunner(IModelProvider model, IContextBuilder context, IToolRegistry tools, IToolPolicy policy,
            IToolExecutor executor, ICompletionPolicy completion, IRunStore store, HarnessOptions options)
        {
            options.Validate(); this.model = model; this.context = context; this.tools = tools; this.policy = policy;
            this.executor = executor; this.completion = completion; this.store = store; this.options = options;
        }
        public async Task<RunState> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var cancelled = new RunState { Request = TaskJson.Clone(request), Status = RunStatus.Cancelled, StopReason = "cancelled" };
                await store.SaveAsync(cancelled, CancellationToken.None).ConfigureAwait(false); return cancelled;
            }
            await using var runtime = new TaskRuntime(model, new LegacyRunStoreAdapter(store), options,
                new ModelBinding { Model = request.Model, ConfigurationVersion = "legacy-adapter" }, context, tools, policy,
                executor: executor, completion: new LegacyCompletionPolicyAdapter(completion));
            var command = new TaskCommand { Kind = TaskCommandKind.Start, Request = request };
            string id = "task_" + TaskJson.Hash(command.Id).Substring(0, 32);
            Task<TaskCommandResult>? cancellation = null;
            try
            {
                var executing = runtime.ExecuteAsync(command, CancellationToken.None);
                using var registration = cancellationToken.Register(() => cancellation = runtime.ExecuteAsync(
                    new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = id }, CancellationToken.None));
                var result = await executing.ConfigureAwait(false);
                if (cancellation != null) result = await cancellation.ConfigureAwait(false);
                return TaskProjection.ToRun(result.State, result.State.Turns[0]);
            }
            catch (CheckpointWriteException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw;
            }
        }
    }
}
