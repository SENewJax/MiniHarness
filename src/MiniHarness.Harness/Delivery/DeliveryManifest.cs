using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness.Acceptance;

namespace MiniHarness.Harness.Delivery
{
    public sealed class DeliveryServices
    {
        public DeliveryOptions Options { get; set; } = new DeliveryOptions();
        public IArtifactStore Artifacts { get; set; } = null!;
        public IChangeSetProvider? Changes { get; set; }
        public IAcceptanceAuthorizer? Authorizer { get; set; }
    }
    public sealed class DeliveryManifest
    {
        public int Version { get; set; } = 1;
        public string TaskId { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; }
        public TaskFacts Facts { get; set; } = new TaskFacts();
        public CompletionCandidate Candidate { get; set; } = new CompletionCandidate();
        public AcceptanceAssessment Assessment { get; set; } = new AcceptanceAssessment();
        public ProtectionBaseline Protection { get; set; } = new ProtectionBaseline();
        public List<VerificationAttempt> Attempts { get; set; } = new List<VerificationAttempt>();
        public List<ReviewDecision> Reviews { get; set; } = new List<ReviewDecision>();
        public List<ArtifactReference> Artifacts { get; set; } = new List<ArtifactReference>();
        public TaskBudget Cost { get; set; } = new TaskBudget();
        public ChangeSetSnapshot Changes { get; set; } = new ChangeSetSnapshot();
        public List<ResourceReference> Resources { get; set; } = new List<ResourceReference>();
        public string Limitations { get; set; } = "Integrity and recorded acceptance only; shared trusted host, no sandbox or test coverage guarantee.";
    }
    public static class DeliveryManifestBuilder
    {
        public static string CandidateHash(AgentTaskState task, string changesHash, string explanation) => AcceptanceBinding.Hash(new
        {
            Spec = task.Facts.AcceptanceSpec!.ContentHash, task.Facts.Workspace.Revision, task.SteeringRevision,
            Evidence = task.Acceptance!.Attempts.OrderBy(a => a.Generation),
            Artifacts = task.Acceptance.FrozenArtifacts.OrderBy(a => a.Id, StringComparer.Ordinal),
            Protection = task.Acceptance.Protection.ContentHash, Changes = changesHash, Explanation = explanation
        });
        public static byte[] Render(DeliveryManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, TaskJson.Options);
        public static byte[] Explain(DeliveryManifest manifest) => Encoding.UTF8.GetBytes("# Delivery\n\nTask: " + manifest.TaskId +
            "\n\nCandidate: " + manifest.Candidate.Fingerprint + "\n\n" + string.Join("\n", manifest.Assessment.Criteria.Select(c => "- " + c.CriterionId + ": " + c.Status)) +
            "\n\n## Model explanation (not verification)\n\n" + manifest.Candidate.Explanation + "\n\n" + manifest.Limitations + "\n");
        public static async Task Verify(string directory, string expectedManifestHash, CancellationToken ct)
        {
            byte[] bytes = await SafeFiles.Read(SafeFiles.Within(directory, "manifest.json"), 8 * 1024 * 1024, ct);
            if (SafeFiles.Hash(bytes) != expectedManifestHash) throw new InvalidDataException("Trusted manifest hash mismatch.");
            var manifest = JsonSerializer.Deserialize<DeliveryManifest>(bytes, TaskJson.Options) ?? throw new InvalidDataException("Missing manifest.");
            if (manifest.Version != 1 || manifest.Facts.AcceptanceSpec == null) throw new InvalidDataException("Unsupported delivery contract.");
            AcceptanceBinding.Validate(manifest.Facts.AcceptanceSpec);
            foreach (var artifact in manifest.Artifacts)
            {
                if (artifact.OwnerTaskId != manifest.TaskId || artifact.BlobHash != artifact.ContentHash || artifact.Location != "blobs/" + artifact.ContentHash)
                    throw new InvalidDataException("Invalid artifact binding.");
                byte[] content = await SafeFiles.Read(SafeFiles.Within(directory, artifact.Location), 8 * 1024 * 1024, ct);
                if (SafeFiles.Hash(content) != artifact.ContentHash || content.LongLength != artifact.Length) throw new InvalidDataException("Artifact hash mismatch.");
            }
            var task = new AgentTaskState { Id = manifest.TaskId, Facts = manifest.Facts, Budget = manifest.Cost, SteeringRevision = manifest.Candidate.SteeringRevision,
                Acceptance = new AcceptanceState { Attempts = manifest.Attempts, Candidate = manifest.Candidate, Reviews = manifest.Reviews,
                    Protection = manifest.Protection, FrozenArtifacts = manifest.Artifacts.Where(a => a.Id != "changes.json" && a.Id != "delivery.md").ToList() } };
            foreach (var attempt in manifest.Attempts)
            {
                if (attempt.Result == null && attempt.Status == VerificationAttemptStatus.Cancelled) continue;
                if (attempt.Result == null || attempt.Status != attempt.Result.Status) throw new InvalidDataException("Unsettled attempt.");
                string hash = AcceptanceBinding.Hash(new { attempt.Result.AttemptId, attempt.Result.Source, attempt.Result.Status, attempt.Result.Evidence });
                if (hash != attempt.Result.ContentHash || attempt.Result.AttemptId != attempt.Id || attempt.Result.Evidence.TaskId != task.Id) throw new InvalidDataException("Evidence integrity failure.");
            }
            if (!AcceptanceEvaluator.Evaluate(task).CanFinalize || manifest.Changes.Conflicts.Count > 0 ||
                DeliveryManifestBuilder.CandidateHash(task, AcceptanceBinding.Hash(manifest.Changes), manifest.Candidate.Explanation) != manifest.Candidate.Fingerprint)
                throw new InvalidDataException("Acceptance/candidate mismatch.");
            foreach (var change in manifest.Changes.Entries.Concat(manifest.Changes.ExternalEntries))
                if (change.After == null || SafeFiles.Hash(change.After) != change.AfterHash || change.Before != null && SafeFiles.Hash(change.Before) != change.BeforeHash)
                    throw new InvalidDataException("Change set integrity failure.");
        }
    }
}
