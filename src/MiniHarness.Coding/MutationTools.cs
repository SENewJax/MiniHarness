using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding
{
    // create_file(path, content) — refuses to overwrite an existing file.
    public sealed class CreateFileTool : CodingToolBase
    {
        private readonly ChangeLedger ledger;
        public CreateFileTool(ChangeLedger ledger, WorkspaceOptions? options = null) : base(options)
        {
            this.ledger = ledger;
        }

        public override ToolDefinition Definition { get; } = Def("create_file",
            "Create a new UTF-8 text file in the workspace. Fails if the file already exists; use apply_patch to modify an existing file.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"path\":{\"type\":\"string\"}," +
            "\"content\":{\"type\":\"string\"}}," +
            "\"required\":[\"path\",\"content\"],\"additionalProperties\":false}");

        protected override Task<ToolOutcome> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
        {
            var access = new WorkspaceAccess(context.Workspace, Options);
            string path = String(args, "path", "");
            string content = String(args, "content", "");
            if (path.Length == 0) return Task.FromResult(Fail("path is required."));
            string full = access.Resolve(path);
            access.EnsureWritable(full, allowExisting: false);

            byte[] bytes = new UTF8Encoding(false).GetBytes(content);
            if (bytes.Length > Options.MaxFileBytes) return Task.FromResult(Fail("Content exceeds the file size limit."));

            // Temp file in the same directory then atomic replace; never a partial target.
            string tmp = full + ".mh-tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, full, overwrite: false);
            }
            finally { TryDelete(tmp); }

            string hash = WorkspaceAccess.HashBytes(bytes);
            ledger.RecordAdded(context.TaskId, access.GetRelative(full), hash, context);
            return Task.FromResult(Ok(ToJson(new { path = access.GetRelative(full), hash, status = "added", bytes = bytes.Length })));
        }

        internal static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }
    }

    // apply_patch(path, expected_hash, old_text, new_text)
    // Unique literal match; zero or multiple matches are rejected with no write.
    public sealed class ApplyPatchTool : CodingToolBase
    {
        private readonly ChangeLedger ledger;
        public ApplyPatchTool(ChangeLedger ledger, WorkspaceOptions? options = null) : base(options)
        {
            this.ledger = ledger;
        }

        public override ToolDefinition Definition { get; } = Def("apply_patch",
            "Replace one exact text span in an existing file. Supply the hash returned by read_file as expected_hash. The old_text must match exactly once; ambiguous or stale patches are rejected without changing the file.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"path\":{\"type\":\"string\"}," +
            "\"expected_hash\":{\"type\":\"string\"}," +
            "\"old_text\":{\"type\":\"string\"}," +
            "\"new_text\":{\"type\":\"string\"}}," +
            "\"required\":[\"path\",\"expected_hash\",\"old_text\",\"new_text\"],\"additionalProperties\":false}");

        protected override Task<ToolOutcome> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
        {
            var access = new WorkspaceAccess(context.Workspace, Options);
            string path = String(args, "path", "");
            string expectedHash = String(args, "expected_hash", "");
            string oldText = String(args, "old_text", "");
            string newText = String(args, "new_text", "");
            if (path.Length == 0 || expectedHash.Length == 0) return Task.FromResult(Fail("path and expected_hash are required."));

            string full = access.Resolve(path);
            access.EnsureWritable(full, allowExisting: true);
            if (!File.Exists(full)) return Task.FromResult(Fail("File does not exist; use create_file: " + path));

            // Re-read immediately before writing; never trust the registration snapshot.
            var current = access.ReadText(full);
            if (!string.Equals(current.Hash, expectedHash, StringComparison.Ordinal))
                return Task.FromResult(Fail("File changed since it was read (hash mismatch). Re-read the file and rebuild the patch. current_hash=" + current.Hash));

            int count = CountOccurrences(current.Text, oldText);
            if (count == 0)
                return Task.FromResult(Fail("old_text not found in the file. No change was made. Re-read the exact lines and retry."));
            if (count > 1)
                return Task.FromResult(Fail("old_text matches " + count + " locations; include more surrounding context so it is unique. No change was made."));

            string replacement = NormalizeToFileEnding(newText, current.Text);
            string updated = current.Text.Replace(oldText, replacement);
            byte[] newBytes = Reencode(current, updated);
            if (newBytes.Length > Options.MaxFileBytes) return Task.FromResult(Fail("Resulting file exceeds the size limit; no change was made."));

            // Snapshot the pre-edit bytes (first-touch baseline) BEFORE replacing.
            string baselineHash = ledger.EnsureBaseline(context.TaskId, access.GetRelative(full), current.Bytes);

            string tmp = full + ".mh-tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(tmp, newBytes);
                File.Move(tmp, full, overwrite: true);
            }
            catch (IOException)
            {
                TryDelete(tmp);
                // Destination may or may not have been replaced: signal an unknown
                // effect so the runtime asks for reconciliation rather than retrying.
                return Task.FromResult(Uncertain("Write could not be confirmed; verify the file state before retrying."));
            }
            finally { TryDelete(tmp); }

            string afterHash = WorkspaceAccess.HashBytes(newBytes);
            ledger.RecordModified(context.TaskId, access.GetRelative(full), baselineHash, afterHash, context);
            return Task.FromResult(Ok(ToJson(new
            {
                path = access.GetRelative(full),
                beforeHash = current.Hash,
                afterHash,
                status = "modified"
            })));
        }

        private static ToolOutcome Uncertain(string message) => new ToolOutcome
        {
            Status = ToolCallStatus.Failed, Output = message, Effect = EffectState.Unknown
        };

        private static int CountOccurrences(string text, string value)
        {
            if (value.Length == 0) return 0;
            int count = 0, index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
            return count;
        }

        private static string NormalizeToFileEnding(string inserted, string fileText)
        {
            int crlf = Count(fileText, "\r\n"), lf = Count(fileText, "\n");
            if (crlf >= lf) return inserted.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return inserted.Replace("\r\n", "\n");
        }

        private static int Count(string text, string value)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
            return count;
        }

        private static byte[] Reencode(WorkspaceAccess.TextFile original, string updated)
        {
            var encoding = new UTF8Encoding(false);
            byte[] body = encoding.GetBytes(updated);
            if (!original.HasBom) return body;
            var withBom = new byte[body.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
            return withBom;
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }
    }

    // get_changes() — agent-owned changes from the ledger only, never `git diff`.
    public sealed class GetChangesTool : CodingToolBase
    {
        private readonly ChangeLedger ledger;
        public GetChangesTool(ChangeLedger ledger, WorkspaceOptions? options = null) : base(options)
        {
            this.ledger = ledger;
        }

        public override ToolDefinition Definition { get; } = Def("get_changes",
            "List the changes made by this agent task, with before/after hashes. Excludes pre-existing user edits; no version control operation is performed.",
            "{\"type\":\"object\",\"properties\":{}," +
            "\"additionalProperties\":false}");

        protected override Task<ToolOutcome> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
        {
            var changes = ledger.List(context.TaskId)
                .Select(c => new { path = c.Path, status = c.Status, beforeHash = c.BeforeHash, afterHash = c.AfterHash, operationId = c.OperationId })
                .ToList();
            return Task.FromResult(Ok(ToJson(new { taskId = context.TaskId, changes })));
        }
    }
}
