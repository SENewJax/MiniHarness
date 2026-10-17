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
    public sealed class FileTaskStore : ITaskStore
    {
        private readonly string directory;
        public FileTaskStore(string directory)
        {
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
            if (File.Exists(path))
            {
                using var json = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (!json.RootElement.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != AgentTaskState.CurrentSchemaVersion)
                    throw new InvalidDataException("Unsupported task checkpoint schema version.");
                var task = json.RootElement.Deserialize<AgentTaskState>(TaskJson.Options)!;
                if (task.Id != taskId || task.Revision < 1) throw new InvalidDataException("Task checkpoint identity or revision is invalid.");
                return task;
            }
            string legacyPath = Path.Combine(directory, "resp_" + taskId.Substring(5) + ".json");
            if (!File.Exists(legacyPath)) return null;
            using var legacy = await ReadAsync(legacyPath, cancellationToken).ConfigureAwait(false);
            return LegacyTaskMigration.Convert(legacy.RootElement.Deserialize<RunState>(TaskJson.Options)!);
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
            if (File.Exists(path))
            {
                using var index = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
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
        private static async Task<JsonDocument> ReadAsync(string path, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, value, TaskJson.Options, cancellationToken).ConfigureAwait(false);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
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
                state.Revision = expectedRevision + 1;
                foreach (var item in state.Events.Where(e => e.Revision == 0)) item.Revision = state.Revision;
                try { await WriteAsync(Path.Combine(owner.directory, id + ".json"), state, cancellationToken).ConfigureAwait(false); }
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
