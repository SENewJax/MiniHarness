using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHarness.Abstractions
{
    public enum CriterionKind { VerificationProfile, ArtifactPresence, HumanReview }
    public enum AssessmentStatus { Missing, Passed, Failed, Stale, Invalid, Unknown, NeedsReview, Pending }
    public enum VerificationAttemptStatus { Pending, Passed, Failed, Cancelled, Unknown }
    public enum DeliveryStatus { Preparing, Sealed, Committed, Invalidated }
    public sealed class AcceptanceCriterion
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public CriterionKind Kind { get; set; }
        public bool Required { get; set; } = true;
        public string Profile { get; set; } = "";
        public string VerifierFingerprint { get; set; } = "";
        public string ArtifactName { get; set; } = "";
        public string SubjectArtifact { get; set; } = "";
    }
    public sealed class AcceptanceSpec
    {
        public string Id { get; set; } = "acceptance";
        public int Revision { get; set; } = 1;
        public string ContentHash { get; set; } = "";
        public string PolicyVersion { get; set; } = "v4-1";
        public List<AcceptanceCriterion> Criteria { get; set; } = new List<AcceptanceCriterion>();
        public List<string> ProtectedPaths { get; set; } = new List<string>();
        public List<string> AllowNewFilesUnder { get; set; } = new List<string>();
    }
    public sealed class ProtectionBaseline
    {
        public Dictionary<string, string> Files { get; set; } = new Dictionary<string, string>();
        public string ContentHash { get; set; } = "";
    }
    public sealed class VerificationAttempt
    {
        public string Id { get; set; } = "";
        public string Binding { get; set; } = "";
        public long Generation { get; set; }
        public string CriterionId { get; set; } = "";
        public string SpecHash { get; set; } = "";
        public string WorkspaceRevision { get; set; } = "";
        public string SubjectHash { get; set; } = "";
        public string VerifierFingerprint { get; set; } = "";
        public string OperationId { get; set; } = "";
        public VerificationAttemptStatus Status { get; set; }
        public EvidenceEnvelope? Result { get; set; }
    }
    public sealed class EvidenceEnvelope
    {
        public string AttemptId { get; set; } = "";
        public string Source { get; set; } = "";
        public long AcceptedSequence { get; set; }
        public string ContentHash { get; set; } = "";
        public VerificationAttemptStatus Status { get; set; }
        public EvidenceRecord Evidence { get; set; } = new EvidenceRecord();
    }
    public sealed class CriterionAssessment
    {
        public string CriterionId { get; set; } = "";
        public AssessmentStatus Status { get; set; }
        public string Reason { get; set; } = "";
        public string? EvidenceId { get; set; }
    }
    public sealed class AcceptanceAssessment
    {
        public List<CriterionAssessment> Criteria { get; set; } = new List<CriterionAssessment>();
        public List<string> Blockers { get; set; } = new List<string>();
        public bool AutomaticChecksPassed { get; set; }
        public bool CanBuildCandidate { get; set; }
        public bool NeedsReview { get; set; }
        public bool CanFinalize { get; set; }
    }
    public sealed class ProgressRecord
    {
        public bool Tracking { get; set; }
        public List<string> EvaluatedSteps { get; set; } = new List<string>();
        public string LastEvaluatedAttemptId { get; set; } = "";
        public List<string> Window { get; set; } = new List<string>();
        public int Unchanged { get; set; }
        public int BestPassed { get; set; }
        public int ExtraAttempts { get; set; }
        public int Continued { get; set; }
        public bool Stopped { get; set; }
        public string StopFingerprint { get; set; } = "";
    }
    public sealed class CompletionCandidate
    {
        public string Fingerprint { get; set; } = "";
        public string SpecHash { get; set; } = "";
        public string WorkspaceRevision { get; set; } = "";
        public int SteeringRevision { get; set; }
        public string Explanation { get; set; } = "";
        public string ChangeSetHash { get; set; } = "";
    }
    public sealed class ReviewDecision
    {
        public string CandidateFingerprint { get; set; } = "";
        public bool Accept { get; set; }
        public string Actor { get; set; } = "";
        public string Comment { get; set; } = "";
    }
    public sealed class DeliveryRecord
    {
        public DeliveryStatus Status { get; set; }
        public string CandidateFingerprint { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; }
        public TaskBudget Cost { get; set; } = new TaskBudget();
        public ArtifactReference? Manifest { get; set; }
        public string? Error { get; set; }
    }
    public sealed class ChangeSetEntry
    {
        public string Path { get; set; } = "";
        public string BeforeHash { get; set; } = "";
        public string AfterHash { get; set; } = "";
        public byte[]? Before { get; set; }
        public byte[]? After { get; set; }
        public string OperationId { get; set; } = "";
    }
    public sealed class ChangeSetSnapshot
    {
        public List<ChangeSetEntry> Entries { get; set; } = new List<ChangeSetEntry>();
        public List<ChangeSetEntry> ExternalEntries { get; set; } = new List<ChangeSetEntry>();
        public List<string> Conflicts { get; set; } = new List<string>();
    }
    public sealed class AcceptanceState
    {
        public Dictionary<string, string> WorkspaceBaseline { get; set; } = new Dictionary<string, string>();
        public ProtectionBaseline Protection { get; set; } = new ProtectionBaseline();
        public List<VerificationAttempt> Attempts { get; set; } = new List<VerificationAttempt>();
        public List<string> Diagnostics { get; set; } = new List<string>();
        public long EvidenceSequence { get; set; }
        public AcceptanceAssessment Assessment { get; set; } = new AcceptanceAssessment();
        public ProgressRecord Progress { get; set; } = new ProgressRecord();
        public CompletionCandidate? Candidate { get; set; }
        public List<ReviewDecision> Reviews { get; set; } = new List<ReviewDecision>();
        public DeliveryRecord? Delivery { get; set; }
        public List<ArtifactReference> FrozenArtifacts { get; set; } = new List<ArtifactReference>();
        public List<string> ProtectionViolations { get; set; } = new List<string>();
        public List<string> ChangeConflicts { get; set; } = new List<string>();
    }
    public sealed class AcceptanceCommand
    {
        public string ExpectedHash { get; set; } = "";
        public string Reason { get; set; } = "";
        public AcceptanceSpec? Spec { get; set; }
        public ReviewDecision? Review { get; set; }
        public EvidenceEnvelope? Evidence { get; set; }
        public string CriterionId { get; set; } = "";
        public string AttemptId { get; set; } = "";
        public ArtifactReference? Artifact { get; set; }
    }
    public sealed class DeliveryOptions
    {
        public void Validate()
        {
            if (NoProgressLimit < 1 || MaxContinuations < 0 || ExtraAttempts < 1 || MaxArtifactBytes < 1 || MaxTaskBytes < 1 || MaxArtifacts < 3 || IoTimeoutSeconds < 1)
                throw new ArgumentException("Invalid delivery limits.");
        }
        public int NoProgressLimit { get; set; } = 3;
        public int MaxContinuations { get; set; } = 2;
        public int ExtraAttempts { get; set; } = 2;
        public long MaxArtifactBytes { get; set; } = 8 * 1024 * 1024;
        public long MaxTaskBytes { get; set; } = 64 * 1024 * 1024;
        public int MaxArtifacts { get; set; } = 128;
        public int IoTimeoutSeconds { get; set; } = 30;
    }
    public interface IVerificationProducer
    {
        string? Profile(ToolCall call);
        string Fingerprint(string profile);
    }
    public interface IWorkspaceMutation
    {
        IEnumerable<string> TargetPaths(JsonElement arguments);
    }
    public interface IChangeSetProvider
    {
        Task<Dictionary<string, string>> CaptureBaselineAsync(WorkspaceScope workspace, CancellationToken ct);
        Task<ChangeSetSnapshot> FreezeAsync(AgentTaskState task, CancellationToken ct);
    }
    public interface IAcceptanceAuthorizer
    {
        string PolicyVersion { get; }
        bool Allows(AgentTaskState task, TaskCommandKind action);
        string Actor { get; }
        bool ValidSpec(AgentTaskState task, AcceptanceSpec spec);
    }
    public interface IArtifactStore
    {
        Task<ArtifactReference> ImportAsync(string taskId, ArtifactReference source, CancellationToken ct);
        Task<ArtifactReference> WriteAsync(string taskId, string name, byte[] bytes, CancellationToken ct);
        Task<byte[]> ReadAsync(string taskId, ArtifactReference artifact, CancellationToken ct);
    }
}
