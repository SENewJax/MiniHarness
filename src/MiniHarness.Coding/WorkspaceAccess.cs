using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding
{
    // Expected, model-correctable workspace failures. These are returned to the
    // model as failed tool observations with no effect; they never escape into
    // the runtime's Unknown/reconciliation path.
    public class WorkspaceAccessException : Exception
    {
        public WorkspaceAccessException(string message) : base(message) { }
    }

    // Shared, finite bounds for every workspace operation. Defaults are small on
    // purpose: a coding loop must be able to work inside them.
    public sealed class WorkspaceOptions
    {
        public int MaxFileBytes { get; set; } = 1_000_000;
        public int MaxReadLines { get; set; } = 2_000;
        public int MaxSearchMatches { get; set; } = 200;
        public int MaxListEntries { get; set; } = 1_000;
        public int MaxOutputChars { get; set; } = 16_000;
        public string[] IgnoredDirectories { get; set; }
            = new[] { ".git", ".data", "bin", "obj", "TestResults", ".vs", "node_modules" };
    }

    // The single component allowed to interpret paths and grants for coding tools.
    // Tools never resolve or authorize paths themselves. Every method re-checks
    // the boundary immediately before touching the filesystem; registration-time
    // checks are not relied upon.
    public sealed class WorkspaceAccess
    {
        private readonly WorkspaceScope scope;
        private readonly WorkspaceOptions options;
        private readonly string root;
        private readonly HashSet<string> grants;
        private readonly HashSet<string> ignored;

        public string Root => root;
        public IReadOnlySet<string> IgnoredDirectories => ignored;

        public WorkspaceAccess(WorkspaceScope scope, WorkspaceOptions? options = null)
        {
            this.scope = scope ?? throw new WorkspaceAccessException("Workspace scope is required.");
            this.options = options ?? new WorkspaceOptions();
            if (string.IsNullOrWhiteSpace(scope.RootPath))
                throw new WorkspaceAccessException("No workspace is configured for this task.");
            root = Path.GetFullPath(scope.RootPath);
            if (!Directory.Exists(root))
                throw new WorkspaceAccessException("Workspace root does not exist: " + root);
            if (IsReparsePoint(root))
                throw new WorkspaceAccessException("Workspace root must not be a link or junction.");
            grants = new HashSet<string>(scope.Grants ?? new List<string>(), StringComparer.Ordinal);
            ignored = new HashSet<string>(this.options.IgnoredDirectories, StringComparer.Ordinal);
        }

        public void RequireGrant(string grant)
        {
            if (!grants.Contains(grant))
                throw new WorkspaceAccessException("This task is not granted workspace capability '" + grant + "'.");
        }

        // Resolve a model-supplied relative path to a full path inside the root.
        // Rejects traversal, rooted paths that escape, and any linked component.
        public string Resolve(string relative)
        {
            if (relative == null) throw new WorkspaceAccessException("Path is required.");
            string candidate = relative.Replace('/', Path.DirectorySeparatorChar).Trim();
            if (candidate.Length == 0) throw new WorkspaceAccessException("Path must not be empty.");
            string combined = Path.IsPathRooted(candidate) ? candidate : Path.Combine(root, candidate);
            string full;
            try { full = Path.GetFullPath(combined); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            { throw new WorkspaceAccessException("Invalid path: " + relative); }

            if (!IsWithinRoot(full))
                throw new WorkspaceAccessException("Path escapes the authorized workspace: " + relative);

            // Walk every component from the root down — INCLUDING the final file
            // itself — and reject reparse points. The previous Directory.Exists
            // guard skipped a symlinked FILE (a file is not a directory), letting a
            // final-path symlink read outside the root. IsReparsePoint queries the
            // directory entry itself (it does not follow the link); a genuinely
            // absent path throws FileNotFoundException and is treated as allowed so
            // create_file can target a not-yet-existing name.
            var segments = new List<string>();
            for (string? current = full; current != null && current.Length >= root.Length; current = Directory.GetParent(current)?.FullName)
            {
                if (Path.GetFullPath(current) == root) break;
                segments.Add(current);
            }
            foreach (string segment in Enumerable.Reverse(segments))
                if (IsReparsePoint(segment))
                    throw new WorkspaceAccessException("Linked paths are not permitted inside the workspace: " + relative);
            return full;
        }

        public string GetRelative(string fullPath) =>
            Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

        private bool IsWithinRoot(string full)
        {
            string trimmedRoot = Path.TrimEndingDirectorySeparator(root);
            string trimmed = Path.TrimEndingDirectorySeparator(full);
            return trimmed.Equals(trimmedRoot, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (IOException) { return false; }
        }

        public bool IsIgnoredDirectory(string name) => ignored.Contains(name);

        // Strict UTF-8 with BOM detection and preservation. Files are decoded as
        // raw bytes for hashing; line endings are never normalized by the harness.
        public sealed class TextFile
        {
            public string Text { get; init; } = "";
            public byte[] Bytes { get; init; } = Array.Empty<byte>();
            public string Hash { get; init; } = "";
            public bool HasBom { get; init; }
            public string[] Lines { get; init; } = Array.Empty<string>();
        }

        public static string HashBytes(byte[] bytes) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        public TextFile ReadText(string fullPath)
        {
            RequireGrant(WorkspaceGrants.Read);
            if (!File.Exists(fullPath)) throw new WorkspaceAccessException("File does not exist: " + GetRelative(fullPath));
            var info = new FileInfo(fullPath);
            if (info.Length > options.MaxFileBytes)
                throw new WorkspaceAccessException("File exceeds the " + options.MaxFileBytes + " byte read limit: " + GetRelative(fullPath));

            byte[] bytes = File.ReadAllBytes(fullPath);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int offset = bom ? 3 : 0;
            var strict = new UTF8Encoding(false, true);
            string text;
            try { text = strict.GetString(bytes, offset, bytes.Length - offset); }
            catch (DecoderFallbackException)
            { throw new WorkspaceAccessException("File is not valid UTF-8 text: " + GetRelative(fullPath)); }
            if (ContainsNul(text))
                throw new WorkspaceAccessException("Binary file is not supported as text: " + GetRelative(fullPath));

            string[] lines = SplitLines(text);
            return new TextFile { Text = text, Bytes = bytes, Hash = HashBytes(bytes), HasBom = bom, Lines = lines };
        }

        internal static bool ContainsNul(string text)
        {
            for (int i = 0; i < text.Length; i++) if (text[i] == '\0') return true;
            return false;
        }

        // Split while retaining no terminators; count matches the model-facing
        // 1-based line numbers. A trailing newline does not create an empty line.
        internal static string[] SplitLines(string text)
        {
            if (text.Length == 0) return Array.Empty<string>();
            var list = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    int end = i > 0 && text[i - 1] == '\r' ? i - 1 : i;
                    list.Add(text.Substring(start, end - start));
                    start = i + 1;
                }
            }
            if (start < text.Length) list.Add(text.Substring(start));
            return list.ToArray();
        }

        // Pre-write gate, executed immediately before the filesystem change rather
        // than at tool registration.
        public void EnsureWritable(string fullPath, bool allowExisting)
        {
            RequireGrant(WorkspaceGrants.Write);
            bool exists = File.Exists(fullPath);
            if (exists && !allowExisting)
                throw new WorkspaceAccessException("File already exists; create_file refuses to overwrite: " + GetRelative(fullPath));
            string? directory = Path.GetDirectoryName(fullPath);
            if (directory != null && !Directory.Exists(directory))
                throw new WorkspaceAccessException("Target directory does not exist: " + GetRelative(directory));
        }

        public WorkspaceOptions Limits => options;
    }

    public static class WorkspaceGrants
    {
        public const string Read = "read";
        public const string Write = "write";
        public const string Execute = "execute";
        public const string All = "read,write,execute";
    }
}
