using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Coding.Processes;

namespace MiniHarness.Server
{
    // Notifications are coalesced hints. Both notification and fallback paths
    // submit commands; neither mutates a task nor calls a model directly.
    public sealed class ResourceWakeupService : BackgroundService
    {
        private readonly ITaskApplicationService application;
        private readonly ITaskStore store;
        private readonly TaskRuntime runtime;
        private readonly ProcessSessionManager manager;
        private readonly ProcessServerOptions options;
        private readonly ILogger<ResourceWakeupService> logger;
        private readonly ConcurrentDictionary<string, byte> pending = new ConcurrentDictionary<string, byte>();
        private readonly Channel<string> notifications = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        public ResourceWakeupService(ITaskApplicationService application, ITaskStore store, TaskRuntime runtime,
            ProcessSessionManager manager, ProcessServerOptions options, ILogger<ResourceWakeupService> logger)
        { this.application = application; this.store = store; this.runtime = runtime; this.manager = manager; this.options = options; this.logger = logger; }
        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            void Changed(ResourceReference resource) => notifications.Writer.TryWrite(resource.OwnerTaskId);
            manager.Changed += Changed;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var ids = store is ITaskCatalog catalog ? catalog.EnumerateTaskIds().ToHashSet() : new System.Collections.Generic.HashSet<string>();
                    while (notifications.Reader.TryRead(out var id)) ids.Add(id);
                    foreach (string id in ids)
                    {
                        try
                        {
                            var task = await application.GetAsync(id, stop);
                            if (task == null || task.Status == AgentTaskStatus.Running) continue;
                            if (task.Recovery.HostGeneration != runtime.HostGeneration) continue;
                            if (!task.ResourceWaits.Any(w => w.Status == "active"))
                            {
                                foreach (var owned in task.Resources.Where(r => r.Kind == manager.Kind))
                                {
                                    var current = await manager.ExecuteAsync(owned, ResourceAction.Query, stop);
                                    int hints = task.Events.Count(e => e.Type == "ResourceOutputAvailable" && e.Detail.StartsWith(owned.Id + ":", StringComparison.Ordinal));
                                    if (current.State != owned.State || current.Process!.TerminationConfirmed != owned.Process?.TerminationConfirmed ||
                                        hints < 32 && current.Process.LastSequence > (owned.Process?.LastSequence ?? 0))
                                    {
                                        Dispatch(new TaskCommand { Id = TaskJson.Hash("observe:" + owned.Id + ":" + current.State + ":" + current.Process!.Version),
                                            Kind = TaskCommandKind.QueryResource, TaskId = task.Id, ResourceId = owned.Id }, stop);
                                        break;
                                    }
                                }
                            }
                            if (task.Status == AgentTaskStatus.Cancelled || task.Status == AgentTaskStatus.Completed || task.Status == AgentTaskStatus.Failed) continue;
                            if ((!task.Recovery.CanWake || !task.Recovery.AutomaticAllowed) && task.Status != AgentTaskStatus.Paused) continue;
                            foreach (var wait in task.ResourceWaits.Where(w => w.Status == "active"))
                            {
                                var resource = task.Resources.Single(r => r.Id == wait.Request.ResourceId);
                                var current = await manager.ExecuteAsync(resource, ResourceAction.Query, stop);
                                bool expired = DateTimeOffset.UtcNow >= wait.DeadlineUtc;
                                bool ready = ResourceStates.IsTerminal(current.State) || wait.Request.Condition == ResourceWaitCondition.Output && current.Process!.LastSequence > wait.Request.AfterSequence;
                                if (!expired && !ready) continue;
                                // Observe failures without blocking other tasks on a full
                                // model turn; submission returns after durable acceptance.
                                Dispatch(new TaskCommand { Id = TaskJson.Hash(wait.Id + ":" + current.Process!.Version + ":" + expired),
                                    Kind = TaskCommandKind.ResourceWake, TaskId = task.Id, Automatic = true, Wake = new ResourceWake { WaitId = wait.Id } }, stop);
                            }
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                        catch (Exception ex) { logger.LogWarning(ex, "Resource wake observation failed for {TaskId}.", id); }
                    }
                    await Task.Delay(options.PollMilliseconds, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { manager.Changed -= Changed; }
        }
        private void Dispatch(TaskCommand command, CancellationToken stop)
        {
            if (pending.Count >= 64 || !pending.TryAdd(command.TaskId!, 0)) return;
            _ = DeliverAsync();
            async Task DeliverAsync()
            {
                try { await application.SubmitAsync(command, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception ex) { logger.LogWarning(ex, "Resource command dispatch failed for {TaskId}.", command.TaskId); }
                finally { pending.TryRemove(command.TaskId!, out _); }
            }
        }
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Stop automatic advancement, pause/drain the core, then reclaim OS
            // resources and reconcile only serializable state. No PID reattach.
            await base.StopAsync(cancellationToken);
            await runtime.StopAdvancementAsync();
            try
            {
            if (store is ITaskCatalog tasks)
                foreach (string id in tasks.EnumerateTaskIds())
                {
                    var task = await store.LoadAsync(id, CancellationToken.None);
                    if (task?.Resources.Any(r => r.Kind == manager.Kind) != true ||
                        task.Status == AgentTaskStatus.Completed || task.Status == AgentTaskStatus.Cancelled || task.Status == AgentTaskStatus.Failed || task.Status == AgentTaskStatus.Paused ||
                        task.Turns.LastOrDefault()?.Status == TurnStatus.Completed) continue;
                    await application.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Pause, TaskId = id }, CancellationToken.None);
                }
            }
            finally { await manager.DisposeAsync(); }
            if (store is ITaskCatalog catalog)
                foreach (var id in catalog.EnumerateTaskIds())
                {
                    var task = await store.LoadAsync(id, CancellationToken.None);
                    if (task?.Resources.Any(r => r.Kind == manager.Kind) != true) continue;
                    await application.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ReconcileResources, TaskId = id }, CancellationToken.None);
                }
        }
    }
}
