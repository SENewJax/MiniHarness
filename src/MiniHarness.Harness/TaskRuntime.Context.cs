using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness.Context;

namespace MiniHarness.Harness
{
    public sealed partial class TaskRuntime
    {
        private sealed class ContextAdvanceBudget { public int Calls { get; set; } }
        private string RuntimeContext(AgentTaskState task) => JsonSerializer.Serialize(new
        {
            task.Status, task.Wait,
            PendingOperations = task.Operations.Where(o => o.Status != OperationStatus.Finished).Take(20).Select(o => new { o.Id, o.Status, o.Call.Name }),
            Resources = task.Resources.Where(r => !ResourceFinished(r)).Take(20).Select(r => new { r.Id, r.State }),
            Children = task.Children.Where(c => !c.Collected).Take(20).Select(c => c.TaskId),
            EvidenceGaps = task.Acceptance == null ? task.Facts.RequiredEvidence.Where(r => !task.Evidence.Any(e => e.Requirement == r && e.Passed && e.WorkspaceRevision == task.Facts.Workspace.Revision)) :
                Acceptance.AcceptanceEvaluator.Evaluate(task).Criteria.Where(c => c.Status != AssessmentStatus.Passed).Select(c => c.CriterionId + ":" + c.Status),
            task.Budget.ModelCalls, task.Budget.ToolCalls, task.Budget.Usage,
            Note = "Use task IDs for full diagnostics. Summary claims cannot change these authoritative states."
        }, TaskJson.Options);
        private async Task<ModelRequest> ComposeTaskRequestAsync(AgentTaskState task, TaskTurn turn, TaskStep step, CancellationToken ct, CompactionRecord? candidate = null)
        {
            var memory = candidate ?? ModelContextProjection.ResolveMemory(task);
            var prepared = await preparation.PrepareAsync(new ContextPreparation
            {
                Facts = TaskJson.Clone(task.Facts), Step = TaskJson.Clone(step), History = TaskJson.Clone(task.History),
                Memory = memory == null ? new ContextMemory() : new ContextMemory { SummaryId = memory.Id, CoveredCount = memory.CoveredCount, SourceHash = memory.SourceHash },
                Summary = TaskJson.Clone(memory?.Content), RuntimeState = contextOptions.Enabled || task.Acceptance != null ? RuntimeContext(task) : ""
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var view = TaskProjection.ToRun(task, turn); view.Request.Messages = TaskJson.Clone(prepared.Messages);
            view.Request.Instructions = "Task facts:\n" + JsonSerializer.Serialize(task.Facts, TaskJson.Options) + "\n" +
                (contextOptions.Enabled || task.Acceptance != null ? "Authoritative runtime state:\n" + RuntimeContext(task) + "\n" : "") + view.Request.Instructions;
            view.Request.Options.MaxOutputTokens = (int)Math.Min(view.Request.Options.MaxOutputTokens,
                task.Budget.MaxTokens - task.Budget.Usage.TotalTokens - task.Budget.ReservedTokens);
            return context is IContextComposer composer ? composer.ComposeDraft(view, step.Capabilities.Tools) : context.Build(view, step.Capabilities.Tools);
        }
        private async Task<ModelRequest?> PrepareTaskRequestAsync(AgentTaskState task, TaskTurn turn, TaskStep step, ITaskLease lease,
            ContextAdvanceBudget advance, CancellationToken ct, bool force = false)
        {
            bool explicitRequest = force;
            var draft = await ComposeTaskRequestAsync(task, turn, step, ct);
            if (!contextOptions.Enabled) { if (context is IContextComposer oldComposer) oldComposer.ValidateCapacity(draft); return draft; }
            var composer = (IContextComposer)context;
            var memory = ModelContextProjection.ResolveMemory(task);
            if (task.ContextMemory.SummaryId != memory?.Id)
            {
                foreach (var damaged in task.Compactions.Where(c => c.Status == CompactionStatus.Committed && !ModelContextProjection.ValidSummary(task, c)))
                { damaged.Status = CompactionStatus.Failed; damaged.Error = "corrupt_memory_binding"; }
                task.ContextMemory = memory == null ? new ContextMemory() : new ContextMemory { SummaryId = memory.Id, CoveredCount = memory.CoveredCount, SourceHash = memory.SourceHash };
                Emit(task, "ContextMemoryRebuilt", turn); await CommitAsync(lease, task);
            }
            int before = composer.Measure(draft);
            task.ContextDiagnostics = new ContextDiagnostics { BeforeCharacters = before, AfterCharacters = before, HardLimit = options.MaxContextCharacters,
                Reason = force ? "explicit" : before >= options.MaxContextCharacters * contextOptions.TriggerRatio ? "soft_threshold" : "none", SummaryId = memory?.Id };
            var planner = new ContextPlanner(segmenter);
            while (force || composer.Measure(draft) >= options.MaxContextCharacters * contextOptions.TriggerRatio)
            {
                if (HasPendingSteering(task.Id)) return null;
                force = false;
                if (advance.Calls >= contextOptions.MaxSummaryCallsPerAdvance)
                { task.ContextDiagnostics.Reason = "summary_advance_limit"; break; }
                if (task.ModelInvocations.Count(i => i.Purpose == ModelInvocationPurpose.Summary) >= contextOptions.MaxSummaryCallsPerTask)
                { task.ContextDiagnostics.Reason = "summary_task_limit"; break; }
                var recentFailure = task.Compactions.LastOrDefault(c => c.Status == CompactionStatus.Failed);
                if (!explicitRequest && recentFailure != null && recentFailure.Error != "planned_request_not_sent" && recentFailure.Error != "control_pending" &&
                    task.Budget.ModelCalls - recentFailure.AttemptAtModelCall < contextOptions.FailureBackoffCalls) break;
                if (!HasModelBudget(task)) break;
                CompactionRecord? selected = null; ModelRequest? summaryRequest = null;
                foreach (int end in planner.Boundaries(task, contextOptions.KeepRecentGroups))
                {
                    ct.ThrowIfCancellationRequested();
                    var source = planner.Source(task, end); if (source.Count == 0 || source.Sum(h => h.Message.Text.Length + h.Message.ToolCalls.Sum(c => c.Arguments.Length)) < 256) continue;
                    var request = summaryGenerator.CreateRequest(binding.Model, memory?.Content, source,
                        (int)Math.Min(contextOptions.SummaryMaxOutputTokens, task.Budget.MaxTokens - task.Budget.Usage.TotalTokens - task.Budget.ReservedTokens), contextOptions.MaxSummaryCharacters);
                    if (composer.Measure(request) > options.MaxContextCharacters) break;
                    selected = new CompactionRecord { TurnId = turn.Id, CoveredCount = end, SourceIds = source.Select(h => h.Id).ToList(),
                        SourceHash = ModelContextProjection.PrefixHash(task, end), ParentSummaryId = memory?.Id,
                        Binding = TaskJson.Clone(binding), SteeringRevision = task.SteeringRevision, AttemptAtModelCall = task.Budget.ModelCalls };
                    selected.Key = TaskJson.Hash(selected.SourceHash + ":" + selected.ParentSummaryId + ":" + selected.TemplateVersion + ":" +
                        JsonSerializer.Serialize(binding, TaskJson.Options) + ":" + selected.SteeringRevision);
                    summaryRequest = request;
                }
                if (selected == null || summaryRequest == null) break;
                if (task.Compactions.Any(c => c.Key == selected.Key && c.Status != CompactionStatus.Failed)) break;
                task.Compactions.Add(selected); FaultInjection?.Invoke("summary_planned");
                await CommitAsync(lease, task);
                if (HasPendingSteering(task.Id)) { selected.Status = CompactionStatus.Failed; selected.Error = "control_pending"; await CommitAsync(lease, task); return null; }
                advance.Calls++;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(contextOptions.SummaryTimeoutSeconds));
                    var invocation = await InvokeModelAsync(task, turn, lease, summaryRequest, ModelInvocationPurpose.Summary, timeout.Token, compaction: selected);
                    selected.Content = SummaryValidator.Read(invocation.Response!, task, selected, contextOptions.MaxSummaryCharacters);
                    selected.ContentHash = TaskJson.Hash(JsonSerializer.Serialize(selected.Content, TaskJson.Options));
                    if (HasPendingSteering(task.Id) || selected.SteeringRevision != task.SteeringRevision || ct.IsCancellationRequested)
                    { selected.Status = CompactionStatus.Failed; selected.Error = "control_pending"; await CommitAsync(lease, task); return null; }
                    var compressed = await ComposeTaskRequestAsync(task, turn, step, ct, selected);
                    int after = composer.Measure(compressed);
                    if (after >= composer.Measure(draft) * (1 - contextOptions.MinimumReductionRatio)) throw new InvalidDataException("Summary has no sufficient capacity benefit.");
                    FaultInjection?.Invoke("summary_before_activation");
                    ct.ThrowIfCancellationRequested();
                    if (HasPendingSteering(task.Id)) { selected.Status = CompactionStatus.Failed; selected.Error = "control_pending"; await CommitAsync(lease, task); return null; }
                    selected.Status = CompactionStatus.Committed; invocation.ResultApplied = true;
                    task.ContextMemory = new ContextMemory { SummaryId = selected.Id, CoveredCount = selected.CoveredCount, SourceHash = selected.SourceHash };
                    task.ContextDiagnostics.AfterCharacters = after; task.ContextDiagnostics.SummaryCharacters = JsonSerializer.Serialize(selected.Content, TaskJson.Options).Length;
                    task.ContextDiagnostics.SummaryId = selected.Id; Emit(task, "ContextCompacted", turn, selected.Id);
                    await CommitAsync(lease, task); memory = selected; draft = compressed;
                    if (after <= options.MaxContextCharacters * contextOptions.TargetRatio) break;
                }
                catch (CheckpointWriteException) { throw; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    selected.Status = task.ModelInvocations.Any(i => i.CompactionId == selected.Id && i.Status == ModelInvocationStatus.Unknown)
                        ? CompactionStatus.Unknown : CompactionStatus.Failed;
                    selected.Error = error.GetType().Name + ": " + error.Message; Emit(task, "CompactionStopped", turn, selected.Error);
                    await CommitAsync(lease, task); break;
                }
            }
            if (!HasModelBudget(task))
            {
                task.ContextDiagnostics.Reason = "task_budget";
                if (turn.Status != TurnStatus.Completed) Wait(task, turn, WaitReason.Budget, "task_budget");
                else Emit(task, "CompactionStopped", turn, "task_budget");
                await CommitAsync(lease, task); return null;
            }
            // Recompose after costs and activation so the frozen request contains
            // current facts and remaining output budget, not the planning draft.
            draft = await ComposeTaskRequestAsync(task, turn, step, ct);
            task.ContextDiagnostics.AfterCharacters = composer.Measure(draft);
            if (composer.Measure(draft) > options.MaxContextCharacters)
            {
                var pinned = task.History.Where(segmenter.IsPinned).ToList();
                task.ContextDiagnostics.PinnedCharacters = JsonSerializer.Serialize(pinned, TaskJson.Options).Length;
                throw new ContextLimitException(task.ContextDiagnostics.Reason.StartsWith("summary_", StringComparison.Ordinal) ? task.ContextDiagnostics.Reason :
                    planner.Boundaries(task, contextOptions.KeepRecentGroups).Count == 0 ? "context_pinned_too_large" : "context_capacity");
            }
            ConversationRules.Validate(draft.Messages); composer.ValidateCapacity(draft);
            step.ContextMemoryId = task.ContextMemory.SummaryId; step.ContextSourceHash = ModelContextProjection.PrefixHash(task, task.History.Count);
            return draft;
        }
        private bool HasPendingSteering(string id)
        {
            if (!slots.TryGetValue(id, out var slot)) return false;
            lock (slot.Sync) return slot.Steering.Count > 0;
        }
    }
}
