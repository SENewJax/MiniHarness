using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness.Acceptance
{
    public static class AcceptanceBinding
    {
        public static string Hash<T>(T value) => TaskJson.Hash(JsonSerializer.Serialize(value, TaskJson.Options));
        public static string SpecHash(AcceptanceSpec spec) => Hash(new { spec.Id, spec.Revision, spec.PolicyVersion,
            Criteria = spec.Criteria.OrderBy(c => c.Id, StringComparer.Ordinal),
            ProtectedPaths = spec.ProtectedPaths.OrderBy(p => p, StringComparer.Ordinal),
            AllowNewFilesUnder = spec.AllowNewFilesUnder.OrderBy(p => p, StringComparer.Ordinal) });
        public static void Validate(AcceptanceSpec spec)
        {
            if (spec.Revision < 1 || spec.Criteria == null || spec.ProtectedPaths == null || spec.AllowNewFilesUnder == null ||
                spec.ProtectedPaths.Count > 128 || spec.AllowNewFilesUnder.Count > 128 || spec.Criteria.Count == 0 || !spec.Criteria.Any(c => c.Required) || spec.Criteria.Count > 64 ||
                spec.Criteria.Any(c => string.IsNullOrWhiteSpace(c.Id) || !Enum.IsDefined(c.Kind) ||
                    c.Kind == CriterionKind.VerificationProfile && (c.Profile.Length == 0 || c.VerifierFingerprint.Length == 0) ||
                    c.Kind == CriterionKind.ArtifactPresence && c.ArtifactName.Length == 0) ||
                spec.Criteria.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != spec.Criteria.Count)
                throw new ArgumentException("Invalid acceptance contract.");
            foreach (var path in spec.ProtectedPaths.Concat(spec.AllowNewFilesUnder)) Delivery.SafeFiles.Relative(path);
            if (spec.ContentHash.Length > 0 && spec.ContentHash != SpecHash(spec)) throw new ArgumentException("Acceptance hash mismatch.");
            spec.ContentHash = SpecHash(spec);
        }
        public static string AttemptBinding(AgentTaskState task, AcceptanceCriterion criterion) => Hash(new
        { task.Id, Criterion = criterion.Id, Spec = task.Facts.AcceptanceSpec!.ContentHash, task.Facts.Workspace.Revision,
            criterion.VerifierFingerprint, Subject = Subject(task, criterion) });
        public static string Subject(AgentTaskState task, AcceptanceCriterion criterion) => criterion.SubjectArtifact.Length == 0 ? "" :
            task.Acceptance!.FrozenArtifacts.Where(a => (a.LogicalName ?? a.Id) == criterion.SubjectArtifact)
                .OrderBy(a => a.Generation ?? 0).LastOrDefault()?.ContentHash ?? "missing";
    }
    public static class EvidenceAdmission
    {
        public static VerificationAttempt Begin(AgentTaskState task, string criterionId, string attemptId, string operationId)
        {
            var state = task.Acceptance!; var criterion = task.Facts.AcceptanceSpec!.Criteria.Single(c => c.Id == criterionId && c.Kind == CriterionKind.VerificationProfile);
            var binding = AcceptanceBinding.AttemptBinding(task, criterion);
            var existing = state.Attempts.SingleOrDefault(a => a.Id == attemptId);
            if (existing != null)
            {
                if (existing.Binding != binding || existing.OperationId != operationId) throw new TaskConflictException("Attempt identity conflict.");
                return existing;
            }
            if (state.Attempts.Any(a => a.Binding == binding && (a.Status == VerificationAttemptStatus.Pending || a.Status == VerificationAttemptStatus.Unknown)))
                throw new TaskConflictException("An attempt is still pending or unknown; reconcile it before retrying.");
            var attempt = new VerificationAttempt { Id = attemptId, Binding = binding,
                Generation = state.Attempts.Count == 0 ? 1 : state.Attempts.Max(a => a.Generation) + 1,
                CriterionId = criterion.Id, SpecHash = task.Facts.AcceptanceSpec.ContentHash, WorkspaceRevision = task.Facts.Workspace.Revision,
                SubjectHash = AcceptanceBinding.Subject(task, criterion), VerifierFingerprint = criterion.VerifierFingerprint, OperationId = operationId };
            state.Attempts.Add(attempt); return attempt;
        }
        public static void Settle(AgentTaskState task, EvidenceEnvelope envelope, bool reconcile)
        {
            var state = task.Acceptance!; var attempt = state.Attempts.SingleOrDefault(a => a.Id == envelope.AttemptId)
                ?? throw new TaskConflictException("Unknown verification attempt.");
            var evidence = envelope.Evidence;
            if (evidence.TaskId != task.Id || evidence.OperationId != attempt.OperationId || evidence.WorkspaceRevision != attempt.WorkspaceRevision ||
                evidence.VerifierFingerprint != attempt.VerifierFingerprint || envelope.Source.Length == 0 ||
                envelope.Status == VerificationAttemptStatus.Pending || !Enum.IsDefined(envelope.Status) ||
                envelope.Status == VerificationAttemptStatus.Passed && (!evidence.Passed || evidence.ExitCode != 0))
                throw new TaskConflictException("Evidence does not match the frozen verification attempt.");
            string hash = AcceptanceBinding.Hash(new { envelope.AttemptId, envelope.Source, envelope.Status, envelope.Evidence });
            if (attempt.Result != null)
            {
                if (attempt.Result.ContentHash == hash) return;
                if (!(reconcile && attempt.Status == VerificationAttemptStatus.Unknown)) throw new TaskConflictException("Attempt already has a different settlement.");
            }
            if (attempt.Status == VerificationAttemptStatus.Unknown && !reconcile) throw new TaskConflictException("Unknown attempts require explicit reconciliation.");
            if (state.Attempts.Any(a => a.Id != attempt.Id && a.Result?.Evidence.Id == evidence.Id)) throw new TaskConflictException("Evidence identity reused.");
            envelope.ContentHash = hash; envelope.AcceptedSequence = ++state.EvidenceSequence;
            attempt.Result = TaskJson.Clone(envelope); attempt.Status = envelope.Status;
        }
    }
    public static class AcceptanceEvaluator
    {
        private static bool Approved(AgentTaskState task)
        {
            var state = task.Acceptance!; var candidate = state.Candidate;
            return candidate != null && candidate.SpecHash == task.Facts.AcceptanceSpec!.ContentHash && candidate.WorkspaceRevision == task.Facts.Workspace.Revision &&
                candidate.SteeringRevision == task.SteeringRevision &&
                candidate.Fingerprint == Delivery.DeliveryManifestBuilder.CandidateHash(task, candidate.ChangeSetHash, candidate.Explanation) &&
                state.Reviews.Any(r => r.Accept && r.CandidateFingerprint == candidate.Fingerprint);
        }
        public static AcceptanceAssessment Evaluate(AgentTaskState task)
        {
            var result = new AcceptanceAssessment(); var spec = task.Facts.AcceptanceSpec!; var state = task.Acceptance!;
            foreach (var criterion in spec.Criteria.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                var item = new CriterionAssessment { CriterionId = criterion.Id, Status = AssessmentStatus.Missing, Reason = "missing" };
                if (criterion.Kind == CriterionKind.HumanReview)
                { bool approved = Approved(task); item.Status = approved ? AssessmentStatus.Passed : AssessmentStatus.NeedsReview; item.Reason = approved ? "review_accepted" : "current_candidate_review_required"; }
                else if (criterion.Kind == CriterionKind.ArtifactPresence)
                { if (state.FrozenArtifacts.Any(a => (a.LogicalName ?? a.Id) == criterion.ArtifactName)) { item.Status = AssessmentStatus.Passed; item.Reason = "frozen_artifact"; } }
                else
                {
                    string binding = AcceptanceBinding.AttemptBinding(task, criterion);
                    var attempt = state.Attempts.Where(a => a.Binding == binding).OrderByDescending(a => a.Generation).FirstOrDefault();
                    if (attempt == null && state.Attempts.Any(a => a.CriterionId == criterion.Id)) { item.Status = AssessmentStatus.Stale; item.Reason = "verification_binding_changed"; }
                    if (attempt != null)
                    {
                        item.Status = attempt.Status == VerificationAttemptStatus.Pending ? AssessmentStatus.Pending :
                            attempt.Status == VerificationAttemptStatus.Unknown ? AssessmentStatus.Unknown :
                            attempt.Status == VerificationAttemptStatus.Passed ? AssessmentStatus.Passed : AssessmentStatus.Failed;
                        item.Reason = attempt.Status.ToString(); item.EvidenceId = attempt.Result?.Evidence.Id;
                    }
                }
                result.Criteria.Add(item);
            }
            if (!task.Budget.Usage.Known) result.Blockers.Add("unknown_usage");
            if (task.Operations.Any(o => o.Status == OperationStatus.Unknown || o.Status == OperationStatus.Started)) result.Blockers.Add("unknown_operation");
            if (task.Operations.Any(o => o.Status == OperationStatus.WaitingApproval)) result.Blockers.Add("tool_approval");
            if (state.ProtectionViolations.Count > 0) result.Blockers.Add("protected_files_changed");
            if (state.ChangeConflicts.Count > 0) result.Blockers.Add("change_set_conflict");
            if (state.Attempts.Any(a => a.Status == VerificationAttemptStatus.Unknown || a.Status == VerificationAttemptStatus.Pending)) result.Blockers.Add("unsettled_verification");
            var required = spec.Criteria.Where(c => c.Required).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            result.AutomaticChecksPassed = result.Criteria.Where(c => required.Contains(c.CriterionId)).All(c => c.Status == AssessmentStatus.Passed || c.Status == AssessmentStatus.NeedsReview);
            result.CanBuildCandidate = result.AutomaticChecksPassed && result.Blockers.Count == 0;
            result.NeedsReview = spec.Criteria.Any(c => c.Required && c.Kind == CriterionKind.HumanReview) &&
                !Approved(task);
            result.CanFinalize = result.CanBuildCandidate && !result.NeedsReview;
            if (task.Resources.Any(r => !ResourceStates.IsTerminal(r.State) || r.Process != null && !r.Process.TerminationConfirmed))
            { result.Blockers.Add("resources"); result.CanFinalize = false; }
            if (task.Children.Any(c => !c.Collected)) { result.Blockers.Add("children"); result.CanFinalize = false; }
            if (result.NeedsReview) result.Blockers.Add("review");
            if (state.Progress.Stopped && !result.CanFinalize) result.Blockers.Add("no_progress");
            return result;
        }
    }
    public static class ProgressPolicy
    {
        public static void Sample(AgentTaskState task, TaskStep step, DeliveryOptions options)
        {
            var state = task.Acceptance!; var progress = state.Progress;
            if (step.Status != StepStatus.Responded || progress.EvaluatedSteps.Contains(step.Id) ||
                task.Operations.Any(o => o.StepId == step.Id && o.Status != OperationStatus.Finished) ||
                task.ResourceWaits.Any(w => w.TurnId == step.TurnId && w.Status == "active")) return;
            progress.EvaluatedSteps.Add(step.Id); progress.LastEvaluatedAttemptId = step.Id;
            if (!progress.Tracking)
            {
                progress.Tracking = state.Attempts.Any(a => a.Status == VerificationAttemptStatus.Failed) ||
                    task.ModelInvocations.Any(i => i.StepId == step.Id && i.ResultApplied && i.Response?.Finish == ModelFinish.Stop && i.Response.Message.ToolCalls.Count == 0);
                if (!progress.Tracking) return;
            }
            var assessment = AcceptanceEvaluator.Evaluate(task);
            string signature = AcceptanceBinding.Hash(new { Spec = task.Facts.AcceptanceSpec!.ContentHash,
                Criteria = assessment.Criteria.Select(c => new { c.CriterionId, c.Status, c.Reason }) });
            var required = task.Facts.AcceptanceSpec!.Criteria.Where(c => c.Required).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            int passed = assessment.Criteria.Count(c => c.Status == AssessmentStatus.Passed && required.Contains(c.CriterionId));
            bool improved = passed > progress.BestPassed;
            if (improved) { progress.BestPassed = passed; progress.Unchanged = 0; }
            else progress.Unchanged++;
            bool repeated = progress.Window.Count(s => s == signature) >= options.NoProgressLimit - 1;
            progress.Window.Add(signature); if (progress.Window.Count > 32) progress.Window.RemoveAt(0);
            bool continuationExhausted = false;
            if (progress.ExtraAttempts > 0)
            { progress.ExtraAttempts--; if (progress.ExtraAttempts > 0) return; continuationExhausted = true; }
            if (!assessment.CanFinalize && (continuationExhausted || !assessment.CanBuildCandidate && (progress.Unchanged >= options.NoProgressLimit || repeated)))
            { progress.Stopped = true; progress.StopFingerprint = AcceptanceBinding.Hash(new { task.Id, signature, Step = step.Id, progress.Continued }); }
        }
    }
}
