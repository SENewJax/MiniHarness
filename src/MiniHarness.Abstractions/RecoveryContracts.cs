using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHarness.Abstractions
{
    public enum RecoveryMode { Manual, Safe }
    public sealed class TaskExecutionFailure
    {
        public string TaskId { get; set; } = "";
        public string ErrorType { get; set; } = "";
        public int HResult { get; set; }
        public string? InnerType { get; set; }
        public int? InnerHResult { get; set; }
        public string Stack { get; set; } = "";
        public DateTimeOffset AtUtc { get; set; }
    }
    public enum RecoveryClassification { Ready, Terminal, Paused, Input, Approval, ExternalResult, Budget, Reconciliation, Resources, Children, ModelConfiguration, HostAuthorization, Review, NoProgress, Infrastructure }
    public sealed class RecoveryOptions
    {
        public RecoveryMode Mode { get; set; } = RecoveryMode.Manual;
        public int MaxConcurrency { get; set; } = 2;
        public int MaxAttempts { get; set; } = 1;
        public int ManagementTimeoutSeconds { get; set; } = 10;
        public int StartIntervalMilliseconds { get; set; } = 100;
        public void Validate()
        {
            if (!Enum.IsDefined(Mode) || MaxConcurrency < 1 || MaxConcurrency > 16 || MaxAttempts < 1 || MaxAttempts > 8 ||
                ManagementTimeoutSeconds < 1 || ManagementTimeoutSeconds > 60 || StartIntervalMilliseconds < 10)
                throw new ArgumentException("Invalid recovery policy.");
        }
    }
    public sealed class RecoveryRecord
    {
        public string HostGeneration { get; set; } = "";
        public RecoveryClassification Classification { get; set; }
        public bool CanResume { get; set; }
        public bool CanWake { get; set; }
        public bool AutomaticAllowed { get; set; }
        public string ContinuationToken { get; set; } = "";
        public List<string> ConsumedTokens { get; set; } = new List<string>();
        public List<string> Blockers { get; set; } = new List<string>();
        public long CheckedRevision { get; set; }
        public DateTimeOffset CheckedAtUtc { get; set; }
    }
    public sealed class RecoveryAuthorization
    {
        public bool Allowed { get; set; }
        public string Reason { get; set; } = "";
        public TaskFacts? RestrictedFacts { get; set; }
    }
    public interface IRecoveryAuthorizer
    {
        Task<RecoveryAuthorization> CheckAsync(AgentTaskState task, ModelBinding current, CancellationToken ct);
        bool AllowsUsageReconciliation { get; }
    }
    public interface IResourceRecoveryAuthorizer : IRecoveryAuthorizer
    {
        bool AllowsResourceReconciliation { get; }
    }
    public sealed class ResourceReconciliation
    {
        public string ExpectedGeneration { get; set; } = "";
        public string State { get; set; } = "cancelled";
        public bool TerminationConfirmed { get; set; }
        public int? ExitCode { get; set; }
        public string Basis { get; set; } = "";
    }
    public interface IResourceIntentLookup
    {
        Task<ResourceReference?> FindIntentAsync(string taskId, string operationId, CancellationToken ct);
    }
    public interface IResourceReconciler
    {
        Task<ResourceReference> ReconcileAsync(ResourceReference resource, ResourceReconciliation observation, CancellationToken ct);
    }
}
