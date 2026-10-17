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
            if (verdict.CompleteTask && task.Children.Any(c => !c.Collected))
            {
                Wait(task, turn, WaitReason.ExternalResult, "outstanding_child_tasks");
                await CommitAsync(lease, task).ConfigureAwait(false); return true;
            }
            if (verdict.CompleteTurn || verdict.CompleteTask)
            {
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
