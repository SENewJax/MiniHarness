using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Coding;
using MiniHarness.Harness;

namespace MiniHarness.Tests
{
    internal static class CodingStageTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v1-workspace] bounded read/search/list with hashes and skip rules", LocationScenario));
            tests.Add(("[v1-workspace] boundary, link, grant, binary and size rejections have no effect", BoundaryScenario));
            tests.Add(("[v1-mutation] create refuses overwrite and ledger records agent-only changes", CreateAndLedgerScenario));
            tests.Add(("[v1-mutation] stale hash and ambiguous patch are rejected without mutation", PatchGuardScenario));
            tests.Add(("[v1-mutation] unique patch replaces once, keeps CRLF and records before/after", PatchSuccessScenario));
            tests.Add(("[v1-tool] per-tool timeout override escapes the default ceiling", ToolTimeoutScenario));
        }

        private sealed record Workspace(string Root, string State, WorkspaceScope Scope, ChangeLedger Ledger, string Dir);

        private static Workspace Setup(params string[] grants)
        {
            string dir = Path.Combine(Path.GetTempPath(), "miniharness-v1-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(dir, "workspace");
            string state = Path.Combine(dir, "state");
            Directory.CreateDirectory(root); Directory.CreateDirectory(state);
            var scope = new WorkspaceScope { RootPath = root, Grants = grants.ToList(), Revision = "unmanaged" };
            return new Workspace(root, state, scope, new ChangeLedger(state), dir);
        }

        // Remove the workspace without following junctions (a recursive delete on
        // a link would try to remove the linked target's contents).
        private static void Cleanup(Workspace ws)
        {
            if (!Directory.Exists(ws.Dir)) return;
            foreach (string entry in Directory.GetFileSystemEntries(ws.Root))
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    try { Directory.Delete(entry, false); } catch (IOException) { try { File.Delete(entry); } catch { } }
                }
            try { Directory.Delete(ws.Dir, true); } catch (IOException) { }
        }

        private static ToolExecutionContext Context(Workspace ws) => new ToolExecutionContext
        {
            TaskId = "task_demo", TurnId = "turn_demo", StepId = "step_demo", OperationId = "op_demo",
            Workspace = ws.Scope, Capabilities = new CapabilitySnapshot(), Approval = ApprovalState.Approved
        };

        private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

        private static async Task LocationScenario()
        {
            var ws = Setup(WorkspaceGrants.Read);
            try
            {
                File.WriteAllText(Path.Combine(ws.Root, "Program.cs"), "line one\nline two\nline one\n");
                Directory.CreateDirectory(Path.Combine(ws.Root, "src"));
                File.WriteAllText(Path.Combine(ws.Root, "src", "Helper.cs"), "needle found here");
                Directory.CreateDirectory(Path.Combine(ws.Root, "bin"));
                File.WriteAllText(Path.Combine(ws.Root, "bin", "Ignored.dll"), "needle");

                var read = await new ReadFileTool().ExecuteAsync(Args("{\"path\":\"Program.cs\",\"start_line\":2,\"line_count\":1}"), Context(ws), default);
                Check.Equal(ToolCallStatus.Succeeded, read.Status);
                using var doc = JsonDocument.Parse(read.Output);
                Check.Equal(2, doc.RootElement.GetProperty("endLine").GetInt32());
                Check.True(doc.RootElement.GetProperty("content").GetString()!.Contains("line two", StringComparison.Ordinal));
                string hash = doc.RootElement.GetProperty("hash").GetString()!;
                Check.True(hash.Length == 64);

                var search = await new SearchTextTool().ExecuteAsync(Args("{\"query\":\"needle\"}"), Context(ws), default);
                Check.Equal(ToolCallStatus.Succeeded, search.Status);
                using var sdoc = JsonDocument.Parse(search.Output);
                var paths = sdoc.RootElement.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("path").GetString()).ToList();
                Check.True(paths.Count == 1 && paths[0] == "src/Helper.cs", "build output must be skipped by search");

                var list = await new ListFilesTool().ExecuteAsync(Args("{\"directory\":\".\"}"), Context(ws), default);
                Check.Equal(ToolCallStatus.Succeeded, list.Status);
                using var ldoc = JsonDocument.Parse(list.Output);
                var files = ldoc.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
                Check.True(files.Contains("Program.cs") && files.Contains("src/Helper.cs") && !files.Any(f => f!.Contains("bin/")), "list must skip bin");
            }
            finally { if (Directory.Exists(ws.Dir)) Directory.Delete(ws.Dir, true); }
        }

        private static async Task BoundaryScenario()
        {
            var ws = Setup(WorkspaceGrants.Read);
            try
            {
                File.WriteAllText(Path.Combine(ws.Root, "ok.txt"), "ok");
                string outside = Path.Combine(Path.GetTempPath(), "mh-outside-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(outside, "secret");
                try
                {
                    var traversal = await new ReadFileTool().ExecuteAsync(Args("{\"path\":\"../../" + Path.GetFileName(outside) + "\"}"), Context(ws), default);
                    Check.Equal(ToolCallStatus.Failed, traversal.Status); Check.Equal(EffectState.None, traversal.Effect);

                    var absolute = await new ReadFileTool().ExecuteAsync(
                        Args(JsonSerializer.Serialize(new { path = outside })), Context(ws), default);
                    Check.Equal(ToolCallStatus.Failed, absolute.Status);

                    // read grant must not permit writes.
                    var create = await new CreateFileTool(ws.Ledger).ExecuteAsync(
                        Args(JsonSerializer.Serialize(new { path = "x.txt", content = "x" })), Context(ws), default);
                    Check.Equal(ToolCallStatus.Failed, create.Status); Check.True(!File.Exists(Path.Combine(ws.Root, "x.txt")));

                    // binary file is refused as text with no partial read.
                    File.WriteAllBytes(Path.Combine(ws.Root, "bin.dat"), new byte[] { 1, 2, 0, 4, 5 });
                    var binary = await new ReadFileTool().ExecuteAsync(Args("{\"path\":\"bin.dat\"}"), Context(ws), default);
                    Check.Equal(ToolCallStatus.Failed, binary.Status);

                    // directory junction that escapes the root is rejected.
                    string link = Path.Combine(ws.Root, "escape-link");
                    if (TryCreateJunction(link, Path.GetTempPath()))
                    {
                        var linked = await new ListFilesTool().ExecuteAsync(Args("{\"directory\":\"escape-link\"}"), Context(ws), default);
                        Check.Equal(ToolCallStatus.Failed, linked.Status);
                    }
                }
                finally { if (File.Exists(outside)) File.Delete(outside); }
            }
            finally { Cleanup(ws); }
        }

        private static bool TryCreateJunction(string link, string target)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd", "/c mklink /J \"" + link + "\" \"" + target + "\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit(5000);
                return p.ExitCode == 0 && Directory.Exists(link);
            }
            catch { return false; }
        }

        private static async Task CreateAndLedgerScenario()
        {
            var ws = Setup(WorkspaceGrants.Read, WorkspaceGrants.Write);
            try
            {
                // A pre-existing user edit must NOT be attributed to the agent.
                File.WriteAllText(Path.Combine(ws.Root, "user.txt"), "user change");

                var create = await new CreateFileTool(ws.Ledger).ExecuteAsync(
                    Args(JsonSerializer.Serialize(new { path = "agent.txt", content = "first" })), Context(ws), default);
                Check.Equal(ToolCallStatus.Succeeded, create.Status);
                var second = await new CreateFileTool(ws.Ledger).ExecuteAsync(
                    Args(JsonSerializer.Serialize(new { path = "agent.txt", content = "again" })), Context(ws), default);
                Check.Equal(ToolCallStatus.Failed, second.Status);
                Check.Equal("first", File.ReadAllText(Path.Combine(ws.Root, "agent.txt")));

                var changes = await new GetChangesTool(ws.Ledger).ExecuteAsync(Args("{}"), Context(ws), default);
                using var doc = JsonDocument.Parse(changes.Output);
                var paths = doc.RootElement.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString()).ToList();
                Check.True(paths.Count == 1 && paths[0] == "agent.txt", "get_changes must list only agent files");
            }
            finally { if (Directory.Exists(ws.Dir)) Directory.Delete(ws.Dir, true); }
        }

        private static async Task PatchGuardScenario()
        {
            var ws = Setup(WorkspaceGrants.Read, WorkspaceGrants.Write);
            try
            {
                string file = Path.Combine(ws.Root, "dup.txt");
                File.WriteAllText(file, "dup\ndup\n");
                var read = await new ReadFileTool().ExecuteAsync(Args("{\"path\":\"dup.txt\"}"), Context(ws), default);
                string hash = JsonDocument.Parse(read.Output).RootElement.GetProperty("hash").GetString()!;

                // External modification after read makes the supplied hash stale.
                File.WriteAllText(file, "externally changed\n");
                var stale = await new ApplyPatchTool(ws.Ledger).ExecuteAsync(
                    Args(JsonSerializer.Serialize(new { path = "dup.txt", expected_hash = hash, old_text = "dup", new_text = "x" })), Context(ws), default);
                Check.Equal(ToolCallStatus.Failed, stale.Status);
                Check.Equal("externally changed\n", File.ReadAllText(file));

                File.WriteAllText(file, "dup\ndup\n");
                var ambiguous = await new ApplyPatchTool(ws.Ledger).ExecuteAsync(
                    Args(JsonSerializer.Serialize(new { path = "dup.txt", expected_hash = WorkspaceAccess.HashBytes(File.ReadAllBytes(file)), old_text = "dup", new_text = "x" })), Context(ws), default);
                Check.Equal(ToolCallStatus.Failed, ambiguous.Status);
                Check.True(File.ReadAllText(file) == "dup\ndup\n", "ambiguous patch must leave the file untouched");
            }
            finally { if (Directory.Exists(ws.Dir)) Directory.Delete(ws.Dir, true); }
        }

        private static async Task PatchSuccessScenario()
        {
            var ws = Setup(WorkspaceGrants.Read, WorkspaceGrants.Write);
            try
            {
                string file = Path.Combine(ws.Root, "code.cs");
                File.WriteAllBytes(file, new UTF8Encoding(false).GetBytes("class A {\r\n    int X = 1;\r\n}\r\n"));
                byte[] before = File.ReadAllBytes(file);
                string hash = WorkspaceAccess.HashBytes(before);

                var patched = await new ApplyPatchTool(ws.Ledger).ExecuteAsync(
                    Args(JsonSerializer.Serialize(new { path = "code.cs", expected_hash = hash, old_text = "int X = 1;", new_text = "int X = 2;" })), Context(ws), default);
                Check.Equal(ToolCallStatus.Succeeded, patched.Status);
                string text = File.ReadAllText(file);
                Check.True(text.Contains("int X = 2;", StringComparison.Ordinal));
                string bare = text.Replace("\r\n", "");
                Check.True(!bare.Contains('\n'), "no bare LF: CRLF preserved");

                var changes = await new GetChangesTool(ws.Ledger).ExecuteAsync(Args("{}"), Context(ws), default);
                using var doc = JsonDocument.Parse(changes.Output);
                var record = doc.RootElement.GetProperty("changes").EnumerateArray().Single();
                Check.Equal(hash, record.GetProperty("beforeHash").GetString());
                Check.Equal(WorkspaceAccess.HashBytes(File.ReadAllBytes(file)), record.GetProperty("afterHash").GetString());
            }
            finally { if (Directory.Exists(ws.Dir)) Directory.Delete(ws.Dir, true); }
        }

        private static async Task ToolTimeoutScenario()
        {
            var options = new HarnessOptions { ToolTimeoutSeconds = 1 };
            var executor = new ToolExecutor(options);
            var slow = new SlowCustomTimeoutTool();
            var call = new ToolCall { Id = "call_1", Name = "slow", Arguments = "{}" };
            var result = await executor.ExecuteAsync(slow, call, CancellationToken.None);
            Check.Equal(ToolCallStatus.Succeeded, result.Status);
        }

        private sealed class SlowCustomTimeoutTool : ITool, IToolTimeout
        {
            public int TimeoutSeconds => 8;
            public ToolDefinition Definition { get; }
            public SlowCustomTimeoutTool()
            {
                using var json = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
                Definition = new ToolDefinition { Name = "slow", Parameters = json.RootElement.Clone() };
            }
            public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
            {
                await Task.Delay(2500, cancellationToken);
                return "survived";
            }
        }
    }
}
