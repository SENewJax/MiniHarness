using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace MiniHarness.Abstractions
{
    public static class ResourceStates
    {
        public static bool IsTerminal(string state) => state == "finished" || state == "cancelled" || state == "failed";
    }
    public sealed class ResourceRequestRejectedException : ArgumentException
    {
        public ResourceRequestRejectedException(string message) : base(message) { }
    }
    public sealed class ProcessSnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public string Profile { get; set; } = "";
        public string ProfileVersion { get; set; } = "";
        public string Generation { get; set; } = "";
        public int? Pid { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset DeadlineUtc { get; set; }
        public int? ExitCode { get; set; }
        public bool TerminationConfirmed { get; set; }
        public bool InputClosed { get; set; }
        public bool OutputDegraded { get; set; }
        public long FirstAvailableSequence { get; set; } = 1;
        public long LastSequence { get; set; }
        public long Version { get; set; }
        public string StopReason { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ArtifactReference? OutputArtifact { get; set; }
    }
    public sealed class ProcessOutputChunk
    {
        public long Sequence { get; set; }
        public string Stream { get; set; } = "";
        public string Text { get; set; } = "";
    }
    public sealed class ProcessOutputPage
    {
        public List<ProcessOutputChunk> Chunks { get; set; } = new List<ProcessOutputChunk>();
        public long FirstAvailableSequence { get; set; }
        public long NextCursor { get; set; }
        public bool HasMore { get; set; }
        public bool Gap { get; set; }
        public bool Truncated { get; set; }
    }
    public sealed class ResourceInput
    {
        public string CommandId { get; set; } = "";
        public string ExpectedGeneration { get; set; } = "";
        public string Text { get; set; } = "";
        public bool CloseInput { get; set; }
    }
    public enum ResourceWaitCondition { Exit, Output }
    public sealed class ResourceWaitRequest
    {
        public string ResourceId { get; set; } = "";
        public ResourceWaitCondition Condition { get; set; }
        public long AfterSequence { get; set; }
        public int WaitMilliseconds { get; set; } = 30000;
    }
    public sealed class ResourceWait
    {
        public string Id { get; set; } = "wait_" + Guid.NewGuid().ToString("N");
        public string TurnId { get; set; } = "";
        public ResourceWaitRequest Request { get; set; } = new ResourceWaitRequest();
        public DateTimeOffset DeadlineUtc { get; set; }
        public string Status { get; set; } = "active";
        public long ConsumedVersion { get; set; }
        public bool CompletionCandidate { get; set; }
    }
    public sealed class TaskSteering
    {
        public string Text { get; set; } = "";
        public List<string> Constraints { get; set; } = new List<string>();
    }
    public sealed class ResourceWake
    {
        public string WaitId { get; set; } = "";
    }
    public interface IInteractiveResourceManager : IResourceManager
    {
        Task<ProcessOutputPage> ReadOutputAsync(ResourceReference resource, long after, int maxBytes, CancellationToken cancellationToken);
        Task<ResourceReference> WriteInputAsync(ResourceReference resource, ResourceInput input, CancellationToken cancellationToken);
    }
    public interface IResourceWaitPolicy
    {
        int MaxWaitMilliseconds { get; }
    }
    public interface ITaskCatalog
    {
        IEnumerable<string> EnumerateTaskIds();
    }
    public sealed class ResourceInputAction
    {
        public string CommandId { get; set; } = "";
        public string ResourceId { get; set; } = "";
        public string Status { get; set; } = "started";
        public string Fingerprint { get; set; } = "";
    }
    public interface IResourceInputJournal
    {
        Task<string> ReadInputStatusAsync(ResourceReference resource, string commandId, CancellationToken cancellationToken);
    }
    public interface IResourceOutputAccess
    {
        Task<ProcessOutputPage> ReadResourceOutputAsync(string taskId, string resourceId, long after, int maxBytes, CancellationToken cancellationToken);
    }
}
