using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public sealed class FileTaskStore : ITaskStore, ITaskCatalog
    {
        private readonly string directory;
        private readonly long maxCheckpointBytes;
        public System.Collections.Generic.IEnumerable<string> EnumerateTaskIds()
            => Directory.EnumerateFiles(directory, "task_*.json").Select(Path.GetFileNameWithoutExtension).Select(id => id!);
        // A writer lease excludes other writers, not status/event readers.
        // Windows replacement can fail while a reader still owns a handle.
        // Bounded stripes coordinate handle lifetime and publication across
        // store instances without retaining one lock per historical task.
        private static readonly SemaphoreSlim[] fileGates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
        private static SemaphoreSlim FileGate(string path)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            return fileGates[(int)((uint)comparer.GetHashCode(path) % (uint)fileGates.Length)];
        }
        public FileTaskStore(string directory) : this(directory, 64 * 1024 * 1024) { }
        public FileTaskStore(string directory, long maxCheckpointBytes)
        {
            if (maxCheckpointBytes < 4096) throw new ArgumentException("Checkpoint byte limit is too small.");
            this.maxCheckpointBytes = maxCheckpointBytes;
            this.directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(this.directory);
        }
        public static string NormalizeId(string id)
        {
            if (!Regex.IsMatch(id, "^(task_|resp_)[a-f0-9]{32}$", RegexOptions.CultureInvariant))
                throw new ArgumentException("Invalid task or response identity.");
            return "task_" + id.Substring(5);
        }
        public async Task<AgentTaskState?> LoadAsync(string id, CancellationToken cancellationToken)
        {
            string taskId = NormalizeId(id);
            string path = Path.Combine(directory, taskId + ".json");
            using var json = await ReadIfExistsAsync(path, cancellationToken).ConfigureAwait(false);
            if (json != null)
            {
                var task = TaskStateMigration.Read(json.RootElement);
                if (task.Id != taskId || task.Revision < 1) throw new InvalidDataException("Task checkpoint identity or revision is invalid.");
                return task;
            }
            string legacyPath = Path.Combine(directory, "resp_" + taskId.Substring(5) + ".json");
            using var legacy = await ReadIfExistsAsync(legacyPath, cancellationToken).ConfigureAwait(false);
            if (legacy == null) return null;
            var converted = LegacyTaskMigration.Convert(legacy.RootElement.Deserialize<RunState>(TaskJson.Options)!);
            TaskStateMigration.InitializeLedger(converted); return converted;
        }
        public Task<ITaskLease> AcquireAsync(string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string taskId = NormalizeId(id);
            try
            {
                var ownership = new FileStream(Path.Combine(directory, taskId + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return Task.FromResult<ITaskLease>(new Lease(this, taskId, ownership));
            }
            catch (IOException) { throw new TaskConflictException("Another executor owns this task."); }
        }
        public async Task<TaskResponseReference?> ResolveResponseAsync(string responseId, CancellationToken cancellationToken)
        {
            if (!responseId.StartsWith("resp_", StringComparison.Ordinal)) throw new ArgumentException("Invalid response ID.");
            NormalizeId(responseId);
            string path = Path.Combine(directory, "response-" + responseId + ".json");
            using var index = await ReadIfExistsAsync(path, cancellationToken).ConfigureAwait(false);
            if (index != null)
            {
                return index.RootElement.Deserialize<TaskResponseReference>(TaskJson.Options);
            }
            // The index is rebuildable: a crash after the task commit cannot hide a response.
            foreach (string taskPath in Directory.EnumerateFiles(directory, "task_*.json"))
            {
                var state = await LoadAsync(Path.GetFileNameWithoutExtension(taskPath), cancellationToken).ConfigureAwait(false);
                var turn = state!.Turns.FirstOrDefault(t => t.ResponseId == responseId);
                if (turn != null) return new TaskResponseReference { TaskId = state.Id, TurnId = turn.Id };
            }
            var legacy = await LoadAsync(responseId, cancellationToken).ConfigureAwait(false);
            return legacy == null ? null : new TaskResponseReference { TaskId = legacy.Id, TurnId = legacy.Turns[0].Id };
        }
        private async Task<JsonDocument?> ReadIfExistsAsync(string path, CancellationToken cancellationToken)
        {
            var gate = FileGate(path);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(path)) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > maxCheckpointBytes) throw new InvalidDataException("Task checkpoint exceeds its byte limit.");
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken, long maxBytes = long.MaxValue)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, value, TaskJson.Options, cancellationToken).ConfigureAwait(false);
                    if (stream.Length > maxBytes) throw new IOException("Task checkpoint byte limit exceeded; no snapshot was published.");
                    stream.Flush(true);
                }
                for (int attempt = 0; ; attempt++)
                {
                    var gate = FileGate(path); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try { File.Move(temporary, path, true); break; }
                    catch (Exception error) when (attempt < 6 && (error is IOException || error is UnauthorizedAccessException) &&
                        ((uint)error.HResult & 0xffff0000u) == 0x80070000u && (error.HResult & 0xffff) is 5 or 32 or 33)
                    { /* Retry only publishing the same immutable snapshot, never model/tool effects. */ }
                    finally { gate.Release(); }
                    await Task.Delay(10 << attempt, cancellationToken).ConfigureAwait(false);
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private sealed class Lease : ITaskLease
        {
            private readonly FileTaskStore owner;
            private readonly string id;
            private readonly FileStream ownership;
            private bool disposed;
            public Lease(FileTaskStore owner, string id, FileStream ownership) { this.owner = owner; this.id = id; this.ownership = ownership; }
            public Task<AgentTaskState?> LoadAsync(CancellationToken cancellationToken) => owner.LoadAsync(id, cancellationToken);
            public async Task CommitAsync(AgentTaskState state, long expectedRevision, CancellationToken cancellationToken)
            {
                if (disposed) throw new ObjectDisposedException(nameof(Lease));
                if (state.Id != id || state.SchemaVersion != AgentTaskState.CurrentSchemaVersion) throw new ArgumentException("Invalid task checkpoint.");
                var existing = await LoadAsync(cancellationToken).ConfigureAwait(false);
                if ((existing?.Revision ?? 0) != expectedRevision) throw new TaskConflictException("Task revision changed.");
                TaskStateMigration.Validate(state);
                string checkpointPath = Path.Combine(owner.directory, id + ".json");
                using (var source = await owner.ReadIfExistsAsync(checkpointPath, cancellationToken).ConfigureAwait(false))
                    if (source != null && source.RootElement.GetProperty("schemaVersion").GetInt32() < AgentTaskState.CurrentSchemaVersion)
                    {
                        string backup = checkpointPath + ".v" + source.RootElement.GetProperty("schemaVersion").GetInt32() + ".bak";
                        if (!File.Exists(backup))
                        {
                            string pendingBackup = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try { File.Copy(checkpointPath, pendingBackup); File.Move(pendingBackup, backup); }
                            finally { if (File.Exists(pendingBackup)) File.Delete(pendingBackup); }
                        }
                    }
                state.Revision = expectedRevision + 1;
                foreach (var item in state.Events.Where(e => e.Revision == 0)) item.Revision = state.Revision;
                try { await WriteAsync(checkpointPath, state, cancellationToken, owner.maxCheckpointBytes).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    state.Revision = expectedRevision;
                    throw new CheckpointWriteException(ex);
                }
                // Response indexes are projections, never the authoritative execution record.
                foreach (var turn in state.Turns)
                {
                    try
                    {
                        await WriteAsync(Path.Combine(owner.directory, "response-" + turn.ResponseId + ".json"),
                            new TaskResponseReference { TaskId = id, TurnId = turn.Id }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    { /* ResolveResponseAsync rebuilds missing indexes from committed task snapshots. */ }
                }
            }
            public ValueTask DisposeAsync()
            {
                if (!disposed) { disposed = true; ownership.Dispose(); }
                return ValueTask.CompletedTask;
            }
        }
    }
}
