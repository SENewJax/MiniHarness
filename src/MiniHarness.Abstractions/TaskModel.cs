using System;
using System.Collections.Generic;

namespace MiniHarness.Abstractions
{
    public enum AgentTaskStatus { Ready, Running, Waiting, Paused, Completed, Cancelled, Failed }
    public enum TurnStatus { Ready, Running, Waiting, Completed, Interrupted, Failed }
    public enum WaitReason { None, Input, Approval, ExternalResult, Reconciliation, Evidence, Budget, ModelLimit, CapabilityChanged }
    public enum OperationStatus { Proposed, WaitingApproval, Started, Finished, Unknown }
    public enum EffectState { None, Confirmed, Partial, Unknown }
    public enum ApprovalState { None, Approved, Denied }
    public enum StepStatus { Preparing, Ready, Running, Responded, Interrupted }
    public enum TaskCommandKind { Start, Input, Approval, ExternalResult, Pause, Cancel, Resume, Evidence, RefreshWorkspace, Reconcile, QueryResource, CancelResource, ReconcileResources, StartChild, CollectChild }
    public enum ResourceAction { Query, Cancel, Reconcile }

    public sealed class ModelBinding
    {
        public string Model { get; set; } = "";
        public string Protocol { get; set; } = "Unspecified";
        public string ConfigurationVersion { get; set; } = "";
    }
    public sealed class WorkspaceScope
    {
        public string RootPath { get; set; } = "";
        public string Revision { get; set; } = "unmanaged";
        public List<string> Grants { get; set; } = new List<string>();
    }
    public sealed class TaskFacts
    {
        public string Goal { get; set; } = "";
        public List<string> Constraints { get; set; } = new List<string>();
        public WorkspaceScope Workspace { get; set; } = new WorkspaceScope();
        public List<string> RequiredEvidence { get; set; } = new List<string>();
    }
    public sealed class TaskBudget
    {
        public int MaxModelCalls { get; set; } = 12;
        public int MaxToolCalls { get; set; } = 24;
        public long MaxTokens { get; set; } = 50000;
        public int ModelCalls { get; set; }
        public int ToolCalls { get; set; }
        public TokenUsage Usage { get; set; } = new TokenUsage();
        public int ReservedModelCalls { get; set; }
        public int ReservedToolCalls { get; set; }
        public long ReservedTokens { get; set; }
    }
    public sealed class HistoryEntry
    {
        public string Id { get; set; } = "history_" + Guid.NewGuid().ToString("N");
        public string TurnId { get; set; } = "";
        public string? StepId { get; set; }
        public string? OperationId { get; set; }
        public Message Message { get; set; } = new Message();
    }
    public sealed class TaskTurn
    {
        public string Id { get; set; } = "turn_" + Guid.NewGuid().ToString("N");
        public string ResponseId { get; set; } = "resp_" + Guid.NewGuid().ToString("N");
        public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public TurnStatus Status { get; set; }
        public WaitReason Wait { get; set; }
        public string? StopReason { get; set; }
        // Request configuration only; canonical history belongs to AgentTaskState.History.
        public ModelRequest Settings { get; set; } = new ModelRequest();
        public int ModelCalls { get; set; }
        public int ToolCalls { get; set; }
        public TokenUsage Usage { get; set; } = new TokenUsage();
        public ModelResponse? LastResponse { get; set; }
    }
    public sealed class CapabilitySnapshot
    {
        public string Version { get; set; } = "";
        public string PolicyVersion { get; set; } = "";
        public List<ToolDefinition> Tools { get; set; } = new List<ToolDefinition>();
        public WorkspaceScope Workspace { get; set; } = new WorkspaceScope();
    }
    public sealed class TaskStep
    {
        public string Id { get; set; } = "step_" + Guid.NewGuid().ToString("N");
        public string TurnId { get; set; } = "";
        public StepStatus Status { get; set; }
        public ModelBinding Model { get; set; } = new ModelBinding();
        public CapabilitySnapshot Capabilities { get; set; } = new CapabilitySnapshot();
        // Frozen finite model view, separate from task facts and execution history.
        public ModelRequest? PreparedRequest { get; set; }
    }
    public sealed class ToolOperation
    {
        public string Id { get; set; } = "op_" + Guid.NewGuid().ToString("N");
        public string TurnId { get; set; } = "";
        public string StepId { get; set; } = "";
        public ToolCall Call { get; set; } = new ToolCall();
        public bool ClientOwned { get; set; }
        public OperationStatus Status { get; set; }
        public EffectState Effect { get; set; }
        public ApprovalState Approval { get; set; }
        public string? ApprovalFingerprint { get; set; }
        public ToolOutcome? Outcome { get; set; }
    }
    public sealed class ResourceReference
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string OwnerTaskId { get; set; } = "";
        public string OwnerOperationId { get; set; } = "";
        public string State { get; set; } = "unknown";
        public string RecoveryHint { get; set; } = "";
        public string Result { get; set; } = "";
    }
    public sealed class ChildTaskLink
    {
        public string TaskId { get; set; } = "";
        public string StartCommandId { get; set; } = "";
        public ModelRequest Request { get; set; } = new ModelRequest();
        public TaskFacts Facts { get; set; } = new TaskFacts();
        public TaskBudget Allocation { get; set; } = new TaskBudget();
        public bool Collected { get; set; }
        public TaskBudget? Usage { get; set; }
        public string Result { get; set; } = "";
    }
    public sealed class ArtifactReference
    {
        public string Id { get; set; } = "";
        public string OwnerTaskId { get; set; } = "";
        public string Location { get; set; } = "";
        public string ContentHash { get; set; } = "";
    }
    public sealed class EvidenceRecord
    {
        public string Id { get; set; } = "evidence_" + Guid.NewGuid().ToString("N");
        public string Requirement { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string WorkspaceRevision { get; set; } = "";
        public string OperationId { get; set; } = "";
        public bool Passed { get; set; }
        public int? ExitCode { get; set; }
        public string Output { get; set; } = "";
        public List<ArtifactReference> Artifacts { get; set; } = new List<ArtifactReference>();
    }
    public sealed class ToolOutcome
    {
        public ToolCallStatus Status { get; set; }
        public string Output { get; set; } = "";
        public EffectState Effect { get; set; }
        public List<ResourceReference> Resources { get; set; } = new List<ResourceReference>();
        public List<EvidenceRecord> Evidence { get; set; } = new List<EvidenceRecord>();
        public List<ArtifactReference> Artifacts { get; set; } = new List<ArtifactReference>();
    }
    public sealed class TaskEvent
    {
        public long Sequence { get; set; }
        public long Revision { get; set; }
        public string TaskId { get; set; } = "";
        public string? TurnId { get; set; }
        public string? OperationId { get; set; }
        public string Type { get; set; } = "";
        public string Detail { get; set; } = "";
    }
    public sealed class CommandReceipt
    {
        public string CommandId { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public long AcceptedRevision { get; set; }
        public string? TurnId { get; set; }
    }
    public sealed class AgentTaskState
    {
        public const int CurrentSchemaVersion = 1;
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public long Revision { get; set; }
        public string Id { get; set; } = "task_" + Guid.NewGuid().ToString("N");
        public string? ParentTaskId { get; set; }
        public string? ForkedFromResponseId { get; set; }
        public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Ready;
        public WaitReason Wait { get; set; }
        public TaskFacts Facts { get; set; } = new TaskFacts();
        public ModelBinding Model { get; set; } = new ModelBinding();
        public TaskBudget Budget { get; set; } = new TaskBudget();
        public List<HistoryEntry> History { get; set; } = new List<HistoryEntry>();
        public List<TaskTurn> Turns { get; set; } = new List<TaskTurn>();
        public List<TaskStep> Steps { get; set; } = new List<TaskStep>();
        public List<ToolOperation> Operations { get; set; } = new List<ToolOperation>();
        public List<ResourceReference> Resources { get; set; } = new List<ResourceReference>();
        public List<EvidenceRecord> Evidence { get; set; } = new List<EvidenceRecord>();
        public List<CommandReceipt> Receipts { get; set; } = new List<CommandReceipt>();
        public List<ArtifactReference> Artifacts { get; set; } = new List<ArtifactReference>();
        public List<TaskEvent> Events { get; set; } = new List<TaskEvent>();
        public List<ChildTaskLink> Children { get; set; } = new List<ChildTaskLink>();
    }
    public sealed class TaskResponseReference
    {
        public string TaskId { get; set; } = "";
        public string TurnId { get; set; } = "";
    }
    public sealed class ExternalToolResult
    {
        public string CallId { get; set; } = "";
        public string Output { get; set; } = "";
    }
    public sealed class TaskCommand
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string? TaskId { get; set; }
        public TaskCommandKind Kind { get; set; }
        public ModelRequest? Request { get; set; }
        public string? ForkedFromResponseId { get; set; }
        public TaskFacts? Facts { get; set; }
        public string? OperationId { get; set; }
        public bool Approve { get; set; }
        public List<ExternalToolResult> Results { get; set; } = new List<ExternalToolResult>();
        public List<EvidenceRecord> Evidence { get; set; } = new List<EvidenceRecord>();
        public ToolOutcome? ReconciledOutcome { get; set; }
        public string? ResourceId { get; set; }
        public string? ChildTaskId { get; set; }
        public string? ParentTaskId { get; set; }
        public TaskBudget? ChildBudget { get; set; }
    }
    public sealed class TaskCommandResult
    {
        public AgentTaskState State { get; set; } = new AgentTaskState();
        public CommandReceipt Receipt { get; set; } = new CommandReceipt();
    }
    public sealed class TaskSubmission
    {
        public string TaskId { get; set; } = "";
        public CommandReceipt Receipt { get; set; } = new CommandReceipt();
    }
    public sealed class AgentInput
    {
        public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
        public ModelRequest Request { get; set; } = new ModelRequest();
        public string? PreviousResponseId { get; set; }
        public bool ToolsSpecified { get; set; }
        public bool CancelTaskWhenWaitCancelled { get; set; }
    }
}
