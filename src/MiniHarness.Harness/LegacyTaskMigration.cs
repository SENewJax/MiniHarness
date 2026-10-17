using System;
using System.Linq;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public static class LegacyTaskMigration
    {
        public static AgentTaskState Convert(RunState legacy)
        {
            var task = new AgentTaskState
            {
                Id = FileTaskStore.NormalizeId(legacy.Id),
                Facts = new TaskFacts { Goal = legacy.Request.Messages.FirstOrDefault(m => m.Role == "user")?.Text ?? "" },
                Model = new ModelBinding
                {
                    Model = legacy.Request.Model,
                    Protocol = legacy.Request.Messages.Any(m => m.ProviderItems.Count > 0) ? "Responses" : "Unspecified",
                    ConfigurationVersion = "legacy"
                },
                Budget = new TaskBudget { ModelCalls = legacy.ModelCalls, ToolCalls = legacy.ToolCalls, Usage = TaskJson.Clone(legacy.Usage) }
            };
            var turn = new TaskTurn
            {
                Id = "turn_" + legacy.Id.Substring(5), ResponseId = legacy.Id, CreatedAt = legacy.CreatedAt,
                Settings = TaskJson.Clone(legacy.Request), ModelCalls = legacy.ModelCalls, ToolCalls = legacy.ToolCalls,
                Usage = TaskJson.Clone(legacy.Usage), LastResponse = TaskJson.Clone(legacy.LastResponse), StopReason = legacy.StopReason,
                Status = legacy.Status == RunStatus.Completed ? TurnStatus.Completed : TurnStatus.Waiting
            };
            turn.Settings.Messages.Clear();
            task.Turns.Add(turn);
            foreach (var message in legacy.Request.Messages)
                task.History.Add(new HistoryEntry { TurnId = turn.Id, Message = TaskJson.Clone(message) });
            foreach (var execution in legacy.Executions)
                task.Operations.Add(new ToolOperation
                {
                    TurnId = turn.Id, Call = TaskJson.Clone(execution.Call),
                    Status = execution.Status == ToolCallStatus.Started ? OperationStatus.Unknown : OperationStatus.Finished,
                    Effect = execution.Status == ToolCallStatus.Succeeded ? EffectState.Confirmed :
                        execution.Status == ToolCallStatus.Rejected ? EffectState.None : EffectState.Unknown,
                    Outcome = execution.Result == null ? null : new ToolOutcome
                    { Status = execution.Result.Status, Output = execution.Result.Output, Effect = execution.Result.Status == ToolCallStatus.Succeeded ? EffectState.Confirmed :
                        execution.Result.Status == ToolCallStatus.Rejected ? EffectState.None : EffectState.Unknown }
                });
            task.Status = AgentTaskStatus.Waiting;
            task.Wait = legacy.Status == RunStatus.Completed ? WaitReason.Input : WaitReason.ModelLimit;
            if (legacy.Status == RunStatus.WaitingForTools && legacy.LastResponse != null)
            {
                task.Wait = WaitReason.ExternalResult;
                foreach (var call in legacy.LastResponse.Message.ToolCalls)
                    task.Operations.Add(new ToolOperation { TurnId = turn.Id, Call = TaskJson.Clone(call), ClientOwned = true });
            }
            if (task.Operations.Any(o => o.Status == OperationStatus.Unknown)) task.Wait = WaitReason.Reconciliation;
            if (legacy.Status == RunStatus.Cancelled) task.Status = AgentTaskStatus.Cancelled;
            if (legacy.Status == RunStatus.Failed) task.Status = AgentTaskStatus.Failed;
            turn.Wait = task.Wait;
            return task;
        }
    }
}
