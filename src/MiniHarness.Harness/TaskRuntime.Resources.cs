using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public sealed partial class TaskRuntime
    {
        private static bool ResourceFinished(ResourceReference resource)
            => resource.State == "finished" || resource.State == "cancelled" || resource.State == "failed";

        private async Task ManageResourcesAsync(AgentTaskState task, ITaskLease lease, TaskCommand command)
        {
            var selected = command.Kind == TaskCommandKind.ReconcileResources ? task.Resources.ToList()
                : task.Resources.Where(r => r.Id == command.ResourceId).ToList();
            if (command.Kind != TaskCommandKind.ReconcileResources && selected.Count != 1)
                throw new ArgumentException("Resource not found in this task.");
            var action = command.Kind == TaskCommandKind.CancelResource ? ResourceAction.Cancel
                : command.Kind == TaskCommandKind.QueryResource ? ResourceAction.Query : ResourceAction.Reconcile;
            foreach (var resource in selected) await ManageResourceAsync(task, lease, resource, action).ConfigureAwait(false);
        }
        private async Task CancelResourcesAsync(AgentTaskState task, ITaskLease lease)
        {
            foreach (var resource in task.Resources.Where(r => !ResourceFinished(r)).ToList())
                await ManageResourceAsync(task, lease, resource, ResourceAction.Cancel).ConfigureAwait(false);
        }
        private async Task ManageResourceAsync(AgentTaskState task, ITaskLease lease, ResourceReference resource, ResourceAction action)
        {
            if (action == ResourceAction.Cancel)
            {
                if (ResourceFinished(resource)) return;
                resource.State = "cancellation_requested";
                Emit(task, "ResourceCancellationRequested", detail: resource.Id);
                await CommitAsync(lease, task).ConfigureAwait(false);
            }
            if (!resourceManagers.TryGetValue(resource.Kind, out var manager))
            {
                Emit(task, "ResourceManagerUnavailable", detail: resource.Id);
                return;
            }
            var result = await manager.ExecuteAsync(TaskJson.Clone(resource), action, CancellationToken.None).ConfigureAwait(false);
            if (result.Id != resource.Id || result.Kind != resource.Kind || result.OwnerTaskId != task.Id ||
                result.OwnerOperationId != resource.OwnerOperationId || string.IsNullOrWhiteSpace(result.State))
                throw new TaskConflictException("Resource manager changed resource identity or ownership.");
            task.Resources[task.Resources.IndexOf(resource)] = TaskJson.Clone(result);
            Emit(task, "ResourceObserved", detail: resource.Id + ":" + result.State);
            await CommitAsync(lease, task).ConfigureAwait(false);
        }
    }
}
