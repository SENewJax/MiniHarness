using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Coding;

namespace MiniHarness.Tests
{
    internal static partial class CodingStageTests
    {
        private static async Task LedgerPublicationScenario()
        {
            var ws = Setup(WorkspaceGrants.Read, WorkspaceGrants.Write);
            try
            {
                string file = Path.Combine(ws.Root, "repeat.txt");
                File.WriteAllText(file, "value=0");
                for (int index = 0; index < 200; index++)
                {
                    string before = "value=" + index;
                    var outcome = await new ApplyPatchTool(ws.Ledger).ExecuteAsync(Args(JsonSerializer.Serialize(new
                    {
                        path = "repeat.txt", expected_hash = WorkspaceAccess.HashBytes(File.ReadAllBytes(file)),
                        old_text = before, new_text = "value=" + (index + 1)
                    })), Context(ws), default);
                    Check.True(outcome.Status == ToolCallStatus.Succeeded && outcome.Effect == EffectState.Confirmed, outcome.Output);
                    Check.Equal(WorkspaceAccess.HashBytes(File.ReadAllBytes(file)), ws.Ledger.List(Context(ws).TaskId).Single().AfterHash);
                }
            }
            catch
            {
                string diagnostics = Path.Combine(ws.State, "diagnostics");
                if (Directory.Exists(diagnostics))
                {
                    string output = Path.Combine(MiniHarness.Testing.ValidationPaths.Root, "ledger-diagnostics");
                    Directory.CreateDirectory(output);
                    foreach (string path in Directory.GetFiles(diagnostics, "*.json")) File.Copy(path, Path.Combine(output, Path.GetFileName(path)), true);
                    Console.WriteLine("Ledger failure diagnostics retained: " + output);
                }
                throw;
            }
            finally { Cleanup(ws); }
        }
        private static async Task LedgerNativeFailureScenario()
        {
            if (!OperatingSystem.IsWindows()) throw new TestSkippedException("Windows publication-sharing regression.");
            var ws = Setup(WorkspaceGrants.Read, WorkspaceGrants.Write);
            try
            {
                string file = Path.Combine(ws.Root, "locked.txt");
                File.WriteAllText(file, "old");
                var context = Context(ws);
                ws.Ledger.EnsureBaseline(context.TaskId, "locked.txt", File.ReadAllBytes(file));
                string path = Path.Combine(ws.State, context.TaskId, "changes.json");
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var outcome = await new ApplyPatchTool(ws.Ledger).ExecuteAsync(Args(JsonSerializer.Serialize(new
                    {
                        path = "locked.txt", expected_hash = WorkspaceAccess.HashBytes(File.ReadAllBytes(file)), old_text = "old", new_text = "new"
                    })), context, default);
                    Check.Equal(ToolCallStatus.Failed, outcome.Status); Check.Equal(EffectState.Unknown, outcome.Effect);
                    Check.Equal("new", File.ReadAllText(file));
                }
                string report = Directory.GetFiles(Path.Combine(ws.State, "diagnostics"), "*.json").Single();
                using var json = JsonDocument.Parse(File.ReadAllText(report));
                var error = json.RootElement;
                Check.Equal("publish_move_overwrite", error.GetProperty("operation").GetString());
                Check.Equal(path, error.GetProperty("path").GetString());
                Check.True(error.GetProperty("nativeError").GetInt32() > 0);
                Check.True(error.GetProperty("exception").GetString()!.Contains("ChangeLedger.Save", StringComparison.Ordinal));
                Check.Equal(0, ws.Ledger.List(context.TaskId).Count);
                string retained = Path.Combine(MiniHarness.Testing.ValidationPaths.Root, "ledger-diagnostics");
                Directory.CreateDirectory(retained); File.Copy(report, Path.Combine(retained, Path.GetFileName(report)), true);
            }
            finally { Cleanup(ws); }
        }
        private static async Task LedgerTransientFailureScenario()
        {
            if (!OperatingSystem.IsWindows()) throw new TestSkippedException("Windows publication-sharing regression.");
            var ws = Setup(WorkspaceGrants.Read, WorkspaceGrants.Write);
            Thread? releaser = null;
            FileStream? held = null;
            try
            {
                string file = Path.Combine(ws.Root, "transient.txt"); File.WriteAllText(file, "old");
                var context = Context(ws);
                ws.Ledger.EnsureBaseline(context.TaskId, "transient.txt", File.ReadAllBytes(file));
                string directory = Path.Combine(ws.State, context.TaskId);
                held = new FileStream(Path.Combine(directory, "changes.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
                var locked = held;
                releaser = new Thread(() =>
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (Directory.GetFiles(directory, "*.tmp").Length == 0 && clock.ElapsedMilliseconds < 5000) Thread.Sleep(1);
                    Thread.Sleep(40); locked.Dispose();
                });
                releaser.Start();
                var outcome = await new ApplyPatchTool(ws.Ledger).ExecuteAsync(Args(JsonSerializer.Serialize(new
                {
                    path = "transient.txt", expected_hash = WorkspaceAccess.HashBytes(File.ReadAllBytes(file)), old_text = "old", new_text = "new"
                })), context, default);
                releaser.Join();
                Check.Equal(ToolCallStatus.Succeeded, outcome.Status); Check.Equal(EffectState.Confirmed, outcome.Effect);
                Check.Equal("new", File.ReadAllText(file)); Check.Equal(1, ws.Ledger.List(context.TaskId).Count);
                using var report = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(ws.State, "diagnostics"), "*.json").Single()));
                Check.True(report.RootElement.GetProperty("recovered").GetBoolean());
                Check.True(report.RootElement.GetProperty("attempts").GetInt32() > 1);
            }
            finally { releaser?.Join(); held?.Dispose(); Cleanup(ws); }
        }
    }
}
