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

    // A corrupt changes.json is a pre-existing state defect, not a workspace IO
    // fault. Subclassing WorkspaceAccessException keeps it a corrective failure
    // for the model, while letting write tools tell "ledger was unreadable
    // before the write" apart from "ledger record failed after the write".
    public sealed class LedgerCorruptException : WorkspaceAccessException
    {
        public LedgerCorruptException(string message) : base(message) { }
    }
    public sealed class LedgerPersistenceException : IOException
    {
        public string? DiagnosticPath { get; }
        public LedgerPersistenceException(string message, Exception inner, string? diagnosticPath) : base(message, inner)
        { DiagnosticPath = diagnosticPath; HResult = inner.HResult; }
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

        // Test-only fault injection: when non-null, a durable ledger save throws this
        // before ledger publication, after the workspace mutation has succeeded.
        // Production code leaves it null; this models a real IOException from disk.
        public Exception? PersistFault { get; set; }

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
            // Malformed JSON is a corrupt-state defect (LedgerCorruptException);
            // raw IO errors propagate unchanged so callers can place them pre/post
            // the workspace write instead of collapsing all bookkeeping failures.
            try { return JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(path), Json) ?? new LedgerFile(); }
            catch (JsonException) { throw new LedgerCorruptException("Change ledger is corrupt for this task; manual reconciliation required."); }
        }

        // Pre-write gate. A mutation is refused while the ledger cannot be read,
        // so a corrupt or unreadable changes.json is reported while the workspace
        // is provably untouched (Effect.None), never after the file has changed.
        public void EnsureReadable(string taskId)
        {
            lock (Gate(taskId)) { Load(taskId); }
        }

        private void Save(string taskId, LedgerFile ledger)
        {
            // Injected bookkeeping failure happens BEFORE the atomic move, so the
            // ledger genuinely stays without the new record — a real disk fault
            // during bookkeeping, after the workspace write already succeeded.
            string path = LedgerPath(taskId);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string operation = "injected_fault";
            int publicationAttempts = 0;
            Exception? firstPublicationFailure = null;
            try
            {
                if (PersistFault != null) throw PersistFault;
                operation = "create_directory";
                Directory.CreateDirectory(TaskDir(taskId));
                operation = "write_temporary";
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(ledger, Json));
                    stream.Write(bytes); stream.Flush(true);
                }
                operation = "publish_move_overwrite";
                // Retry publication of the SAME immutable ledger bytes, never
                // the source edit. Persistent denial still surfaces as Unknown.
                for (int attempt = 0; ; attempt++)
                {
                    publicationAttempts++;
                    try { File.Move(tmp, path, overwrite: true); break; }
                    catch (Exception ex) when (attempt < 6 && OperatingSystem.IsWindows() &&
                        (ex is IOException or UnauthorizedAccessException) && NativeError(ex) is 5 or 32 or 33 && File.Exists(tmp))
                    {
                        firstPublicationFailure ??= ex;
                        Thread.Sleep(10 << attempt);
                    }
                }
                if (firstPublicationFailure != null)
                    RecordFailure(taskId, operation, path, tmp, firstPublicationFailure, publicationAttempts, recovered: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string? diagnostic = RecordFailure(taskId, operation, path, tmp, ex, publicationAttempts);
                throw new LedgerPersistenceException("Ledger " + operation + " failed: " + ex.GetType().Name + " " +
                    "(HRESULT=0x" + ex.HResult.ToString("X8") + ", native=" + NativeError(ex) + "): " + ex.Message +
                    "; target=" + path + "; diagnostic=" + (diagnostic ?? "unavailable; original=" + ex), ex, diagnostic);
            }
        }
        private string? RecordFailure(string taskId, string operation, string path, string temporary, Exception error, int attempts, bool recovered = false)
        {
            try
            {
                string directory = Path.Combine(stateRoot, "diagnostics");
                Directory.CreateDirectory(directory);
                string report = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    capturedAtUtc = DateTimeOffset.UtcNow, taskId, operation, path, temporary, attempts, recovered,
                    exceptionType = error.GetType().FullName, hResult = "0x" + error.HResult.ToString("X8"),
                    nativeError = NativeError(error), exception = error.ToString(),
                    targetExists = File.Exists(path), temporaryExists = File.Exists(temporary),
                    targetAttributes = Attributes(path), temporaryAttributes = Attributes(temporary)
                }, Json), new UTF8Encoding(false));
                return report;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        private static string? Attributes(string path)
        {
            try { return File.Exists(path) ? File.GetAttributes(path).ToString() : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "unavailable:" + ex.GetType().Name; }
        }
        private static int? NativeError(Exception error) => ((uint)error.HResult & 0xffff0000u) == 0x80070000u ? error.HResult & 0xffff : null;

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
