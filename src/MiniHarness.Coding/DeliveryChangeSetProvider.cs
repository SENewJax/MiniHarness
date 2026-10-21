using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding
{
    public sealed class DeliveryChangeSetProvider : IChangeSetProvider
    {
        private readonly ChangeLedger ledger;
        private readonly HashSet<string> ignored;
        public DeliveryChangeSetProvider(ChangeLedger ledger, WorkspaceOptions? options = null)
        { this.ledger = ledger; ignored = (options ?? new WorkspaceOptions()).IgnoredDirectories.ToHashSet(System.StringComparer.OrdinalIgnoreCase); }
        private IEnumerable<string> Files(string root)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked workspace inventory path.");
                if ((attributes & FileAttributes.Directory) != 0)
                { if (!ignored.Contains(Path.GetFileName(entry))) foreach (var file in Files(entry)) yield return file; }
                else yield return entry;
            }
        }
        public async Task<Dictionary<string, string>> CaptureBaselineAsync(WorkspaceScope workspace, CancellationToken ct)
        {
            var files = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var file in Files(workspace.RootPath))
            {
                ct.ThrowIfCancellationRequested();
                if (files.Count >= 4096 || new FileInfo(file).Length > 8 * 1024 * 1024) throw new IOException("Workspace inventory bound exceeded.");
                byte[] bytes = await File.ReadAllBytesAsync(file, ct); total += bytes.LongLength;
                if (bytes.Length > 8 * 1024 * 1024 || total > 64 * 1024 * 1024) throw new IOException("Workspace inventory byte bound exceeded.");
                files.Add(Path.GetRelativePath(workspace.RootPath, file).Replace('\\', '/'), WorkspaceAccess.HashBytes(bytes));
            }
            return files;
        }
        public async Task<ChangeSetSnapshot> FreezeAsync(AgentTaskState task, CancellationToken ct)
        {
            var snapshot = new ChangeSetSnapshot();
            var inventory = await CaptureBaselineAsync(task.Facts.Workspace, ct);
            IReadOnlyList<ChangeRecord> records;
            try { records = ledger.List(task.Id); }
            catch (LedgerCorruptException) { snapshot.Conflicts.Add("ledger_corrupt"); return snapshot; }
            foreach (var record in records)
            {
                ct.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(task.Facts.Workspace.RootPath, record.Path));
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(task.Facts.Workspace.RootPath)) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, System.StringComparison.OrdinalIgnoreCase)) throw new IOException("Change path escapes workspace.");
                for (string? current = path; current != null && current.Length >= root.Length; current = Path.GetDirectoryName(current))
                    if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked change path.");
                if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) { snapshot.Conflicts.Add(record.Path); continue; }
                byte[] after = File.ReadAllBytes(path);
                if (WorkspaceAccess.HashBytes(after) != record.AfterHash) snapshot.Conflicts.Add(record.Path);
                if (task.Acceptance!.WorkspaceBaseline.TryGetValue(record.Path, out var initial) && initial != record.BeforeHash)
                    snapshot.Conflicts.Add("baseline_changed:" + record.Path);
                snapshot.Entries.Add(new ChangeSetEntry { Path = record.Path, BeforeHash = record.BeforeHash, AfterHash = record.AfterHash,
                    Before = ledger.BaselineBytes(task.Id, record.BeforeHash), After = after, OperationId = record.OperationId });
            }
            var owned = snapshot.Entries.Select(e => e.Path).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
            foreach (var pair in task.Acceptance!.WorkspaceBaseline)
                if (!owned.Contains(pair.Key))
                {
                    string file = Path.Combine(task.Facts.Workspace.RootPath, pair.Key);
                    if (!File.Exists(file) || new FileInfo(file).Length > 8 * 1024 * 1024) snapshot.Conflicts.Add("external_missing_or_oversized:" + pair.Key);
                    else
                    {
                        byte[] bytes = File.ReadAllBytes(file); string hash = WorkspaceAccess.HashBytes(bytes);
                        if (hash != pair.Value) snapshot.ExternalEntries.Add(new ChangeSetEntry { Path = pair.Key, BeforeHash = pair.Value, AfterHash = hash, After = bytes, OperationId = "external" });
                    }
                }
            foreach (var pair in inventory.Where(p => !owned.Contains(p.Key) && !task.Acceptance.WorkspaceBaseline.ContainsKey(p.Key)))
            {
                byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(task.Facts.Workspace.RootPath, pair.Key), ct);
                if (WorkspaceAccess.HashBytes(bytes) != pair.Value) snapshot.Conflicts.Add("external_file_changed_during_snapshot:" + pair.Key);
                snapshot.ExternalEntries.Add(new ChangeSetEntry { Path = pair.Key, AfterHash = pair.Value, After = bytes, OperationId = "external" });
            }
            snapshot.Entries = snapshot.Entries.OrderBy(e => e.Path, System.StringComparer.Ordinal).ToList();
            snapshot.ExternalEntries = snapshot.ExternalEntries.OrderBy(e => e.Path, System.StringComparer.Ordinal).ToList();
            snapshot.Conflicts = snapshot.Conflicts.OrderBy(p => p, System.StringComparer.Ordinal).ToList();
            return snapshot;
        }
    }
}
