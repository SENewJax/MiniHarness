using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness.Context;

namespace MiniHarness.Harness
{
    public sealed partial class TaskRuntime
    {
        private bool BindingCompatible(ModelBinding saved) => saved.Model == binding.Model &&
            (saved.Protocol == "Unspecified" || saved.Protocol == binding.Protocol) &&
            (saved.ConfigurationVersion.Length == 0 || saved.ConfigurationVersion == "legacy" || saved.ConfigurationVersion == binding.ConfigurationVersion);
        private static bool Terminal(AgentTaskState task) => task.Status == AgentTaskStatus.Completed || task.Status == AgentTaskStatus.Cancelled || task.Status == AgentTaskStatus.Failed;
        private void RefreshRecoveryState(AgentTaskState task)
        {
            var report = task.Recovery; var turn = task.Turns.LastOrDefault(); report.CanResume = false; report.CanWake = false;
            if (report.Classification != RecoveryClassification.HostAuthorization && report.Classification != RecoveryClassification.ModelConfiguration)
            {
                report.Blockers.Clear();
                if (Terminal(task)) report.Classification = RecoveryClassification.Terminal;
                else if (turn?.Status == TurnStatus.Completed) report.Classification = RecoveryClassification.Input;
                else if (task.Operations.Any(o => o.Status == OperationStatus.Unknown || o.Status == OperationStatus.Started) ||
                    task.ResourceInputs.Any(i => i.Status != "finished" && i.Status != "absent"))
                { report.Classification = RecoveryClassification.Reconciliation; report.Blockers.AddRange(task.Operations.Where(o => o.Status == OperationStatus.Unknown || o.Status == OperationStatus.Started).Select(o => o.Id));
                    report.Blockers.AddRange(task.ResourceInputs.Where(i => i.Status != "finished" && i.Status != "absent").Select(i => i.CommandId)); }
                else if (!task.Budget.Usage.Known || !HasModelBudget(task) && !HasLocalSettlement(task, turn))
                { report.Classification = RecoveryClassification.Budget; report.Blockers.AddRange(task.ModelInvocations.Where(i => (i.Status != ModelInvocationStatus.Responded && i.Status != ModelInvocationStatus.Reconciled) || !i.Usage.Known).Select(i => i.Id));
                    if (task.LegacyUnknown) report.Blockers.Add("legacy_unknown");
                    report.Blockers.AddRange(task.Children.Where(c => c.Collected && c.Usage?.Usage.Known == false).Select(c => c.TaskId)); }
                else if (UncertainResources(task))
                { report.Classification = RecoveryClassification.Resources; report.Blockers.AddRange(task.Resources.Where(r => !ResourceFinished(r) && r.State != "running").Select(r => r.Id)); }
                else if (task.Wait == WaitReason.Approval || task.Operations.Any(o => o.Status == OperationStatus.WaitingApproval)) report.Classification = RecoveryClassification.Approval;
                else if (task.Wait == WaitReason.ExternalResult) report.Classification = RecoveryClassification.ExternalResult;
                else if (task.Acceptance != null && !Acceptance.AcceptanceEvaluator.Evaluate(task).CanFinalize && task.Wait == WaitReason.Review)
                    report.Classification = RecoveryClassification.Review;
                else if (task.Acceptance?.Progress.Stopped == true && !Acceptance.AcceptanceEvaluator.Evaluate(task).CanFinalize)
                    report.Classification = RecoveryClassification.NoProgress;
                else if (task.Wait == WaitReason.Infrastructure) report.Classification = RecoveryClassification.Infrastructure;
                else if (task.Wait == WaitReason.ModelLimit || task.Wait == WaitReason.Evidence) report.Classification = RecoveryClassification.Reconciliation;
                else if (task.Status == AgentTaskStatus.Paused) { report.Classification = RecoveryClassification.Paused; report.CanResume = true; }
                else { report.Classification = RecoveryClassification.Ready; report.CanResume = turn != null; report.CanWake = true; }
            }
            if (report.Classification == RecoveryClassification.Paused || report.Classification == RecoveryClassification.Terminal || report.Classification == RecoveryClassification.Input)
                report.AutomaticAllowed = false;
            if (task.Acceptance != null)
                report.Blockers = report.Blockers.Concat(Acceptance.AcceptanceEvaluator.Evaluate(task).Blockers).Distinct().ToList();
            report.ContinuationToken = ContinuationToken(task);
        }
        private static bool HasLocalSettlement(AgentTaskState task, TaskTurn? turn)
        {
            if (turn == null) return false;
            // This admits only already-paid work. Advance still checks budgets before any new model request.
            bool finalResponse = turn.LastResponse?.Finish == ModelFinish.Stop && turn.LastResponse.Message.ToolCalls.Count == 0;
            if (task.Acceptance != null && finalResponse && Acceptance.AcceptanceEvaluator.Evaluate(task).CanFinalize) return true;
            return finalResponse && (turn.ResumeCompletionCandidate || task.ResourceWaits.Any(w => w.TurnId == turn.Id &&
                    w.CompletionCandidate && (w.Status == "active" || w.Status == "consumed"))) ||
                task.Budget.Usage.TotalTokens + task.Budget.ReservedTokens < task.Budget.MaxTokens &&
                task.Operations.Any(o => o.TurnId == turn.Id && o.Status == OperationStatus.Proposed);
        }
        private static void AlignRecoveryWait(AgentTaskState task)
        {
            var report = task.Recovery; var turn = task.Turns.LastOrDefault();
            if (Terminal(task) || turn == null || turn.Status == TurnStatus.Completed) return;
            if ((report.Classification == RecoveryClassification.Reconciliation && report.Blockers.Count > 0) || report.Classification == RecoveryClassification.Resources ||
                report.Classification == RecoveryClassification.Budget && !task.Budget.Usage.Known)
            {
                bool paused = task.Status == AgentTaskStatus.Paused;
                Wait(task, turn, report.Classification == RecoveryClassification.Budget ? WaitReason.Budget : WaitReason.Reconciliation, "recovery_requires_reconciliation");
                if (paused) { task.Status = AgentTaskStatus.Paused; turn.Status = TurnStatus.Interrupted; }
            }
        }
        private string ContinuationToken(AgentTaskState task)
        {
            var turn = task.Turns.LastOrDefault();
            return TaskJson.Hash(JsonSerializer.Serialize(new
            {
                HostGeneration, task.Id, Turn = turn?.Id, task.SteeringRevision, task.Budget.ModelCalls, task.Budget.ToolCalls,
                History = task.History.LastOrDefault()?.Id,
                Summary = task.ContextMemory.SummaryId,
                Waits = task.ResourceWaits.Select(w => new { w.Id, w.Status }), Evidence = task.Evidence.Select(e => e.Id)
            }, TaskJson.Options));
        }
        private async Task RecoverTaskAsync(AgentTaskState task, ITaskLease lease, bool explicitRecovery)
        {
            bool changedHost = task.Recovery.HostGeneration != HostGeneration;
            if (!changedHost && !explicitRecovery) return;
            TaskStateMigration.InitializeLedger(task);
            var report = task.Recovery; report.HostGeneration = HostGeneration; report.CheckedAtUtc = DateTimeOffset.UtcNow;
            report.CheckedRevision = task.Revision; report.Classification = RecoveryClassification.Ready;
            if (task.Acceptance != null && (delivery?.Authorizer == null || !delivery.Authorizer.ValidSpec(task, task.Facts.AcceptanceSpec!)))
            { report.Classification = RecoveryClassification.HostAuthorization; report.Blockers = new List<string> { "acceptance_configuration_changed" }; }
            report.AutomaticAllowed = recoveryOptions.Mode == RecoveryMode.Safe;
            if (!BindingCompatible(task.Model)) { report.Classification = RecoveryClassification.ModelConfiguration; report.Blockers = new List<string> { "model_binding_changed" }; }
            if (recoveryAuthorizer != null)
            {
                var authorization = await recoveryAuthorizer.CheckAsync(TaskJson.Clone(task), TaskJson.Clone(binding), CancellationToken.None);
                if (!authorization.Allowed) { report.Classification = RecoveryClassification.HostAuthorization; report.Blockers = new List<string> { authorization.Reason }; }
                else if (authorization.RestrictedFacts != null)
                { task.Facts = TaskJson.Clone(authorization.RestrictedFacts); if (task.Acceptance != null) ApplyRequiredEvidence(task); }
            }
            foreach (var operation in task.Operations.Where(o => o.Status == OperationStatus.Started))
            { operation.Status = OperationStatus.Unknown; operation.Effect = EffectState.Unknown; }
            foreach (var operation in task.Operations.Where(o => o.Status == OperationStatus.Unknown))
                foreach (var lookup in resourceManagers.Values.OfType<IResourceIntentLookup>())
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(recoveryOptions.ManagementTimeoutSeconds));
                    var found = await Task.Run(() => lookup.FindIntentAsync(task.Id, operation.Id, deadline.Token)).WaitAsync(deadline.Token);
                    if (found != null && found.OwnerTaskId == task.Id && found.OwnerOperationId == operation.Id && !task.Resources.Any(r => r.Id == found.Id))
                        task.Resources.Add(TaskJson.Clone(found));
                }
            foreach (var invocation in task.ModelInvocations.Where(i => i.Status == ModelInvocationStatus.Started))
            { invocation.Status = ModelInvocationStatus.Unknown; task.Budget.Usage.Known = false; task.Turns.Single(t => t.Id == invocation.TurnId).Usage.Known = false; }
            foreach (var step in task.Steps.Where(s => s.Status == StepStatus.Running || s.Status == StepStatus.Preparing))
            {
                if (step.Status == StepStatus.Running && !task.ModelInvocations.Any(i => i.StepId == step.Id))
                { task.LegacyUnknown = true; task.LegacyUsage.Known = false; task.Budget.Usage.Known = false;
                    var legacyTurn = task.LegacyTurns.SingleOrDefault(t => t.TurnId == step.TurnId); if (legacyTurn != null) legacyTurn.Usage.Known = false; }
                step.Status = StepStatus.Interrupted;
            }
            foreach (var compaction in task.Compactions.Where(c => c.Status == CompactionStatus.Started || c.Status == CompactionStatus.Planned))
            {
                compaction.Status = compaction.Status == CompactionStatus.Planned ? CompactionStatus.Failed : CompactionStatus.Unknown;
                compaction.Error = compaction.Status == CompactionStatus.Failed ? "planned_request_not_sent" : "unknown_summary_usage";
            }
            if (changedHost)
                foreach (var resource in task.Resources.Where(r => !ResourceFinished(r)).ToList())
                {
                    if (!resourceManagers.TryGetValue(resource.Kind, out var manager)) { resource.State = "unknown"; continue; }
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(recoveryOptions.ManagementTimeoutSeconds));
                    try
                    {
                        var observed = await Task.Run(() => manager.ExecuteAsync(TaskJson.Clone(resource), ResourceAction.Reconcile, deadline.Token)).WaitAsync(deadline.Token);
                        if (observed.Id != resource.Id || observed.Kind != resource.Kind || observed.OwnerTaskId != task.Id || observed.OwnerOperationId != resource.OwnerOperationId)
                            throw new InvalidDataException("Recovery resource ownership changed.");
                        task.Resources[task.Resources.IndexOf(resource)] = TaskJson.Clone(observed);
                    }
                    catch (Exception error) { resource.State = "unknown"; resource.Result = "Recovery: " + error.GetType().Name; }
                }
            UpdateUsageKnowledge(task);
            if (task.Acceptance != null)
                foreach (var attempt in task.Acceptance.Attempts.Where(a => a.Status == VerificationAttemptStatus.Pending &&
                    task.Operations.Any(o => o.Id == a.OperationId && o.Status == OperationStatus.Unknown))) attempt.Status = VerificationAttemptStatus.Unknown;
            foreach (var child in task.Children.Where(c => c.Collected && c.Usage?.Usage.Known == false).ToList())
                try { await CollectChildAsync(task, child.TaskId); } catch (TaskConflictException) { /* Child still busy/unsettled: retain the lower bound. */ }
            if (report.Classification != RecoveryClassification.ModelConfiguration && report.Classification != RecoveryClassification.HostAuthorization && !Terminal(task))
            {
                foreach (var invocation in task.ModelInvocations.Where(i => i.Purpose == ModelInvocationPurpose.Reasoning && i.Status == ModelInvocationStatus.Responded && !i.ResultApplied && i.Response != null))
                {
                    var turn = task.Turns.Single(t => t.Id == invocation.TurnId); var step = task.Steps.Single(s => s.Id == invocation.StepId);
                    ApplyReasoningResponse(task, turn, step, invocation);
                }
                foreach (var compaction in task.Compactions.Where(c => c.Status == CompactionStatus.Responded).ToList())
                    await RecoverSummaryAsync(task, compaction);
            }
            if (task.Status == AgentTaskStatus.Running) { task.Status = AgentTaskStatus.Ready; var turn = task.Turns.LastOrDefault(); if (turn != null) turn.Status = TurnStatus.Ready; }
            RefreshRecoveryState(task);
            var activeTurn = task.Turns.LastOrDefault();
            if (!Terminal(task) && activeTurn != null && activeTurn.Status != TurnStatus.Completed &&
                ((report.Classification == RecoveryClassification.Reconciliation && report.Blockers.Count > 0) || report.Classification == RecoveryClassification.Resources ||
                 report.Classification == RecoveryClassification.Budget && !task.Budget.Usage.Known))
            {
                bool paused = task.Status == AgentTaskStatus.Paused;
                Wait(task, activeTurn, report.Classification == RecoveryClassification.Budget ? WaitReason.Budget : WaitReason.Reconciliation, "recovery_requires_reconciliation");
                if (paused) { task.Status = AgentTaskStatus.Paused; activeTurn.Status = TurnStatus.Interrupted; }
            }
            Emit(task, "TaskRecovered", task.Turns.LastOrDefault(), report.Classification.ToString());
            await CommitAsync(lease, task); failures.TryRemove(task.Id, out _); FaultInjection?.Invoke("recovery_recorded");
        }
        private Task RecoverSummaryAsync(AgentTaskState task, CompactionRecord compaction)
        {
            try
            {
                if (!contextOptions.Enabled || compaction.SteeringRevision != task.SteeringRevision || !BindingCompatible(compaction.Binding)) throw new InvalidDataException("Summary recovery binding changed.");
                var invocation = task.ModelInvocations.Single(i => i.Id == compaction.InvocationId);
                compaction.Content = SummaryValidator.Read(invocation.Response!, task, compaction, contextOptions.MaxSummaryCharacters);
                compaction.ContentHash = TaskJson.Hash(JsonSerializer.Serialize(compaction.Content, TaskJson.Options));
                var turn = task.Turns.Single(t => t.Id == compaction.TurnId);
                var view = TaskProjection.ToRun(task, turn);
                view.Request.Instructions = "Task facts:\n" + JsonSerializer.Serialize(task.Facts, TaskJson.Options) + "\nAuthoritative runtime state:\n" + RuntimeContext(task) + "\n" + view.Request.Instructions;
                var composer = (IContextComposer)context;
                var projection = new ModelContextProjection(segmenter); var previous = ModelContextProjection.ResolveMemory(task);
                view.Request.Messages = projection.Project(task.History, previous == null ? new ContextMemory() : new ContextMemory
                    { SummaryId = previous.Id, CoveredCount = previous.CoveredCount }, previous?.Content);
                var capabilities = CaptureCapabilities(task); int before = composer.Measure(composer.ComposeDraft(view, capabilities.Tools));
                view.Request.Messages = projection.Project(task.History,
                    new ContextMemory { SummaryId = compaction.Id, CoveredCount = compaction.CoveredCount }, compaction.Content);
                int after = composer.Measure(composer.ComposeDraft(view, capabilities.Tools));
                if (after >= before * (1 - contextOptions.MinimumReductionRatio)) throw new InvalidDataException("Recovered summary has no sufficient capacity benefit.");
                if (HasPendingSteering(task.Id)) throw new InvalidDataException("Steering pending.");
                compaction.Status = CompactionStatus.Committed; invocation.ResultApplied = true;
                task.ContextMemory = new ContextMemory { SummaryId = compaction.Id, CoveredCount = compaction.CoveredCount, SourceHash = compaction.SourceHash };
            }
            catch (Exception error) { compaction.Status = CompactionStatus.Failed; compaction.Error = "recovery: " + error.Message; }
            return Task.CompletedTask;
        }
        private void ReconcileModelUsage(AgentTaskState task, ModelUsageReconciliation value)
        {
            if (recoveryAuthorizer?.AllowsUsageReconciliation != true) throw new TaskConflictException("Host policy does not authorize model usage reconciliation.");
            if (string.IsNullOrWhiteSpace(value.Basis) || value.Basis.Length > 4096 || !value.Usage.Known || value.Usage.InputTokens < 0 || value.Usage.OutputTokens < 0)
                throw new ArgumentException("Known usage and a trusted reconciliation basis are required.");
            TaskStateMigration.InitializeLedger(task);
            if (value.InvocationId == "legacy_unknown")
            {
                if (!task.LegacyUnknown) throw new TaskConflictException("Legacy usage is already settled.");
                if (value.Usage.InputTokens < task.LegacyUsage.InputTokens || value.Usage.OutputTokens < task.LegacyUsage.OutputTokens)
                    throw new ArgumentException("Legacy reconciliation cannot erase the recorded lower bound.");
                var unknownTurns = task.LegacyTurns.Where(t => !t.Usage.Known).ToList();
                if (value.LegacyTurns.Count != unknownTurns.Count || value.LegacyTurns.Select(t => t.TurnId).Distinct().Count() != unknownTurns.Count)
                    throw new ArgumentException("Provide the usage breakdown for every unknown legacy turn.");
                long inputDelta = 0, outputDelta = 0;
                foreach (var turn in unknownTurns)
                {
                    var updated = value.LegacyTurns.SingleOrDefault(t => t.TurnId == turn.TurnId);
                    if (updated == null || !updated.Usage.Known || updated.Usage.InputTokens < turn.Usage.InputTokens || updated.Usage.OutputTokens < turn.Usage.OutputTokens)
                        throw new ArgumentException("Invalid legacy turn usage.");
                    inputDelta += updated.Usage.InputTokens - turn.Usage.InputTokens; outputDelta += updated.Usage.OutputTokens - turn.Usage.OutputTokens;
                }
                if (unknownTurns.Count > 0 && (inputDelta != value.Usage.InputTokens - task.LegacyUsage.InputTokens || outputDelta != value.Usage.OutputTokens - task.LegacyUsage.OutputTokens))
                    throw new ArgumentException("Legacy task and turn usage deltas differ.");
                foreach (var old in unknownTurns)
                {
                    var updated = value.LegacyTurns.Single(t => t.TurnId == old.TurnId); var turn = task.Turns.Single(t => t.Id == old.TurnId);
                    turn.Usage.InputTokens += updated.Usage.InputTokens - old.Usage.InputTokens; turn.Usage.OutputTokens += updated.Usage.OutputTokens - old.Usage.OutputTokens;
                    old.Usage = TaskJson.Clone(updated.Usage);
                }
                task.Budget.Usage.InputTokens += value.Usage.InputTokens - task.LegacyUsage.InputTokens;
                task.Budget.Usage.OutputTokens += value.Usage.OutputTokens - task.LegacyUsage.OutputTokens;
                task.LegacyUsage = TaskJson.Clone(value.Usage); task.LegacyUnknown = false;
            }
            else
            {
                var record = task.ModelInvocations.SingleOrDefault(i => i.Id == value.InvocationId) ?? throw new ArgumentException("Unknown invocation ID.");
                if (record.UsageApplied && record.Usage.Known) throw new TaskConflictException("Invocation usage is already settled.");
                long oldInput = record.UsageApplied ? record.Usage.InputTokens : 0, oldOutput = record.UsageApplied ? record.Usage.OutputTokens : 0;
                if (value.Usage.InputTokens < oldInput || value.Usage.OutputTokens < oldOutput) throw new ArgumentException("Usage cannot erase recorded lower bounds.");
                var turn = task.Turns.Single(t => t.Id == record.TurnId);
                task.Budget.Usage.InputTokens += value.Usage.InputTokens - oldInput; task.Budget.Usage.OutputTokens += value.Usage.OutputTokens - oldOutput;
                turn.Usage.InputTokens += value.Usage.InputTokens - oldInput; turn.Usage.OutputTokens += value.Usage.OutputTokens - oldOutput;
                record.Usage = TaskJson.Clone(value.Usage); record.UsageApplied = true; record.Status = record.Response == null ? ModelInvocationStatus.Reconciled : ModelInvocationStatus.Responded;
                record.UsageReconciliation = (value.ConservativeUpperBound ? "upper_bound: " : "provider: ") + value.Basis;
                foreach (var compaction in task.Compactions.Where(c => c.InvocationId == record.Id && c.Status == CompactionStatus.Unknown))
                { compaction.Status = CompactionStatus.Failed; compaction.Error = "usage_reconciled_response_unavailable"; }
            }
            UpdateUsageKnowledge(task);
            foreach (var turn in task.Turns)
                turn.Usage.Known = !task.LegacyTurns.Any(t => t.TurnId == turn.Id && !t.Usage.Known) &&
                    task.ModelInvocations.Where(i => i.TurnId == turn.Id).All(i => (i.Status == ModelInvocationStatus.Responded || i.Status == ModelInvocationStatus.Reconciled) && i.UsageApplied && i.Usage.Known);
            Emit(task, "ModelUsageReconciled", task.Turns.LastOrDefault(), value.InvocationId);
        }
        public async Task<TaskHistoryPage> ReadHistoryAsync(string taskId, string? afterId, int maxBytes, CancellationToken ct)
        {
            if (maxBytes < 1024 || maxBytes > contextOptions.MaxHistoryReadBytes) throw new ArgumentException("Invalid history byte limit.");
            var task = await GetAsync(taskId, ct) ?? throw new ArgumentException("Task not found.");
            int index = afterId == null ? 0 : task.History.FindIndex(h => h.Id == afterId) + 1;
            if (afterId != null && index == 0) throw new ArgumentException("Unknown history cursor.");
            var result = new TaskHistoryPage { NextId = afterId };
            foreach (var entry in task.History.Skip(index))
            {
                result.Entries.Add(TaskJson.Clone(entry)); result.NextId = entry.Id; result.HasMore = true;
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, TaskJson.Options)) > maxBytes)
                {
                    result.Entries.RemoveAt(result.Entries.Count - 1);
                    result.NextId = result.Entries.LastOrDefault()?.Id ?? afterId;
                    if (result.Entries.Count == 0) throw new ArgumentException("One history entry exceeds this page budget; use a larger authorized limit.");
                    return result;
                }
                if (result.Entries.Count == 100) { result.HasMore = index + result.Entries.Count < task.History.Count; return result; }
            }
            result.HasMore = false; return result;
        }
        public Task<TaskExecutionFailure?> ReadFailureAsync(string taskId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); taskId = FileTaskStore.NormalizeId(taskId);
            return Task.FromResult(failures.TryGetValue(taskId, out var failure) ? TaskJson.Clone(failure) : null);
        }
        public async Task<TaskHistoryChunk> ReadHistoryEntryAsync(string taskId, string historyId, int offset, int maxBytes, CancellationToken ct)
        {
            if (maxBytes < 1024 || maxBytes > contextOptions.MaxHistoryReadBytes || offset < 0) throw new ArgumentException("Invalid history chunk bounds.");
            var task = await GetAsync(taskId, ct) ?? throw new ArgumentException("Task not found.");
            var entry = task.History.SingleOrDefault(h => h.Id == historyId) ?? throw new ArgumentException("History ID does not belong to this task.");
            string body = JsonSerializer.Serialize(entry, TaskJson.Options);
            if (offset > body.Length) throw new ArgumentException("History offset is past the end.");
            int length = Math.Min(body.Length - offset, maxBytes / 4);
            var chunk = new TaskHistoryChunk { HistoryId = historyId, Offset = offset, TotalCharacters = body.Length, ContentHash = TaskJson.Hash(body) };
            do
            {
                chunk.Data = body.Substring(offset, length); chunk.NextOffset = offset + length;
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(chunk, TaskJson.Options)) <= maxBytes) return chunk;
                length /= 2;
            } while (length > 0);
            throw new ArgumentException("History envelope exceeds the requested budget.");
        }
    }
}
