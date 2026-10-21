using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHarness.Abstractions
{
    public enum ModelInvocationPurpose { Reasoning, Summary }
    public enum ModelInvocationStatus { Started, Responded, Unknown, Reconciled }
    public enum CompactionStatus { Planned, Started, Responded, Committed, Failed, Unknown }
    public sealed class ContextOptions
    {
        public bool Enabled { get; set; }
        public double TriggerRatio { get; set; } = 0.75;
        public double TargetRatio { get; set; } = 0.55;
        public int KeepRecentGroups { get; set; } = 2;
        public int MaxSummaryCharacters { get; set; } = 6000;
        public int SummaryMaxOutputTokens { get; set; } = 2048;
        public int SummaryTimeoutSeconds { get; set; } = 60;
        public int MaxSummaryCallsPerAdvance { get; set; } = 2;
        public int MaxSummaryCallsPerTask { get; set; } = 32;
        public double MinimumReductionRatio { get; set; } = 0.05;
        public int FailureBackoffCalls { get; set; } = 2;
        public long MaxCheckpointBytes { get; set; } = 64 * 1024 * 1024;
        public int MaxHistoryReadBytes { get; set; } = 65536;
        public void Validate()
        {
            if (!(TargetRatio > 0 && TargetRatio < TriggerRatio && TriggerRatio < 1) || KeepRecentGroups < 1 ||
                MaxSummaryCharacters < 128 || SummaryMaxOutputTokens < 32 || SummaryTimeoutSeconds < 1 || SummaryTimeoutSeconds > 600 ||
                MaxSummaryCallsPerAdvance < 1 || MaxSummaryCallsPerAdvance > 8 || MaxSummaryCallsPerTask < 1 ||
                MinimumReductionRatio < 0 || MinimumReductionRatio >= 1 || FailureBackoffCalls < 1 ||
                MaxCheckpointBytes < 4096 || MaxHistoryReadBytes < 1024 || MaxHistoryReadBytes > 1024 * 1024)
                throw new ArgumentException("Invalid context limits.");
        }
    }
    public sealed class SummaryContent
    {
        public string Background { get; set; } = "";
        public List<string> Tried { get; set; } = new List<string>();
        public List<string> Pending { get; set; } = new List<string>();
        public List<string> Paths { get; set; } = new List<string>();
        public List<string> References { get; set; } = new List<string>();
    }
    public sealed class ContextMemory
    {
        public string? SummaryId { get; set; }
        public string PolicyVersion { get; set; } = "prefix-v1";
        public int CoveredCount { get; set; }
        public string SourceHash { get; set; } = "";
    }
    public sealed class ModelInvocationRecord
    {
        public string Id { get; set; } = "invocation_" + Guid.NewGuid().ToString("N");
        public string TurnId { get; set; } = "";
        public string? StepId { get; set; }
        public string? CompactionId { get; set; }
        public ModelInvocationPurpose Purpose { get; set; }
        public ModelInvocationStatus Status { get; set; }
        public ModelBinding Binding { get; set; } = new ModelBinding();
        public ModelRequest Request { get; set; } = new ModelRequest();
        public ModelResponse? Response { get; set; }
        public TokenUsage Usage { get; set; } = new TokenUsage { Known = false };
        public bool UsageApplied { get; set; }
        public bool ResultApplied { get; set; }
        public string? UsageReconciliation { get; set; }
    }
    public sealed class CompactionRecord
    {
        public string Id { get; set; } = "summary_" + Guid.NewGuid().ToString("N");
        public string Key { get; set; } = "";
        public string TurnId { get; set; } = "";
        public string? InvocationId { get; set; }
        public CompactionStatus Status { get; set; }
        public string? ParentSummaryId { get; set; }
        public int CoveredCount { get; set; }
        public string SourceHash { get; set; } = "";
        public List<string> SourceIds { get; set; } = new List<string>();
        public string TemplateVersion { get; set; } = "summary-json-v2";
        public ModelBinding Binding { get; set; } = new ModelBinding();
        public int SteeringRevision { get; set; }
        public SummaryContent? Content { get; set; }
        public string ContentHash { get; set; } = "";
        public string Error { get; set; } = "";
        public int AttemptAtModelCall { get; set; }
    }
    public sealed class ContextDiagnostics
    {
        public int BeforeCharacters { get; set; }
        public int AfterCharacters { get; set; }
        public int HardLimit { get; set; }
        public int PinnedCharacters { get; set; }
        public int SummaryCharacters { get; set; }
        public string Reason { get; set; } = "";
        public string? SummaryId { get; set; }
    }
    public sealed class ModelUsageReconciliation
    {
        public string InvocationId { get; set; } = "";
        public TokenUsage Usage { get; set; } = new TokenUsage();
        public string Basis { get; set; } = "";
        public bool ConservativeUpperBound { get; set; }
        public List<LegacyTurnUsage> LegacyTurns { get; set; } = new List<LegacyTurnUsage>();
    }
    public sealed class LegacyTurnUsage
    {
        public string TurnId { get; set; } = "";
        public TokenUsage Usage { get; set; } = new TokenUsage();
    }
    public sealed class TaskHistoryPage
    {
        public List<HistoryEntry> Entries { get; set; } = new List<HistoryEntry>();
        public string? NextId { get; set; }
        public bool HasMore { get; set; }
    }
    public interface IContextComposer : IContextBuilder
    {
        ModelRequest ComposeDraft(RunState state, IReadOnlyList<ToolDefinition> localTools);
        int Measure(ModelRequest request);
        void ValidateCapacity(ModelRequest request);
    }
    public interface IModelContinuationPolicy
    {
        bool CanCompactClosedMessage(Message message);
    }
    public interface ISummaryGenerator
    {
        ModelRequest CreateRequest(string model, SummaryContent? previous, IReadOnlyList<HistoryEntry> source, int maxOutputTokens, int maxCharacters);
    }
    public interface ITaskDiagnostics
    {
        Task<TaskHistoryPage> ReadHistoryAsync(string taskId, string? afterId, int maxBytes, CancellationToken ct);
        Task<TaskHistoryChunk> ReadHistoryEntryAsync(string taskId, string historyId, int offset, int maxBytes, CancellationToken ct);
        Task<TaskExecutionFailure?> ReadFailureAsync(string taskId, CancellationToken ct);
    }
    public sealed class TaskHistoryChunk
    {
        public string HistoryId { get; set; } = "";
        public int Offset { get; set; }
        public int NextOffset { get; set; }
        public int TotalCharacters { get; set; }
        public string ContentHash { get; set; } = "";
        public string Data { get; set; } = "";
    }
}
