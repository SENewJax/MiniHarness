using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public sealed partial class TaskRuntime
    {
        private async Task RefreshWorkspaceAsync(AgentTaskState task, TaskTurn? turn, CancellationToken ct)
        {
            string revision = await workspaceRevision.GetRevisionAsync(TaskJson.Clone(task.Facts.Workspace), ct).ConfigureAwait(false);
            if (revision != task.Facts.Workspace.Revision)
            {
                task.Facts.Workspace.Revision = revision;
                Emit(task, "WorkspaceChanged", turn, revision);
            }
        }
        private async Task<bool> FinishCandidateAsync(AgentTaskState task, TaskTurn turn, ITaskLease lease, CancellationToken ct)
        {
            await RefreshWorkspaceAsync(task, turn, ct).ConfigureAwait(false);
            var verdict = await completion.EvaluateAsync(TaskJson.Clone(task), TaskJson.Clone(turn.LastResponse!), ct).ConfigureAwait(false);
            if (verdict.CompleteTask)
            {
                foreach (var resource in task.Resources.Where(r => !ResourceFinished(r)).ToList())
                    await ManageResourceAsync(task, lease, resource, ResourceAction.Query);
                var activeResource = task.Resources.FirstOrDefault(r => !ResourceFinished(r));
                if (activeResource != null)
                {
                    if (!task.ResourceWaits.Any(w => w.TurnId == turn.Id && w.Status == "active"))
                    {
                        int duration = resourceManagers.TryGetValue(activeResource.Kind, out var waitManager) && waitManager is IResourceWaitPolicy policy ? Math.Min(30000, policy.MaxWaitMilliseconds) : 30000;
                        RegisterWait(task, turn, new ResourceWaitRequest { ResourceId = activeResource.Id, Condition = ResourceWaitCondition.Exit, WaitMilliseconds = duration });
                        task.ResourceWaits.Last().CompletionCandidate = true;
                    }
                    Wait(task, turn, WaitReason.Resource, "unclosed_resource:" + activeResource.Id);
                    await CommitAsync(lease, task); return true;
                }
            }
            if (verdict.CompleteTask && task.Children.Any(c => !c.Collected))
            {
                Wait(task, turn, WaitReason.ExternalResult, "outstanding_child_tasks");
                await CommitAsync(lease, task).ConfigureAwait(false); return true;
            }
            if (verdict.CompleteTurn || verdict.CompleteTask)
            {
                if (await YieldForSteeringAsync(task, turn, lease)) return true;
                turn.Status = TurnStatus.Completed; turn.Wait = WaitReason.None; turn.StopReason = null;
                task.Status = verdict.CompleteTask ? AgentTaskStatus.Completed : AgentTaskStatus.Waiting;
                task.Wait = verdict.CompleteTask ? WaitReason.None : WaitReason.Input;
                Emit(task, verdict.CompleteTask ? "TaskCompleted" : "TurnCompleted", turn);
                await CommitAsync(lease, task).ConfigureAwait(false); return true;
            }
            if (verdict.CanContinue)
            {
                task.History.Add(new HistoryEntry { TurnId = turn.Id, Message = new Message { Role = "system", Text = verdict.Feedback } });
                turn.Wait = WaitReason.None; Emit(task, "CompletionFeedback", turn, verdict.Feedback);
                await CommitAsync(lease, task).ConfigureAwait(false); return false;
            }
            Wait(task, turn, verdict.WaitingReason, verdict.Feedback);
            await CommitAsync(lease, task).ConfigureAwait(false); return true;
        }
    }
}
