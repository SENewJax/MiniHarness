using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness.Acceptance;
using MiniHarness.Harness.Delivery;

namespace MiniHarness.Harness
{
    public sealed partial class TaskRuntime
    {
        private async Task<TaskCompletionVerdict> AssessDeliveryAsync(AgentTaskState task, TaskTurn turn, CancellationToken ct)
        {
            await CheckProtectionAsync(task, ct);
            var state = task.Acceptance!;
            if (delivery!.Changes != null) state.ChangeConflicts = (await delivery.Changes.FreezeAsync(TaskJson.Clone(task), ct)).Conflicts;
            foreach (var artifact in state.FrozenArtifacts) await delivery!.Artifacts.ReadAsync(task.Id, artifact, ct);
            foreach (var step in task.Steps.Where(s => s.TurnId == turn.Id)) ProgressPolicy.Sample(task, step, delivery!.Options);
            var assessment = AcceptanceEvaluator.Evaluate(task); state.Assessment = assessment;
            bool modelRepair = assessment.Criteria.Any(item => task.Facts.AcceptanceSpec!.Criteria.Any(c => c.Id == item.CriterionId && c.Required && c.Kind == CriterionKind.VerificationProfile) &&
                item.Status != AssessmentStatus.Passed && item.Status != AssessmentStatus.Pending && item.Status != AssessmentStatus.Unknown);
            return new TaskCompletionVerdict { Assessment = assessment, CompleteTask = assessment.CanBuildCandidate,
                CanContinue = modelRepair && !assessment.CanBuildCandidate && !state.Progress.Stopped && assessment.Blockers.All(b => b == "review"),
                WaitingReason = state.ChangeConflicts.Count > 0 ? WaitReason.Reconciliation : state.Progress.Stopped ? WaitReason.NoProgress : WaitReason.Evidence,
                Feedback = JsonSerializer.Serialize(assessment, TaskJson.Options) };
        }
        private async Task<bool> FinalizeDeliveryAsync(AgentTaskState task, TaskTurn turn, ITaskLease lease, CancellationToken ct)
        {
            var services = delivery!; var state = task.Acceptance!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(services.Options.IoTimeoutSeconds));
            try
            {
                var changes = services.Changes == null ? new ChangeSetSnapshot() : await services.Changes.FreezeAsync(TaskJson.Clone(task), deadline.Token);
                if (changes.Conflicts.Count > 0)
                { state.Assessment.Blockers.AddRange(changes.Conflicts.Select(c => "change_conflict:" + c)); Wait(task, turn, WaitReason.Reconciliation, "change_set_conflict"); await CommitAsync(lease, task); return true; }
                string fingerprint = DeliveryManifestBuilder.CandidateHash(task, AcceptanceBinding.Hash(changes), turn.LastResponse!.Message.Text);
                if (state.Candidate?.Fingerprint != fingerprint)
                {
                    state.Candidate = new CompletionCandidate { Fingerprint = fingerprint, SpecHash = task.Facts.AcceptanceSpec!.ContentHash,
                        WorkspaceRevision = task.Facts.Workspace.Revision, SteeringRevision = task.SteeringRevision,
                        ChangeSetHash = AcceptanceBinding.Hash(changes), Explanation = turn.LastResponse.Message.Text };
                    state.Delivery = null;
                    Emit(task, "CompletionCandidateCreated", turn, fingerprint); await CommitAsync(lease, task);
                }
                state.Assessment = AcceptanceEvaluator.Evaluate(task);
                if (!state.Assessment.CanFinalize)
                { Wait(task, turn, state.Assessment.NeedsReview ? WaitReason.Review : WaitReason.Evidence, "delivery_blocked"); await CommitAsync(lease, task); return true; }
                state.Delivery ??= new DeliveryRecord { CandidateFingerprint = fingerprint, CreatedAtUtc = DateTimeOffset.UtcNow, Cost = TaskJson.Clone(task.Budget) };
                var record = state.Delivery; record.Status = DeliveryStatus.Preparing; record.Error = null;
                await CommitAsync(lease, task); FaultInjection?.Invoke("delivery_preparing");
                var manifest = new DeliveryManifest { TaskId = task.Id, CreatedAtUtc = record.CreatedAtUtc, Facts = TaskJson.Clone(task.Facts),
                    Candidate = TaskJson.Clone(state.Candidate), Assessment = TaskJson.Clone(state.Assessment), Protection = TaskJson.Clone(state.Protection),
                    Attempts = TaskJson.Clone(state.Attempts), Reviews = TaskJson.Clone(state.Reviews),
                    Artifacts = TaskJson.Clone(state.FrozenArtifacts.OrderBy(a => a.Id, StringComparer.Ordinal).ToList()),
                    Cost = TaskJson.Clone(record.Cost), Changes = changes, Resources = TaskJson.Clone(task.Resources) };
                manifest.Facts.Workspace.RootPath = "";
                var patch = await services.Artifacts.WriteAsync(task.Id, "changes.json", JsonSerializer.SerializeToUtf8Bytes(changes, TaskJson.Options), deadline.Token);
                var explanation = await services.Artifacts.WriteAsync(task.Id, "delivery.md", DeliveryManifestBuilder.Explain(manifest), deadline.Token);
                manifest.Artifacts.Add(patch); manifest.Artifacts.Add(explanation);
                record.Manifest = await services.Artifacts.WriteAsync(task.Id, "manifest.json", DeliveryManifestBuilder.Render(manifest), deadline.Token);
                record.Status = DeliveryStatus.Sealed;
                await CommitAsync(lease, task); FaultInjection?.Invoke("delivery_sealed");
                await RefreshWorkspaceAsync(task, turn, deadline.Token); await CheckProtectionAsync(task, deadline.Token);
                foreach (var artifact in state.FrozenArtifacts) await services.Artifacts.ReadAsync(task.Id, artifact, deadline.Token);
                var freshChanges = services.Changes == null ? new ChangeSetSnapshot() : await services.Changes.FreezeAsync(TaskJson.Clone(task), deadline.Token);
                if (state.ProtectionViolations.Count > 0 || freshChanges.Conflicts.Count > 0 ||
                    fingerprint != DeliveryManifestBuilder.CandidateHash(task, AcceptanceBinding.Hash(freshChanges), turn.LastResponse.Message.Text))
                { record.Status = DeliveryStatus.Invalidated; Wait(task, turn, WaitReason.Evidence, "candidate_dependencies_changed"); await CommitAsync(lease, task); return true; }
                FaultInjection?.Invoke("delivery_before_commit");
                var slot = slots[task.Id]; await slot.Admission.WaitAsync(CancellationToken.None);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    bool steering;
                    lock (slot.Sync)
                    {
                        if (slot.Controls.Count > 0) throw new OperationCanceledException("Control admitted before delivery commit.", ct);
                        steering = slot.Steering.Count > 0;
                    }
                    if (steering)
                    { record.Status = DeliveryStatus.Invalidated; Wait(task, turn, WaitReason.Input, "delivery_invalidated_by_steering"); await CommitAsync(lease, task); return true; }
                    // Validate the entire sealed package at the final admission boundary,
                    // including generated files, rather than only imported artifacts.
                    await services.Artifacts.ReadAsync(task.Id, record.Manifest, deadline.Token);
                    foreach (var artifact in manifest.Artifacts)
                        await services.Artifacts.ReadAsync(task.Id, artifact, deadline.Token);
                    record.Status = DeliveryStatus.Committed;
                    turn.Status = TurnStatus.Completed; turn.Wait = WaitReason.None; turn.StopReason = null; turn.ResponseHistoryCount ??= task.History.Count;
                    task.Status = AgentTaskStatus.Completed; task.Wait = WaitReason.None;
                    Emit(task, "TaskCompleted", turn, fingerprint); await CommitAsync(lease, task);
                }
                finally { slot.Admission.Release(); }
                FaultInjection?.Invoke("delivery_committed"); return true;
            }
            catch (CheckpointWriteException) { throw; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (state.Delivery != null) state.Delivery.Error = error.GetType().Name;
                state.Assessment.Blockers.Add("infrastructure");
                Wait(task, turn, WaitReason.Infrastructure, error.GetType().Name); await CommitAsync(lease, task); return true;
            }
        }
    }
}
