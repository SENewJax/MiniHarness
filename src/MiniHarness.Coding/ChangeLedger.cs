using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding
{
    public sealed class ChangeRecord
    {
        public string Path { get; set; } = "";
        public string Status { get; set; } = "modified"; // modified | added
        public string BeforeHash { get; set; } = "";
        public string AfterHash { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string TurnId { get; set; } = "";
        public long UpdatedAt { get; set; }
    }

    internal sealed class LedgerFile
    {
        public List<ChangeRecord> Changes { get; set; } = new List<ChangeRecord>();
        // First-touch baseline per workspace-relative path: content-addressed
        // snapshot of the bytes at the moment of the agent's first edit. User
        // changes that predate the task are never counted as agent output.
        public Dictionary<string, string> Baselines { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Records agent-owned file changes outside the source workspace, so the
    /// source fingerprint is never polluted and dirty user files are never
    /// claimed as agent work. No git command is ever executed; no reset occurs.
    /// </summary>
    public sealed class ChangeLedger
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly string stateRoot;
        public ChangeLedger(string stateRoot)
        {
            this.stateRoot = string.IsNullOrWhiteSpace(stateRoot)
                ? throw new ArgumentException("Change ledger state directory is required.")
                : Path.GetFullPath(stateRoot);
        }

        private string TaskDir(string taskId)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                taskId = taskId.Replace(c, '_');
            return Path.Combine(stateRoot, taskId);
        }

        private string LedgerPath(string taskId) => Path.Combine(TaskDir(taskId), "changes.json");
        private string SnapshotPath(string taskId, string hash) => Path.Combine(TaskDir(taskId), "snapshots", hash + ".bin");

        private LedgerFile Load(string taskId)
        {
            string path = LedgerPath(taskId);
            if (!File.Exists(path)) return new LedgerFile();
            try { return JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(path), Json) ?? new LedgerFile(); }
            catch (JsonException) { throw new WorkspaceAccessException("Change ledger is corrupt for this task; manual reconciliation required."); }
        }

        private void Save(string taskId, LedgerFile ledger)
        {
            Directory.CreateDirectory(TaskDir(taskId));
            string path = LedgerPath(taskId);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ledger, Json), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }

        // Returns the baseline hash to associate with the pre-edit content of a
        // path. On first touch the current bytes are snapshotted outside the
        // workspace; on later edits the original baseline is preserved.
        public string EnsureBaseline(string taskId, string relativePath, byte[] currentBytes)
        {
            lock (Gate(taskId))
            {
                var ledger = Load(taskId);
                if (ledger.Baselines.TryGetValue(relativePath, out var existing)) return existing;
                string hash = WorkspaceAccess.HashBytes(currentBytes);
                string snapshot = SnapshotPath(taskId, hash);
                Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
                File.WriteAllBytes(snapshot, currentBytes);
                ledger.Baselines[relativePath] = hash;
                Save(taskId, ledger);
                return hash;
            }
        }

        public void RecordAdded(string taskId, string relativePath, string afterHash, ToolExecutionContext context)
        {
            lock (Gate(taskId))
            {
                var ledger = Load(taskId);
                Upsert(ledger, relativePath, "", afterHash, "added", context);
                Save(taskId, ledger);
            }
        }

        public void RecordModified(string taskId, string relativePath, string beforeHash, string afterHash, ToolExecutionContext context)
        {
            lock (Gate(taskId))
            {
                var ledger = Load(taskId);
                // Anchor the displayed before-hash to the first-touch baseline.
                string baseline = ledger.Baselines.TryGetValue(relativePath, out var snap) ? snap : beforeHash;
                Upsert(ledger, relativePath, baseline, afterHash, "modified", context);
                Save(taskId, ledger);
            }
        }

        private static void Upsert(LedgerFile ledger, string path, string before, string after, string status, ToolExecutionContext context)
        {
            var record = ledger.Changes.FirstOrDefault(c => c.Path == path);
            if (record == null)
            {
                record = new ChangeRecord { Path = path, BeforeHash = before, Status = status, OperationId = context.OperationId, TurnId = context.TurnId };
                ledger.Changes.Add(record);
            }
            record.AfterHash = after;
            record.Status = status;
            record.OperationId = context.OperationId;
            record.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        public IReadOnlyList<ChangeRecord> List(string taskId)
        {
            lock (Gate(taskId)) { return Load(taskId).Changes; }
        }

        public byte[]? BaselineBytes(string taskId, string baselineHash)
        {
            string path = SnapshotPath(taskId, baselineHash);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        private readonly Dictionary<string, object> gates = new Dictionary<string, object>();
        private readonly object gateLock = new object();
        private object Gate(string taskId)
        {
            lock (gateLock)
            {
                if (!gates.TryGetValue(taskId, out var gate)) { gate = new object(); gates[taskId] = gate; }
                return gate;
            }
        }
    }
}
