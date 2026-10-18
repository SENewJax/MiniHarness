using System;
using System.Collections.Generic;
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

            // Refuse the write while bookkeeping is unreadable: Effect.None is then
            // truthful because the workspace is provably untouched.
            try { ledger.EnsureReadable(context.TaskId); }
            catch (WorkspaceAccessException ex) { return Task.FromResult(Fail(ex.Message)); }

            // Temp file in the same directory then atomic replace; never a partial target.
            string tmp = full + ".mh-tmp-" + Guid.NewGuid().ToString("N");
            string rel = access.GetRelative(full);
            string hash = WorkspaceAccess.HashBytes(bytes);
            bool moved = false;
            try
            {
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, full, overwrite: false);
                moved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The move reported failure: verify what actually reached disk instead
                // of assuming "no effect". Only a confirmed absence is Effect.None.
                string disk = ReadHashOrEmpty(full);
                if (disk.Length == 0) return Task.FromResult(Fail("Could not create file and it is absent on disk: " + ex.GetType().Name + ": " + ex.Message));
                if (disk == hash) return Task.FromResult(RecordAddedOrUncertain(ledger, context, full, rel, hash, bytes.Length));
                return Task.FromResult(Uncertain("Create failed and the existing file differs from both the attempt and the expected absence; verify state. " + ex.Message));
            }
            finally { if (!moved) TryDelete(tmp); }

            return Task.FromResult(RecordAddedOrUncertain(ledger, context, full, rel, hash, bytes.Length));
        }

        // Confirm the freshly written bytes, then record them. A ledger failure must
        // surface as an unrecorded-but-confirmed effect (Unknown), never Effect.None.
        private static ToolOutcome RecordAddedOrUncertain(ChangeLedger ledger, ToolExecutionContext context,
            string full, string rel, string hash, int length)
        {
            string disk = ReadHashOrEmpty(full);
            if (disk != hash)
                return Uncertain("Write result could not be confirmed on disk for " + rel + "; verify before retrying.");
            try
            {
                ledger.RecordAdded(context.TaskId, rel, hash, context);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LedgerCorruptException)
            {
                return WrittenButUnrecorded(rel, hash, ex.Message);
            }
            return Ok(ToJson(new { path = rel, hash, status = "added", bytes = length }), EffectState.Confirmed);
        }

        // Hash of an existing file, or "" when it is absent/unreadable. Used to tell
        // a confirmed write apart from a confirmed absence after a failed move.
        internal static string ReadHashOrEmpty(string full)
        {
            try { return File.Exists(full) ? WorkspaceAccess.HashBytes(File.ReadAllBytes(full)) : ""; }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
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

            // Refuse while bookkeeping is unreadable, before any snapshot or write:
            // a corrupt ledger must not surface as "no effect" after a real edit.
            try { ledger.EnsureReadable(context.TaskId); }
            catch (WorkspaceAccessException ex) { return Task.FromResult(Fail(ex.Message)); }

            // Snapshot the pre-edit bytes (first-touch baseline) BEFORE replacing.
            string baselineHash = ledger.EnsureBaseline(context.TaskId, access.GetRelative(full), current.Bytes);

            string rel = access.GetRelative(full);
            string afterHash = WorkspaceAccess.HashBytes(newBytes);
            string tmp = full + ".mh-tmp-" + Guid.NewGuid().ToString("N");
            bool moved = false;
            try
            {
                File.WriteAllBytes(tmp, newBytes);
                File.Move(tmp, full, overwrite: true);
                moved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(tmp);
                // The move reported failure: inspect the destination instead of
                // guessing. New content present => confirmed change; old content
                // intact => no effect; anything else => unknown, reconcile.
                string disk = CreateFileTool.ReadHashOrEmpty(full);
                if (disk == afterHash)
                    return Task.FromResult(RecordModifiedOrUncertain(ledger, context, full, rel, current.Hash, baselineHash, afterHash));
                if (disk == current.Hash)
                    return Task.FromResult(Fail("Patch was not applied; the file is unchanged on disk: " + ex.Message));
                return Task.FromResult(Uncertain("Write could not be confirmed; the destination is neither the old nor the new content. Verify the file state before retrying. " + ex.Message));
            }
            finally { if (!moved) TryDelete(tmp); }

            return Task.FromResult(RecordModifiedOrUncertain(ledger, context, full, rel, current.Hash, baselineHash, afterHash));
        }

        // Confirm the patched bytes on disk, then record them. A ledger failure after
        // a confirmed write is Unknown (real change, missing record), not Effect.None.
        private static ToolOutcome RecordModifiedOrUncertain(ChangeLedger ledger, ToolExecutionContext context,
            string full, string rel, string readHash, string baselineHash, string afterHash)
        {
            string disk = CreateFileTool.ReadHashOrEmpty(full);
            if (disk != afterHash)
                return Uncertain("Write result could not be confirmed on disk for " + rel + "; verify before retrying.");
            try
            {
                ledger.RecordModified(context.TaskId, rel, baselineHash, afterHash, context);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LedgerCorruptException)
            {
                return WrittenButUnrecorded(rel, afterHash, ex.Message);
            }
            return Ok(ToJson(new { path = rel, beforeHash = readHash, afterHash, status = "modified" }), EffectState.Confirmed);
        }

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

        private const int MaxDiffCharsPerFile = 8_000;

        public override ToolDefinition Definition { get; } = Def("get_changes",
            "List the changes made by this agent task with before/after hashes and a readable unified diff. Excludes pre-existing user edits; no version control operation is performed.",
            "{\"type\":\"object\",\"properties\":{}," +
            "\"additionalProperties\":false}");

        protected override Task<ToolOutcome> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
        {
            var access = new WorkspaceAccess(context.Workspace, Options);
            var changes = new List<object>();
            foreach (var c in ledger.List(context.TaskId))
            {
                string before = BaselineText(context.TaskId, c);
                string after = CurrentTextOrEmpty(access, c.Path, out bool present, out string currentHash);
                bool drifted = present && currentHash != c.AfterHash;
                string diff = present
                    ? DiffFormatter.Unified(c.Path, before, after, MaxDiffCharsPerFile)
                    : "--- a/" + c.Path + "\n+++ /dev/null\n(current file is absent; it existed at the recorded change)\n";

                changes.Add(new
                {
                    path = c.Path,
                    status = c.Status,
                    beforeHash = c.BeforeHash,
                    afterHash = c.AfterHash,
                    operationId = c.OperationId,
                    currentHash,
                    currentFilePresent = present,
                    // True when the on-disk file changed again after the agent's recorded edit.
                    currentDiffersFromRecord = drifted,
                    diff
                });
            }
            return Task.FromResult(Ok(ToJson(new { taskId = context.TaskId, changes })));
        }

        // First-touch baseline bytes live outside the workspace; decode as UTF-8.
        // Added files have an empty baseline.
        private string BaselineText(string taskId, ChangeRecord c)
        {
            if (c.BeforeHash.Length == 0) return "";
            byte[]? bytes = ledger.BaselineBytes(taskId, c.BeforeHash);
            if (bytes == null || bytes.Length == 0) return "";
            try
            {
                int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException) { return "<baseline is not valid UTF-8 text>"; }
        }

        private static string CurrentTextOrEmpty(WorkspaceAccess access, string relative, out bool present, out string currentHash)
        {
            present = false; currentHash = "";
            try
            {
                string full = access.Resolve(relative);
                if (!File.Exists(full)) return "";
                var text = access.ReadText(full);
                present = true; currentHash = text.Hash;
                return text.Text;
            }
            catch (WorkspaceAccessException) { return ""; }
        }
    }
}
