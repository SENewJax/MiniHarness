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
        private CapabilitySnapshot CaptureCapabilities(AgentTaskState task) => new CapabilitySnapshot
        {
            Version = TaskJson.Hash(JsonSerializer.Serialize(new
            {
                tools.Definitions,
                Implementations = tools.Definitions.Select(d =>
                {
                    tools.TryGet(d.Name, out var implementation);
                    return new { d.Name, Type = implementation?.GetType().FullName, Binary = implementation?.GetType().Module.ModuleVersionId,
                        Configuration = implementation is IVersionedTool versioned ? versioned.Version : "" };
                }).ToArray()
            }, TaskJson.Options)),
            PolicyVersion = policy.Version, Tools = TaskJson.Clone(tools.Definitions.ToList()), Workspace = TaskJson.Clone(task.Facts.Workspace)
        };
        private static string OperationFingerprint(AgentTaskState task, ToolOperation operation)
        {
            var step = task.Steps.Single(s => s.Id == operation.StepId);
            return TaskJson.Hash(JsonSerializer.Serialize(new { operation.Id, operation.Call, step.Capabilities }, TaskJson.Options));
        }
        private async Task<bool> ProcessOperationsAsync(AgentTaskState task, TaskTurn turn, ITaskLease lease, CancellationToken ct)
        {
            var pending = task.Operations.Where(o => o.TurnId == turn.Id && o.Status != OperationStatus.Finished).ToList();
            if (pending.Any(o => o.Status == OperationStatus.Unknown))
            {
                Wait(task, turn, WaitReason.Reconciliation, "unknown_side_effect"); await CommitAsync(lease, task).ConfigureAwait(false); return false;
            }
            if (pending.Any(o => o.ClientOwned))
            {
                bool mixed = pending.Any(o => !o.ClientOwned);
                Wait(task, turn, mixed ? WaitReason.Input : WaitReason.ExternalResult, mixed ? "mixed_tool_ownership" : "client_tool_calls");
                await CommitAsync(lease, task).ConfigureAwait(false); return false;
            }
            foreach (var operation in pending)
            {
                ct.ThrowIfCancellationRequested();
                await RefreshWorkspaceAsync(task, turn, ct).ConfigureAwait(false);
                var step = task.Steps.Single(s => s.Id == operation.StepId);
                var current = CaptureCapabilities(task);
                if (current.Version != step.Capabilities.Version || current.PolicyVersion != step.Capabilities.PolicyVersion ||
                    JsonSerializer.Serialize(task.Facts.Workspace, TaskJson.Options) != JsonSerializer.Serialize(step.Capabilities.Workspace, TaskJson.Options))
                {
                    // Close the stale suffix explicitly; never transfer an old
                    // approval to a new workspace or capability snapshot.
                    foreach (var stale in pending.Where(o => o.Status == OperationStatus.Proposed || o.Status == OperationStatus.WaitingApproval))
                        FinishOperation(task, turn, stale, new ToolOutcome { Status = ToolCallStatus.Rejected, Effect = EffectState.None,
                            Output = "Operation invalidated: capability or workspace changed. Replan with a fresh snapshot and obtain new approval if needed." });
                    Emit(task, "OperationsInvalidated", turn, "capability_or_workspace_changed");
                    await CommitAsync(lease, task).ConfigureAwait(false); return true;
                }
                if (task.Budget.ToolCalls + task.Budget.ReservedToolCalls >= task.Budget.MaxToolCalls)
                { Wait(task, turn, WaitReason.Budget, "tool_budget"); await CommitAsync(lease, task).ConfigureAwait(false); return false; }
                var context = new ToolExecutionContext
                {
                    TaskId = task.Id, TurnId = turn.Id, StepId = step.Id, OperationId = operation.Id,
                    Workspace = TaskJson.Clone(step.Capabilities.Workspace), Capabilities = TaskJson.Clone(step.Capabilities), Approval = operation.Approval
                };
                bool registered = tools.TryGet(operation.Call.Name, out var tool);
                var admission = registered ? await policy.CheckAsync(TaskJson.Clone(operation.Call), TaskJson.Clone(context), ct).ConfigureAwait(false)
                    : new ToolAdmission { Reason = "Unknown tool: " + operation.Call.Name };
                if (operation.Approval == ApprovalState.Denied) admission = new ToolAdmission { Reason = "Approval denied." };
                if (admission.NeedsUser)
                {
                    operation.Status = OperationStatus.WaitingApproval; operation.ApprovalFingerprint = OperationFingerprint(task, operation);
                    Wait(task, turn, WaitReason.Approval, admission.Reason); Emit(task, "ApprovalRequired", turn, admission.Reason, operation);
                    await CommitAsync(lease, task).ConfigureAwait(false); return false;
                }
                operation.Status = OperationStatus.Started; task.Budget.ToolCalls++; turn.ToolCalls++;
                Emit(task, "OperationStarted", turn, operation.Call.Name, operation);
                await CommitAsync(lease, task).ConfigureAwait(false);
                var outcome = admission.Allowed ? await executor.ExecuteAsync(tool!, TaskJson.Clone(operation.Call), context, ct).ConfigureAwait(false)
                    : new ToolOutcome { Status = ToolCallStatus.Rejected, Output = admission.Reason, Effect = EffectState.None };
                if (outcome.Effect == EffectState.Unknown || outcome.Effect == EffectState.Partial)
                {
                    operation.Outcome = outcome; operation.Effect = outcome.Effect; operation.Status = OperationStatus.Unknown;
                    Wait(task, turn, WaitReason.Reconciliation, "uncertain_tool_effect");
                    await CommitAsync(lease, task).ConfigureAwait(false); return false;
                }
                FinishOperation(task, turn, operation, outcome);
                await CommitAsync(lease, task).ConfigureAwait(false);
            }
            if (pending.Count > 0) turn.Settings.Options.ToolChoice = "auto";
            return true;
        }
        private static void FinishOperation(AgentTaskState task, TaskTurn turn, ToolOperation operation, ToolOutcome outcome)
        {
            outcome = TaskJson.Clone(outcome);
            foreach (var resource in outcome.Resources)
                if (string.IsNullOrWhiteSpace(resource.Id) || (resource.OwnerTaskId.Length > 0 && resource.OwnerTaskId != task.Id) ||
                    (resource.OwnerOperationId.Length > 0 && resource.OwnerOperationId != operation.Id) || task.Resources.Any(r => r.Id == resource.Id))
                    throw new InvalidOperationException("Invalid resource ownership.");
            if (outcome.Resources.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != outcome.Resources.Count)
                throw new InvalidOperationException("Duplicate resource identity.");
            if (outcome.Evidence.Any(e => e.TaskId.Length > 0 && e.TaskId != task.Id)) throw new InvalidOperationException("Invalid evidence ownership.");
            foreach (var resource in outcome.Resources)
            {
                resource.OwnerTaskId = task.Id; resource.OwnerOperationId = operation.Id;
                task.Resources.Add(TaskJson.Clone(resource));
            }
            operation.Outcome = outcome; operation.Status = OperationStatus.Finished; operation.Effect = outcome.Effect;
            // Continuation mode belongs to the same durable transition as the
            // last result. A crash before the next model step must not force a
            // second invocation of a named tool that has already completed.
            if (task.Operations.Where(o => o.TurnId == turn.Id).All(o => o.Status == OperationStatus.Finished))
                turn.Settings.Options.ToolChoice = "auto";
            foreach (var evidence in outcome.Evidence)
            {
                if (evidence.TaskId.Length > 0 && evidence.TaskId != task.Id) throw new InvalidOperationException("Invalid evidence ownership.");
                evidence.TaskId = task.Id; evidence.OperationId = operation.Id;
                foreach (var artifact in evidence.Artifacts) RegisterArtifact(task, artifact);
                task.Evidence.Add(TaskJson.Clone(evidence));
            }
            foreach (var artifact in outcome.Artifacts) RegisterArtifact(task, artifact);
            task.History.Add(new HistoryEntry
            {
                TurnId = turn.Id, StepId = operation.StepId, OperationId = operation.Id,
                Message = new Message { Role = "tool", ToolCallId = operation.Call.Id, Text = JsonSerializer.Serialize(new ToolResult
                { CallId = operation.Call.Id, Status = outcome.Status, Output = outcome.Output }) }
            });
            Emit(task, "OperationFinished", turn, outcome.Status.ToString(), operation);
        }
        private static void RegisterArtifact(AgentTaskState task, ArtifactReference artifact)
        {
            if (string.IsNullOrWhiteSpace(artifact.Id) || (artifact.OwnerTaskId.Length > 0 && artifact.OwnerTaskId != task.Id))
                throw new ArgumentException("Invalid artifact ownership.");
            artifact.OwnerTaskId = task.Id;
            var existing = task.Artifacts.SingleOrDefault(a => a.Id == artifact.Id);
            if (existing != null && (existing.ContentHash != artifact.ContentHash || existing.Location != artifact.Location))
                throw new TaskConflictException("Artifact ID was reused with another content version.");
            if (existing == null) task.Artifacts.Add(TaskJson.Clone(artifact));
        }
    }
}
