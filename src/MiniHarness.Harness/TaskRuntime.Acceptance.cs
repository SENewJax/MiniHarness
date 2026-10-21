using System;
using System.Collections.Generic;
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
        private readonly DeliveryServices? delivery;
        private async Task InitializeAcceptanceAsync(AgentTaskState task)
        {
            if (task.Facts.AcceptanceSpec == null) return;
            if (delivery == null) throw new ArgumentException("V4 services are not configured.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(delivery.Options.IoTimeoutSeconds));
            AcceptanceBinding.Validate(task.Facts.AcceptanceSpec);
            if (delivery.Authorizer == null || !delivery.Authorizer.ValidSpec(task, task.Facts.AcceptanceSpec)) throw new TaskConflictException("Acceptance contract is not host authorized.");
            task.Acceptance = new AcceptanceState { Protection = await SafeFiles.Capture(task.Facts.Workspace.RootPath, task.Facts.AcceptanceSpec,
                delivery.Options.MaxArtifactBytes, deadline.Token) };
            if (delivery.Changes != null)
                task.Acceptance.WorkspaceBaseline = await delivery.Changes.CaptureBaselineAsync(task.Facts.Workspace, deadline.Token);
            ApplyRequiredEvidence(task);
        }
        private static void ApplyRequiredEvidence(AgentTaskState task) => task.Facts.RequiredEvidence = task.Facts.AcceptanceSpec!.Criteria
            .Where(c => c.Required && c.Kind == CriterionKind.VerificationProfile).Select(c => "verification:" + c.Profile).ToList();
        private async Task CheckProtectionAsync(AgentTaskState task, CancellationToken ct)
        {
            if (task.Acceptance != null) task.Acceptance.ProtectionViolations = await SafeFiles.Check(task.Facts.Workspace.RootPath,
                task.Facts.AcceptanceSpec!, task.Acceptance.Protection, delivery!.Options.MaxArtifactBytes, ct);
        }
        private async Task AcceptToolEvidenceAsync(AgentTaskState task, ToolOperation operation, ToolOutcome outcome, CancellationToken ct)
        {
            if (task.Acceptance == null) return;
            var attempts = task.Acceptance.Attempts.Where(a => a.OperationId == operation.Id).ToArray();
            var proposed = TaskJson.Clone(task);
            foreach (var attempt in attempts)
            {
                var evidence = outcome.Evidence.SingleOrDefault(e => e.Requirement == "verification:" + task.Facts.AcceptanceSpec!.Criteria.Single(c => c.Id == attempt.CriterionId).Profile);
                if (evidence == null)
                {
                    proposed.Acceptance!.Attempts.Single(a => a.Id == attempt.Id).Status = outcome.Effect == EffectState.Unknown || outcome.Status == ToolCallStatus.TimedOut
                        ? VerificationAttemptStatus.Unknown : VerificationAttemptStatus.Cancelled;
                    continue;
                }
                evidence = TaskJson.Clone(evidence); evidence.TaskId = task.Id; evidence.OperationId = operation.Id;
                var frozen = new List<ArtifactReference>();
                foreach (var artifact in evidence.Artifacts) frozen.Add(await delivery!.Artifacts.ImportAsync(task.Id, artifact, ct));
                evidence.Artifacts = frozen;
                EvidenceAdmission.Settle(proposed, new EvidenceEnvelope { AttemptId = attempt.Id, Source = "tool:" + operation.Call.Name,
                    Status = evidence.Passed ? VerificationAttemptStatus.Passed : VerificationAttemptStatus.Failed, Evidence = evidence }, false);
                foreach (var artifact in frozen) UpsertArtifact(proposed, artifact);
            }
            task.Acceptance = proposed.Acceptance;
        }
        private void UpsertArtifact(AgentTaskState task, ArtifactReference artifact)
        {
            if (artifact.Id == "changes.json" || artifact.Id == "manifest.json" || artifact.Id == "delivery.md") throw new ArgumentException("Reserved delivery artifact name.");
            var list = task.Acceptance!.FrozenArtifacts;
            var old = list.SingleOrDefault(a => a.Id == artifact.Id);
            if (old == null && list.Count >= delivery!.Options.MaxArtifacts) throw new IOException("Task artifact reference quota exceeded.");
            if (old != null && (old.ContentHash != artifact.ContentHash || (old.LogicalName ?? old.Id) != (artifact.LogicalName ?? artifact.Id))) throw new TaskConflictException("Frozen artifact identity reused.");
            if (old != null) artifact.Generation = old.Generation;
            else { artifact.Generation = checked(list.Count == 0 ? 1 : list.Max(a => a.Generation ?? 0) + 1); list.Add(TaskJson.Clone(artifact)); }
        }
        private async Task<bool> ExecuteAcceptanceCommandAsync(AgentTaskState task, TaskCommand command, ITaskLease lease, CancellationToken ct)
        {
            if (task.Acceptance == null || command.Acceptance == null) throw new ArgumentException("V4 task and payload required.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(delivery!.Options.IoTimeoutSeconds)); ct = deadline.Token;
            var input = command.Acceptance; var state = task.Acceptance;
            if (command.Kind == TaskCommandKind.FinalizeDelivery && input.ExpectedHash.Length > 0 && input.ExpectedHash != state.Candidate?.Fingerprint)
                throw new TaskConflictException("Stale finalize candidate.");
            if (command.Kind != TaskCommandKind.FinalizeDelivery && command.Kind != TaskCommandKind.VerifyDelivery &&
                !(delivery!.Authorizer?.Allows(task, command.Kind) ?? false)) throw new TaskConflictException("Host has not authorized this acceptance command.");
            switch (command.Kind)
            {
                case TaskCommandKind.AmendAcceptanceSpec:
                    if (input.ExpectedHash != task.Facts.AcceptanceSpec!.ContentHash || input.Spec == null || string.IsNullOrWhiteSpace(input.Reason)) throw new TaskConflictException("Spec revision conflict.");
                    var next = TaskJson.Clone(input.Spec); AcceptanceBinding.Validate(next);
                    if (next.Revision != task.Facts.AcceptanceSpec.Revision + 1 || !delivery!.Authorizer!.ValidSpec(task, next) ||
                        !next.ProtectedPaths.SequenceEqual(task.Facts.AcceptanceSpec.ProtectedPaths) || !next.AllowNewFilesUnder.SequenceEqual(task.Facts.AcceptanceSpec.AllowNewFilesUnder))
                        throw new TaskConflictException("Host requirements or frozen protection policy cannot be weakened.");
                    task.Facts.AcceptanceSpec = next; ApplyRequiredEvidence(task); state.Candidate = null; state.Delivery = null; task.Recovery.Classification = RecoveryClassification.Ready; break;
                case TaskCommandKind.BeginVerification:
                    EvidenceAdmission.Begin(task, input.CriterionId, input.AttemptId, "external:" + input.AttemptId); break;
                case TaskCommandKind.SubmitVerification:
                case TaskCommandKind.ReconcileVerification:
                    var envelope = TaskJson.Clone(input.Evidence ?? throw new ArgumentException("Evidence required."));
                    envelope.Source = "host:" + delivery!.Authorizer!.Actor;
                    var draft = TaskJson.Clone(task);
                    var frozenEvidence = new List<ArtifactReference>();
                    foreach (var artifact in envelope.Evidence.Artifacts)
                    { var frozen = await delivery.Artifacts.ImportAsync(task.Id, artifact, ct); UpsertArtifact(draft, frozen); frozenEvidence.Add(frozen); }
                    envelope.Evidence.Artifacts = frozenEvidence;
                    try { EvidenceAdmission.Settle(draft, envelope, command.Kind == TaskCommandKind.ReconcileVerification); }
                    catch (TaskConflictException)
                    {
                        string diagnostic = "evidence_conflict:" + command.Id;
                        if (!task.Acceptance!.Diagnostics.Contains(diagnostic))
                        { if (task.Acceptance.Diagnostics.Count >= 128) task.Acceptance.Diagnostics.RemoveAt(0); task.Acceptance.Diagnostics.Add(diagnostic); await CommitAsync(lease, task); }
                        throw;
                    }
                    task.Acceptance = draft.Acceptance; break;
                case TaskCommandKind.RegisterDeliveryArtifact:
                    UpsertArtifact(task, await delivery!.Artifacts.ImportAsync(task.Id, input.Artifact ?? throw new ArgumentException("Artifact required."), ct)); break;
                case TaskCommandKind.ReviewDelivery:
                    var review = TaskJson.Clone(input.Review ?? throw new ArgumentException("Review required."));
                    if (state.Candidate == null || review.CandidateFingerprint != state.Candidate.Fingerprint) throw new TaskConflictException("Stale review candidate.");
                    await RefreshWorkspaceAsync(task, task.Turns.LastOrDefault(), ct); await CheckProtectionAsync(task, ct);
                    var currentChanges = delivery!.Changes == null ? new ChangeSetSnapshot() : await delivery.Changes.FreezeAsync(TaskJson.Clone(task), ct);
                    if (state.ProtectionViolations.Count > 0 || currentChanges.Conflicts.Count > 0 ||
                        review.CandidateFingerprint != DeliveryManifestBuilder.CandidateHash(task, AcceptanceBinding.Hash(currentChanges), task.Turns.Last().LastResponse!.Message.Text))
                    {
                        state.Candidate = null;
                        if (state.Delivery != null) state.Delivery.Status = DeliveryStatus.Invalidated;
                        Wait(task, task.Turns.Last(), WaitReason.Evidence, "stale_review_candidate");
                        await CommitAsync(lease, task); throw new TaskConflictException("Candidate dependencies changed; review is stale.");
                    }
                    var prior = state.Reviews.SingleOrDefault(r => r.CandidateFingerprint == review.CandidateFingerprint);
                    review.Actor = delivery!.Authorizer!.Actor;
                    if (prior != null && AcceptanceBinding.Hash(prior) != AcceptanceBinding.Hash(review)) throw new TaskConflictException("Conflicting review decision.");
                    if (prior == null) state.Reviews.Add(review);
                    if (!review.Accept)
                    {
                        state.Candidate = null; state.Delivery = null; task.SteeringRevision++;
                        task.History.Add(new HistoryEntry { TurnId = task.Turns.Last().Id, Source = "steering", Message = new Message { Text = "Delivery rejected: " + review.Comment } });
                        task.Turns.Last().ResumeCompletionCandidate = false;
                        return task.Status != AgentTaskStatus.Paused;
                    }
                    break;
                case TaskCommandKind.ContinueAfterNoProgress:
                    if (!state.Progress.Stopped || input.ExpectedHash != state.Progress.StopFingerprint || string.IsNullOrWhiteSpace(input.Reason) ||
                        state.Progress.Continued >= delivery!.Options.MaxContinuations) throw new TaskConflictException("Invalid no-progress continuation.");
                    state.Progress.Stopped = false; state.Progress.Continued++; state.Progress.ExtraAttempts = delivery!.Options.ExtraAttempts; break;
                case TaskCommandKind.VerifyDelivery:
                    if (state.Delivery?.Manifest == null) throw new TaskConflictException("No sealed delivery.");
                    byte[] manifestBytes = await delivery!.Artifacts.ReadAsync(task.Id, state.Delivery.Manifest, ct);
                    var manifest = JsonSerializer.Deserialize<DeliveryManifest>(manifestBytes, TaskJson.Options)!;
                    foreach (var artifact in manifest.Artifacts) await delivery.Artifacts.ReadAsync(task.Id, artifact, ct);
                    return false;
                case TaskCommandKind.FinalizeDelivery: break;
                default: throw new ArgumentException("Unknown acceptance command.");
            }
            task.Acceptance!.Assessment = AcceptanceEvaluator.Evaluate(task);
            var turn = task.Turns.LastOrDefault();
            if (turn?.LastResponse?.Finish == ModelFinish.Stop && turn.LastResponse.Message.ToolCalls.Count == 0) turn.ResumeCompletionCandidate = true;
            return command.Kind != TaskCommandKind.BeginVerification && task.Status != AgentTaskStatus.Paused;
        }
    }
}
