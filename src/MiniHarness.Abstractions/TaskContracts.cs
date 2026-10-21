using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHarness.Abstractions
{
    public interface ITaskStore
    {
        Task<AgentTaskState?> LoadAsync(string id, CancellationToken cancellationToken);
        Task<ITaskLease> AcquireAsync(string id, CancellationToken cancellationToken);
        Task<TaskResponseReference?> ResolveResponseAsync(string responseId, CancellationToken cancellationToken);
    }
    // A lease owns the task across read/advance/commit, including asynchronous work.
    public interface ITaskLease : IAsyncDisposable
    {
        Task<AgentTaskState?> LoadAsync(CancellationToken cancellationToken);
        Task CommitAsync(AgentTaskState state, long expectedRevision, CancellationToken cancellationToken);
    }
    public interface ITaskRuntime
    {
        Task<TaskCommandResult> ExecuteAsync(TaskCommand command, CancellationToken waitCancellation);
        Task<TaskSubmission> SubmitAsync(TaskCommand command, CancellationToken waitCancellation);
        Task<AgentTaskState?> GetAsync(string taskId, CancellationToken cancellationToken);
        Task<IReadOnlyList<TaskEvent>> ObserveAsync(string taskId, long afterSequence, CancellationToken cancellationToken);
    }
    public interface ITaskInitializer
    {
        void Initialize(TaskCommand command);
    }
    public sealed class TaskExecutionOptions
    {
        public bool CancelTaskWhenWaitCancelled { get; set; }
    }
    public interface ITaskApplicationService : ITaskRuntime
    {
        Task<TaskCommandResult> ExecuteAsync(TaskCommand command, TaskExecutionOptions options, CancellationToken waitCancellation);
        Task<TaskSubmission> SubmitAsync(TaskCommand command, TaskExecutionOptions options, CancellationToken waitCancellation);
    }
    public interface IAgentService
    {
        Task<RunState> RespondAsync(AgentInput input, CancellationToken waitCancellation);
        Task<RunState?> ReadResponseAsync(string responseId, CancellationToken cancellationToken);
    }
    public sealed class ToolExecutionContext
    {
        public string TaskId { get; set; } = "";
        public string TurnId { get; set; } = "";
        public string StepId { get; set; } = "";
        public string OperationId { get; set; } = "";
        public WorkspaceScope Workspace { get; set; } = new WorkspaceScope();
        public CapabilitySnapshot Capabilities { get; set; } = new CapabilitySnapshot();
        public ApprovalState Approval { get; set; }
    }
    public interface IContextualTool : ITool
    {
        Task<ToolOutcome> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken);
    }
    public interface IVersionedTool
    {
        string Version { get; }
    }
    public interface IVersionedToolPolicy
    {
        string Version { get; }
    }
    public interface IContextualToolPolicy
    {
        string Version { get; }
        Task<ToolAdmission> CheckAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken);
    }
    public sealed class ContextPreparation
    {
        public TaskFacts Facts { get; set; } = new TaskFacts();
        public TaskStep Step { get; set; } = new TaskStep();
        public List<HistoryEntry> History { get; set; } = new List<HistoryEntry>();
        public ContextMemory Memory { get; set; } = new ContextMemory();
        public SummaryContent? Summary { get; set; }
        public string RuntimeState { get; set; } = "";
    }
    public sealed class PreparedContext
    {
        public List<Message> Messages { get; set; } = new List<Message>();
        public string SourceHash { get; set; } = "";
    }
    public interface IContextPreparation
    {
        Task<PreparedContext> PrepareAsync(ContextPreparation input, CancellationToken cancellationToken);
    }
    public sealed class TaskCompletionVerdict
    {
        public AcceptanceAssessment? Assessment { get; set; }
        public bool CompleteTask { get; set; }
        public bool CompleteTurn { get; set; }
        public bool CanContinue { get; set; }
        public WaitReason WaitingReason { get; set; } = WaitReason.Input;
        public string Feedback { get; set; } = "";
    }
    public interface ITaskCompletionPolicy
    {
        Task<TaskCompletionVerdict> EvaluateAsync(AgentTaskState state, ModelResponse candidate, CancellationToken cancellationToken);
    }
    public interface IWorkspaceRevisionProvider
    {
        Task<string> GetRevisionAsync(WorkspaceScope workspace, CancellationToken cancellationToken);
    }
    // Managers own the external handle. Returned references must preserve identity
    // and ownership. Cancel and Reconcile must be safe to repeat after a crash.
    public interface IResourceManager
    {
        string Kind { get; }
        Task<ResourceReference> ExecuteAsync(ResourceReference resource, ResourceAction action, CancellationToken cancellationToken);
    }
    public sealed class TaskConflictException : Exception
    {
        public TaskConflictException(string message) : base(message) { }
    }
    public sealed class CheckpointWriteException : Exception
    {
        public CheckpointWriteException(Exception inner) : base("Task checkpoint could not be committed; execution stopped.", inner) { }
    }
}
