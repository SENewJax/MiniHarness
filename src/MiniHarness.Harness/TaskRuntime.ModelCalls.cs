using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public sealed partial class TaskRuntime
    {
        private bool HasModelBudget(AgentTaskState task) => task.Budget.ModelCalls + task.Budget.ReservedModelCalls < task.Budget.MaxModelCalls &&
            task.Budget.Usage.TotalTokens + task.Budget.ReservedTokens < task.Budget.MaxTokens &&
            (!options.StopWhenUsageUnknown || task.Budget.Usage.Known);
        private static void InterruptModelSteps(AgentTaskState task, TaskTurn turn)
        {
            TaskStateMigration.InitializeLedger(task);
            foreach (var step in task.Steps.Where(s => s.TurnId == turn.Id && (s.Status == StepStatus.Preparing || s.Status == StepStatus.Running)))
            {
                if (step.Status == StepStatus.Running)
                {
                    var invocation = task.ModelInvocations.SingleOrDefault(i => i.StepId == step.Id);
                    if (invocation == null)
                    {
                        task.LegacyUnknown = true; task.LegacyUsage.Known = false;
                        var legacy = task.LegacyTurns.SingleOrDefault(t => t.TurnId == turn.Id); if (legacy != null) legacy.Usage.Known = false;
                    }
                    else if (invocation.Status == ModelInvocationStatus.Started) invocation.Status = ModelInvocationStatus.Unknown;
                }
                step.Status = StepStatus.Interrupted;
            }
            UpdateUsageKnowledge(task);
        }
        private static void UpdateUsageKnowledge(AgentTaskState task)
        {
            bool known = !task.LegacyUnknown && task.ModelInvocations.All(i => (i.Status == ModelInvocationStatus.Responded || i.Status == ModelInvocationStatus.Reconciled) && i.UsageApplied && i.Usage.Known) &&
                task.Children.Where(c => c.Collected && c.Usage != null).All(c => c.Usage!.Usage.Known);
            task.Budget.Usage.Known = known;
            foreach (var turn in task.Turns)
                if (task.LegacyTurns.Any(t => t.TurnId == turn.Id && !t.Usage.Known) ||
                    task.ModelInvocations.Any(i => i.TurnId == turn.Id && ((i.Status != ModelInvocationStatus.Responded && i.Status != ModelInvocationStatus.Reconciled) || !i.Usage.Known))) turn.Usage.Known = false;
        }
        private async Task<ModelInvocationRecord> InvokeModelAsync(AgentTaskState task, TaskTurn turn, ITaskLease lease, ModelRequest request,
            ModelInvocationPurpose purpose, CancellationToken ct, TaskStep? step = null, CompactionRecord? compaction = null)
        {
            TaskStateMigration.InitializeLedger(task);
            request.InvocationPurpose = purpose;
            if (!HasModelBudget(task)) throw new ContextLimitException("task_budget");
            request.Options.MaxOutputTokens = (int)Math.Min(request.Options.MaxOutputTokens,
                task.Budget.MaxTokens - task.Budget.Usage.TotalTokens - task.Budget.ReservedTokens);
            if (context is IContextComposer composer) composer.ValidateCapacity(request);
            var record = new ModelInvocationRecord { TurnId = turn.Id, StepId = step?.Id, CompactionId = compaction?.Id,
                Purpose = purpose, Status = ModelInvocationStatus.Started, Binding = TaskJson.Clone(binding), Request = TaskJson.Clone(request) };
            task.ModelInvocations.Add(record); task.Budget.ModelCalls++; turn.ModelCalls++;
            if (step != null) { step.PreparedRequest = TaskJson.Clone(request); step.Status = StepStatus.Running; }
            if (compaction != null) { compaction.Status = CompactionStatus.Started; compaction.InvocationId = record.Id; }
            Emit(task, purpose == ModelInvocationPurpose.Summary ? "SummaryStarted" : "ModelStarted", turn, purpose == ModelInvocationPurpose.Summary ? record.Id : "");
            await CommitAsync(lease, task);
            try
            {
                FaultInjection?.Invoke(purpose == ModelInvocationPurpose.Summary ? "summary_started" : "model_started");
                var response = await model.GenerateAsync(TaskJson.Clone(request), ct).ConfigureAwait(false);
                if (response.Usage.InputTokens < 0 || response.Usage.OutputTokens < 0) throw new ModelException("Negative model usage.");
                record.Response = TaskJson.Clone(response); record.Usage = TaskJson.Clone(response.Usage);
                record.Status = ModelInvocationStatus.Responded; record.UsageApplied = true;
                task.Budget.Usage.Add(response.Usage); turn.Usage.Add(response.Usage);
                if (compaction != null) compaction.Status = CompactionStatus.Responded;
                if (purpose == ModelInvocationPurpose.Summary) Emit(task, "SummaryResponded", turn, record.Id);
                await CommitAsync(lease, task);
                FaultInjection?.Invoke(purpose == ModelInvocationPurpose.Summary ? "summary_responded" : "model_responded");
                return record;
            }
            catch (CheckpointWriteException) { throw; }
            catch
            {
                if (record.Status == ModelInvocationStatus.Started)
                {
                    record.Status = ModelInvocationStatus.Unknown; task.Budget.Usage.Known = false; turn.Usage.Known = false;
                    if (compaction != null) compaction.Status = CompactionStatus.Unknown;
                    Emit(task, "ModelUsageUnknown", turn, record.Id); await CommitAsync(lease, task);
                }
                throw;
            }
        }
        private void ApplyReasoningResponse(AgentTaskState task, TaskTurn turn, TaskStep step, ModelInvocationRecord invocation)
        {
            if (invocation.ResultApplied) return;
            var response = invocation.Response ?? throw new InvalidOperationException("No stored model response.");
            ToolCallRules.ValidateIdentity(response.Message.ToolCalls, task.History.Select(h => h.Message));
            step.Status = StepStatus.Responded; turn.LastResponse = TaskJson.Clone(response);
            task.History.Add(new HistoryEntry { TurnId = turn.Id, StepId = step.Id, Source = "model", Message = TaskJson.Clone(response.Message) });
            invocation.ResultApplied = true; Emit(task, "ModelResponded", turn);
            if (response.Finish == ModelFinish.Length || response.Finish == ModelFinish.Refused || response.Finish == ModelFinish.ContentFiltered)
            { Wait(task, turn, WaitReason.ModelLimit, response.Finish.ToString()); return; }
            ToolCallRules.ValidateChoice(invocation.Request.Options.ToolChoice, response.Message.ToolCalls);
            foreach (var call in response.Message.ToolCalls)
                task.Operations.Add(new ToolOperation { TurnId = turn.Id, StepId = step.Id, Call = TaskJson.Clone(call),
                    ClientOwned = turn.Settings.Tools.Any(t => t.Name == call.Name) });
            turn.ResumeCompletionCandidate = response.Message.ToolCalls.Count == 0 && response.Finish == ModelFinish.Stop;
        }
    }
}
