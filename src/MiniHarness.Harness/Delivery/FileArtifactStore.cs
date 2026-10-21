using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness.Delivery
{
    public static class SafeFiles
    {
        public static string Relative(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains(':') || value.Contains('\\') ||
                value.Split('/').Any(p => p == ".." || p == "." || p.Length == 0)) throw new ArgumentException("Invalid relative artifact/protection path.");
            return value;
        }
        public static string Within(string root, string relative)
        {
            Relative(relative); root = Path.GetFullPath(root);
            string resolved = Path.GetFullPath(Path.Combine(root, relative));
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path escapes its root.");
            for (string? path = resolved; path != null; path = Path.GetDirectoryName(path))
            {
                if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked artifact paths are forbidden.");
                if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
            }
            return resolved;
        }
        public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        public static async Task<byte[]> Read(string path, long max, CancellationToken ct)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            if (stream.Length > max) throw new IOException("Artifact byte limit exceeded.");
            using var output = new MemoryStream(); byte[] buffer = new byte[8192]; int size;
            while ((size = await stream.ReadAsync(buffer, ct)) > 0)
            { if (output.Length + size > max) throw new IOException("Artifact grew beyond byte limit."); await output.WriteAsync(buffer.AsMemory(0, size), ct); }
            return output.ToArray();
        }
        public static IEnumerable<string> Files(string root)
        {
            foreach (var file in Directory.EnumerateFiles(root)) { Within(root, Path.GetFileName(file)); yield return file; }
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(directory);
                if (name == ".git" || name == "bin" || name == "obj" || name == ".data") continue;
                Within(root, name);
                foreach (var file in Files(directory)) yield return file;
            }
        }
        public static bool Matches(string path, string pattern) => path.Equals(pattern, StringComparison.OrdinalIgnoreCase) || path.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        private static IEnumerable<string> ProtectedFiles(string root, AcceptanceSpec spec, CancellationToken ct)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string scope in spec.ProtectedPaths)
            {
                string target = Within(root, scope);
                var pending = new Stack<string>(); pending.Push(target);
                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    string path = pending.Pop();
                    Within(root, Path.GetRelativePath(root, path).Replace('\\', '/'));
                    if (!seen.Add(path)) continue;
                    if (seen.Count > 65536) throw new IOException("Protected path inventory limit exceeded.");
                    if (Directory.Exists(path))
                    {
                        foreach (string entry in Directory.EnumerateFileSystemEntries(path)) pending.Push(entry);
                    }
                    else if (File.Exists(path)) yield return path;
                }
            }
        }
        public static async Task<ProtectionBaseline> Capture(string root, AcceptanceSpec spec, long max, CancellationToken ct)
        {
            var baseline = new ProtectionBaseline();
            if (spec.ProtectedPaths.Count == 0) return baseline;
            foreach (var file in ProtectedFiles(root, spec, ct))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (spec.ProtectedPaths.Any(p => Matches(relative, p))) baseline.Files.Add(relative, Hash(await Read(file, max, ct)));
            }
            foreach (string path in spec.ProtectedPaths)
                if (!baseline.Files.Keys.Any(p => Matches(p, path)) && !Directory.Exists(Within(root, path))) throw new ArgumentException("Protected path does not exist: " + path);
            baseline.ContentHash = Acceptance.AcceptanceBinding.Hash(baseline.Files.OrderBy(p => p.Key, StringComparer.Ordinal)); return baseline;
        }
        public static async Task<List<string>> Check(string root, AcceptanceSpec spec, ProtectionBaseline baseline, long max, CancellationToken ct)
        {
            var issues = new List<string>();
            foreach (var pair in baseline.Files)
            {
                string file = Within(root, pair.Key);
                if (!File.Exists(file) || Hash(await Read(file, max, ct)) != pair.Value) issues.Add(pair.Key);
            }
            if (spec.ProtectedPaths.Count > 0)
                foreach (var file in ProtectedFiles(root, spec, ct))
                {
                    string path = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!baseline.Files.ContainsKey(path) && spec.ProtectedPaths.Any(p => Matches(path, p)) && !spec.AllowNewFilesUnder.Any(p => Matches(path, p))) issues.Add(path);
                }
            return issues;
        }
    }
    public sealed class FileArtifactStore : IArtifactStore
    {
        private readonly string root; private readonly string[] sources; private readonly DeliveryOptions options;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public FileArtifactStore(string root, IEnumerable<string> sourceRoots, DeliveryOptions options)
        { options.Validate(); this.root = Path.GetFullPath(root); sources = sourceRoots.Select(Path.GetFullPath).ToArray(); this.options = options; Directory.CreateDirectory(this.root); }
        public async Task<ArtifactReference> ImportAsync(string taskId, ArtifactReference source, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(options.IoTimeoutSeconds)); ct = deadline.Token;
            if (source.OwnerTaskId.Length > 0 && source.OwnerTaskId != taskId) throw new ArgumentException("Cross-task artifact.");
            if (source.BlobHash != null) { await ReadAsync(taskId, source, ct); return TaskJson.Clone(source); }
            string? path = null;
            foreach (string directory in sources)
            {
                string absolute = Path.GetFullPath(source.Location);
                if (absolute.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    path = SafeFiles.Within(directory, Path.GetRelativePath(directory, absolute).Replace('\\', '/'));
            }
            if (path == null) throw new ArgumentException("Artifact source is outside host allowed roots.");
            byte[] bytes = await SafeFiles.Read(path, options.MaxArtifactBytes, ct);
            if (SafeFiles.Hash(bytes) != source.ContentHash) throw new IOException("Artifact source hash mismatch.");
            var result = await WriteAsync(taskId, source.Id, bytes, ct); result.LogicalName = source.LogicalName ?? source.Id; return result;
        }
        public async Task<ArtifactReference> WriteAsync(string taskId, string name, byte[] bytes, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(options.IoTimeoutSeconds)); ct = deadline.Token;
            FileTaskStore.NormalizeId(taskId); SafeFiles.Relative(name);
            if (bytes.LongLength > options.MaxArtifactBytes) throw new IOException("Artifact size limit.");
            string hash = SafeFiles.Hash(bytes); string logical = "blobs/" + hash;
            await gate.WaitAsync(ct);
            try
            {
                string directory = SafeFiles.Within(root, taskId); Directory.CreateDirectory(directory);
                string path = SafeFiles.Within(directory, logical); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path))
                {
                    var existing = SafeFiles.Files(directory).ToArray();
                    if (existing.Length >= options.MaxArtifacts || existing.Sum(f => new FileInfo(f).Length) + bytes.LongLength > options.MaxTaskBytes) throw new IOException("Task artifact quota.");
                    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { await File.WriteAllBytesAsync(temporary, bytes, ct); File.Move(temporary, path); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                else if (SafeFiles.Hash(await SafeFiles.Read(path, options.MaxArtifactBytes, ct)) != hash) throw new IOException("Stored blob is corrupt.");
                return new ArtifactReference { Id = name, OwnerTaskId = taskId, Location = logical, BlobHash = hash, ContentHash = hash, Length = bytes.LongLength };
            }
            finally { gate.Release(); }
        }
        public async Task<byte[]> ReadAsync(string taskId, ArtifactReference artifact, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(options.IoTimeoutSeconds)); ct = deadline.Token;
            FileTaskStore.NormalizeId(taskId);
            if (artifact.OwnerTaskId != taskId || artifact.BlobHash == null || artifact.BlobHash != artifact.ContentHash ||
                artifact.BlobHash.Length != 64 || artifact.BlobHash.Any(c => !Uri.IsHexDigit(c)) || artifact.Location != "blobs/" + artifact.BlobHash)
                throw new ArgumentException("Invalid artifact identity.");
            byte[] bytes = await SafeFiles.Read(SafeFiles.Within(root, taskId + "/" + artifact.Location), options.MaxArtifactBytes, ct);
            if (SafeFiles.Hash(bytes) != artifact.ContentHash || bytes.LongLength != artifact.Length) throw new IOException("Artifact integrity failure.");
            return bytes;
        }
    }
}
