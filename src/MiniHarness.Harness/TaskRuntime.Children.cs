using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public sealed partial class TaskRuntime
    {
        private async Task<AgentTaskState> NewTaskAsync(string id, TaskCommand command)
        {
            var task = NewTask(id, command);
            if (command.ParentTaskId == null) return task;
            var parent = await store.LoadAsync(command.ParentTaskId, CancellationToken.None).ConfigureAwait(false);
            var link = parent?.Children.SingleOrDefault(c => c.TaskId == id && c.StartCommandId == command.Id && !c.Collected);
            if (link == null || parent!.Status == AgentTaskStatus.Cancelled || JsonSerializer.Serialize(command.Request, TaskJson.Options) != JsonSerializer.Serialize(link.Request, TaskJson.Options))
                throw new TaskConflictException("Child start requires a matching durable parent reservation.");
            task.ParentTaskId = parent!.Id;
            task.Facts = TaskJson.Clone(link.Facts);
            task.Budget = TaskJson.Clone(link.Allocation);
            return task;
        }
        private void ReserveChild(AgentTaskState task, TaskCommand command)
        {
            if (task.Turns.LastOrDefault()?.Status != TurnStatus.Completed)
                throw new TaskConflictException("Create children at a completed turn boundary.");
            var allocation = command.ChildBudget ?? throw new ArgumentException("Child budget required.");
            if (allocation.MaxModelCalls < 1 || allocation.MaxToolCalls < 0 || allocation.MaxTokens < 1 || !task.Budget.Usage.Known ||
                allocation.MaxModelCalls > task.Budget.MaxModelCalls - task.Budget.ModelCalls - task.Budget.ReservedModelCalls ||
                allocation.MaxToolCalls > task.Budget.MaxToolCalls - task.Budget.ToolCalls - task.Budget.ReservedToolCalls ||
                allocation.MaxTokens > task.Budget.MaxTokens - task.Budget.Usage.TotalTokens - task.Budget.ReservedTokens)
                throw new TaskConflictException("Child allocation exceeds the parent's available budget.");
            var request = TaskJson.Clone(command.Request ?? throw new ArgumentException("Child request required."));
            ValidateTurnRequest(new AgentTaskState { Model = TaskJson.Clone(task.Model) }, request);
            var facts = TaskJson.Clone(task.Facts);
            facts.Goal = request.Messages.FirstOrDefault(m => m.Role == "user")?.Text ?? facts.Goal;
            string startId = "child_" + TaskJson.Hash(task.Id + ":" + command.Id);
            task.Children.Add(new ChildTaskLink
            {
                TaskId = "task_" + TaskJson.Hash(startId).Substring(0, 32), StartCommandId = startId, Request = request, Facts = facts,
                Allocation = new TaskBudget { MaxModelCalls = allocation.MaxModelCalls, MaxToolCalls = allocation.MaxToolCalls, MaxTokens = allocation.MaxTokens }
            });
            task.Budget.ReservedModelCalls += allocation.MaxModelCalls;
            task.Budget.ReservedToolCalls += allocation.MaxToolCalls;
            task.Budget.ReservedTokens += allocation.MaxTokens;
            Emit(task, "ChildReserved", detail: task.Children.Last().TaskId);
        }
        private async Task DispatchChildrenAsync(AgentTaskState task)
        {
            foreach (var child in task.Children.Where(c => !c.Collected))
                await SubmitAsync(new TaskCommand { Id = child.StartCommandId, Kind = TaskCommandKind.Start,
                    ParentTaskId = task.Id, Request = TaskJson.Clone(child.Request) }, CancellationToken.None).ConfigureAwait(false);
        }
        private async Task CollectChildAsync(AgentTaskState task, string childId)
        {
            var link = task.Children.SingleOrDefault(c => c.TaskId == childId) ?? throw new TaskConflictException("Child does not belong to this task.");
            if (link.Collected) return;
            // The child lease prevents collection while a child operation is still
            // running. Close it durably before releasing the parent's reservation.
            await using var lease = await store.AcquireAsync(childId, CancellationToken.None).ConfigureAwait(false);
            var child = await lease.LoadAsync(CancellationToken.None).ConfigureAwait(false) ?? throw new TaskConflictException("Child has not started.");
            if (child.ParentTaskId != task.Id || child.Children.Any(c => !c.Collected)) throw new TaskConflictException("Invalid child ownership or outstanding descendants.");
            if (child.Resources.Any(r => !ResourceFinished(r))) throw new TaskConflictException("Child resources require confirmed cleanup before collection.");
            if (child.Turns.LastOrDefault()?.Status != TurnStatus.Completed && child.Status != AgentTaskStatus.Completed &&
                child.Status != AgentTaskStatus.Cancelled && child.Status != AgentTaskStatus.Failed)
                throw new TaskConflictException("Child has no final result yet.");
            if (child.Status == AgentTaskStatus.Waiting)
            {
                child.Status = AgentTaskStatus.Completed; child.Wait = WaitReason.None;
                Emit(child, "ChildClosed", detail: task.Id);
                await CommitAsync(lease, child).ConfigureAwait(false);
            }
            link.Usage = TaskJson.Clone(child.Budget);
            link.Result = child.Turns.LastOrDefault()?.LastResponse?.Message.Text ?? child.Status.ToString();
            link.Collected = true;
            task.Budget.ReservedModelCalls -= link.Allocation.MaxModelCalls;
            task.Budget.ReservedToolCalls -= link.Allocation.MaxToolCalls;
            task.Budget.ReservedTokens -= link.Allocation.MaxTokens;
            task.Budget.ModelCalls += child.Budget.ModelCalls;
            task.Budget.ToolCalls += child.Budget.ToolCalls;
            task.Budget.Usage.Add(child.Budget.Usage);
            task.History.Add(new HistoryEntry { TurnId = task.Turns.Last().Id, Message = new Message { Role = "system",
                Text = "Child task result: " + JsonSerializer.Serialize(new { child.Id, child.Status, link.Result, child.Budget }, TaskJson.Options) } });
            Emit(task, "ChildCollected", detail: child.Id);
        }
        private async Task CancelChildrenAsync(AgentTaskState task, string commandId)
        {
            foreach (var link in task.Children.Where(c => !c.Collected).ToList())
            {
                var child = await store.LoadAsync(link.TaskId, CancellationToken.None).ConfigureAwait(false);
                if (child == null)
                {
                    // The durable reservation may precede dispatch at a crash.
                    // Marking it collected also fences any delayed child Start.
                    link.Collected = true; link.Result = "cancelled_before_start"; link.Usage = new TaskBudget();
                    task.Budget.ReservedModelCalls -= link.Allocation.MaxModelCalls;
                    task.Budget.ReservedToolCalls -= link.Allocation.MaxToolCalls;
                    task.Budget.ReservedTokens -= link.Allocation.MaxTokens;
                    Emit(task, "ChildCancelledBeforeStart", detail: link.TaskId);
                    continue;
                }
                if (child.Status != AgentTaskStatus.Completed && child.Status != AgentTaskStatus.Failed)
                    await ExecuteAsync(new TaskCommand { Id = TaskJson.Hash(commandId + ":cancel:" + child.Id),
                        TaskId = child.Id, Kind = TaskCommandKind.Cancel }, CancellationToken.None).ConfigureAwait(false);
                var stopped = (await store.LoadAsync(child.Id, CancellationToken.None).ConfigureAwait(false))!;
                if (stopped.Resources.Any(r => !ResourceFinished(r)) || stopped.Children.Any(c => !c.Collected))
                { link.Result = "Cancellation requested; child cleanup is unconfirmed."; Emit(task, "ChildCancellationUnconfirmed", detail: child.Id); continue; }
                await CollectChildAsync(task, child.Id).ConfigureAwait(false);
            }
        }
    }
}
