using System.Linq;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public static class TaskProjection
    {
        public static RunState ToRun(AgentTaskState task, TaskTurn turn)
        {
            var request = TaskJson.Clone(turn.Settings);
            var turns = task.Turns.TakeWhile(t => t.Id != turn.Id).Select(t => t.Id).Append(turn.Id).ToHashSet();
            request.Messages = task.History.Where(h => turns.Contains(h.TurnId)).Select(h => TaskJson.Clone(h.Message)).ToList();
            RunStatus status = RunStatus.WaitingForUser;
            if (turn.Status == TurnStatus.Ready || turn.Status == TurnStatus.Running) status = RunStatus.Running;
            if (turn.Status == TurnStatus.Completed) status = RunStatus.Completed;
            if (turn.Wait == WaitReason.ExternalResult) status = RunStatus.WaitingForTools;
            if (turn.Wait == WaitReason.Budget) status = RunStatus.BudgetExhausted;
            if (task.Status == AgentTaskStatus.Cancelled || task.Status == AgentTaskStatus.Paused) status = RunStatus.Cancelled;
            if (turn.Status == TurnStatus.Failed) status = RunStatus.Failed;
            return new RunState
            {
                Id = turn.ResponseId, TaskId = task.Id, CreatedAt = turn.CreatedAt, Status = status, Request = request,
                StopReason = turn.StopReason, ModelCalls = turn.ModelCalls, ToolCalls = turn.ToolCalls,
                Usage = TaskJson.Clone(turn.Usage), LastResponse = TaskJson.Clone(turn.LastResponse),
                Executions = task.Operations.Where(o => o.TurnId == turn.Id &&
                    (o.Status == OperationStatus.Started || o.Status == OperationStatus.Finished || o.Status == OperationStatus.Unknown)).Select(o => new ToolExecution
                {
                    Call = TaskJson.Clone(o.Call), Status = o.Outcome?.Status ?? ToolCallStatus.Started,
                    Result = o.Outcome == null ? null : new ToolResult { CallId = o.Call.Id, Status = o.Outcome.Status, Output = o.Outcome.Output }
                }).ToList()
            };
        }
    }
}
