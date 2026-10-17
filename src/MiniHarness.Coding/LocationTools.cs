using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding
{
    // list_files(relative_dir?, filter?, page?, page_size?)
    public sealed class ListFilesTool : CodingToolBase
    {
        public ListFilesTool(WorkspaceOptions? options = null) : base(options) { }
        public override ToolDefinition Definition { get; } = Def("list_files",
            "List files in a workspace directory, skipping build output. Returns relative paths with pagination.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"directory\":{\"type\":\"string\",\"description\":\"Relative directory; omit for workspace root.\"}," +
            "\"filter\":{\"type\":\"string\",\"description\":\"Optional filename substring filter (case-insensitive).\"}," +
            "\"page\":{\"type\":\"integer\",\"description\":\"1-based page number.\"}," +
            "\"page_size\":{\"type\":\"integer\"}}," +
            "\"additionalProperties\":false}");

        protected override Task<ToolOutcome> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
        {
            var access = new WorkspaceAccess(context.Workspace, Options);
            access.RequireGrant(WorkspaceGrants.Read);
            string directory = String(args, "directory", ".");
            int page = Math.Max(1, Int(args, "page", 1));
            int requestedPageSize = Int(args, "page_size", 200);
            if (requestedPageSize <= 0) requestedPageSize = 200;
            int pageSize = Math.Clamp(requestedPageSize, 1, Options.MaxListEntries);
            string filter = String(args, "filter", "");

            string full = access.Resolve(directory);
            if (!Directory.Exists(full)) return Task.FromResult(Fail("Directory does not exist: " + directory));

            var files = new List<string>();
            var enumerate = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string entry in Directory.EnumerateFiles(full, "*", enumerate))
            {
                ct.ThrowIfCancellationRequested();
                string relative = access.GetRelative(entry);
                if (relative.Split('/').Any(access.IsIgnoredDirectory)) continue;
                // Re-verify boundary after enumeration and reject any linked leaf.
                if (!IsInside(full, entry)) continue;
                if (filter.Length > 0 && !Path.GetFileName(entry).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(relative);
                if (files.Count > Options.MaxListEntries)
                    return Task.FromResult(Fail("Result set exceeds the " + Options.MaxListEntries + " entry limit; narrow the directory or filter."));
            }
            files.Sort(StringComparer.Ordinal);
            int total = files.Count;
            var pageItems = files.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            bool truncated = page * pageSize < total;
            return Task.FromResult(Ok(ToJson(new
            {
                files = pageItems,
                page,
                pageSize,
                total,
                truncated
            })));
        }

        private static bool IsInside(string root, string candidate)
        {
            string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            return c.Equals(r, StringComparison.OrdinalIgnoreCase) || c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }

    // search_text(query, path?, max_results?) — literal substring, regex deferred.
    public sealed class SearchTextTool : CodingToolBase
    {
        public SearchTextTool(WorkspaceOptions? options = null) : base(options) { }
        public override ToolDefinition Definition { get; } = Def("search_text",
            "Search for a literal text string across workspace text files. Returns path, 1-based line number and matched snippet.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"query\":{\"type\":\"string\"}," +
            "\"path\":{\"type\":\"string\",\"description\":\"Optional relative directory or file to scope the search.\"}," +
            "\"max_results\":{\"type\":\"integer\"}}," +
            "\"required\":[\"query\"],\"additionalProperties\":false}");

        protected override Task<ToolOutcome> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
        {
            var access = new WorkspaceAccess(context.Workspace, Options);
            access.RequireGrant(WorkspaceGrants.Read);
            string query = String(args, "query", "");
            if (query.Length == 0) return Task.FromResult(Fail("query must not be empty."));
            int requestedMax = Int(args, "max_results", 100);
            if (requestedMax <= 0) requestedMax = 100;
            int max = Math.Clamp(requestedMax, 1, Options.MaxSearchMatches);
            string scopePath = String(args, "path", ".");
            string scope = access.Resolve(scopePath);
            if (!File.Exists(scope) && !Directory.Exists(scope))
                return Task.FromResult(Fail("Search path does not exist: " + scopePath));

            var targets = new List<string>();
            if (File.Exists(scope)) targets.Add(scope);
            else
                foreach (string file in Directory.EnumerateFiles(scope, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    string relative = access.GetRelative(file);
                    if (relative.Split('/').Any(access.IsIgnoredDirectory)) continue;
                    if (new FileInfo(file).Length <= Options.MaxFileBytes) targets.Add(file);
                }

            var matches = new List<object>();
            bool truncated = false;
            foreach (string file in targets.OrderBy(f => f, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                WorkspaceAccess.TextFile text;
                try { text = access.ReadText(file); }
                catch (WorkspaceAccessException) { continue; } // binary / non-UTF8 / oversized are skipped
                for (int i = 0; i < text.Lines.Length; i++)
                {
                    int index = text.Lines[i].IndexOf(query, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        matches.Add(new
                        {
                            path = access.GetRelative(file),
                            line = i + 1,
                            snippet = Truncate(text.Lines[i], 200)
                        });
                        if (matches.Count >= max) { truncated = true; break; }
                    }
                }
                if (truncated) break;
            }
            return Task.FromResult(Ok(ToJson(new { query, matches, truncated })));
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value.Substring(0, max - 12) + " [truncated]";
    }

    // read_file(path, start_line?, line_count?) — content with line numbers, hash, truncation flag.
    public sealed class ReadFileTool : CodingToolBase
    {
        public ReadFileTool(WorkspaceOptions? options = null) : base(options) { }
        public override ToolDefinition Definition { get; } = Def("read_file",
            "Read a UTF-8 text file by 1-based line range. Returns numbered lines, the file content hash and truncation. The hash is required by apply_patch.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"path\":{\"type\":\"string\"}," +
            "\"start_line\":{\"type\":\"integer\"}," +
            "\"line_count\":{\"type\":\"integer\"}}," +
            "\"required\":[\"path\"],\"additionalProperties\":false}");

        protected override Task<ToolOutcome> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
        {
            var access = new WorkspaceAccess(context.Workspace, Options);
            access.RequireGrant(WorkspaceGrants.Read);
            string path = String(args, "path", "");
            if (path.Length == 0) return Task.FromResult(Fail("path is required."));
            string full = access.Resolve(path);
            var text = access.ReadText(full);

            int total = text.Lines.Length;
            int start = Math.Max(1, Int(args, "start_line", 1));
            int requested = Int(args, "line_count", Options.MaxReadLines);
            int count = Math.Clamp(requested <= 0 ? Options.MaxReadLines : requested, 1, Options.MaxReadLines);
            if (start > total && total > 0) return Task.FromResult(Fail("start_line " + start + " is past end of file (" + total + " lines)."));

            var builder = new System.Text.StringBuilder();
            int end = Math.Min(total, start + count - 1);
            for (int i = start; i <= end; i++)
            {
                ct.ThrowIfCancellationRequested();
                builder.Append(i).Append('\t').Append(text.Lines[i - 1]).Append('\n');
            }
            string content = builder.ToString();
            if (content.Length > Options.MaxOutputChars)
                content = content.Substring(0, Options.MaxOutputChars - 20) + "\n[output truncated]";
            bool truncated = end < total;
            return Task.FromResult(Ok(ToJson(new
            {
                path = access.GetRelative(full),
                startLine = start,
                endLine = end,
                totalLines = total,
                hash = text.Hash,
                hasBom = text.HasBom,
                truncated,
                lineEnding = DetectLineEnding(text.Text),
                content
            })));
        }

        private static string DetectLineEnding(string text)
        {
            bool crlf = text.Contains("\r\n", StringComparison.Ordinal);
            bool lf = text.Contains('\n') && text.Replace("\r\n", "").Contains('\n');
            return crlf && !lf ? "CRLF" : !crlf && lf ? "LF" : crlf ? "mixed" : "none";
        }
    }
}
