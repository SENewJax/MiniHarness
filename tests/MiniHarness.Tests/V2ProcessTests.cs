using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Coding;
using MiniHarness.Coding.Processes;
using MiniHarness.ProcessFixture;

namespace MiniHarness.Tests
{
    internal static class V2ProcessTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v2-session] owned startup, stdin idempotence, EOF and lifetime", Sessions));
            tests.Add(("[v2-output] bounded Unicode stdout/stderr, cursor replay and gaps", Output));
            tests.Add(("[v2-output] concurrent journal/output readers coexist with atomic publication and cleanup", ConcurrentPublication));
            tests.Add(("[v2-recovery] job owns early orphan, shutdown and old-generation reconciliation", Recovery));
            tests.Add(("[v2-recovery] startup intent and uncertain stdin are never replayed; quota remains bounded", FaultWindows));
            tests.Add(("[v2-recovery] hard-killed owner closes job; restarted manager never attaches old PIDs", HardKillOwner));
        }
        internal static ProcessProfile Profile(string name, int lifetime = 30) => new ProcessProfile
        { Name = name, FileName = "dotnet", Arguments = new[] { typeof(FixtureMarker).Assembly.Location, name }, AllowStdin = name == "echo", LifetimeSeconds = lifetime };
        internal static ToolExecutionContext Context(string root, string operation = "op_start", string task = "task_test") => new ToolExecutionContext
        { TaskId = task, TurnId = "turn_test", OperationId = operation, Workspace = new WorkspaceScope { RootPath = root, Grants = { "read", "write", "execute" } } };
        private static async Task<ResourceReference> Until(ProcessSessionManager manager, ResourceReference resource, Func<ResourceReference, bool> condition)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (true)
            {
                var current = await manager.ExecuteAsync(resource, ResourceAction.Query, limit.Token);
                if (condition(current)) return current;
                await Task.Delay(20, limit.Token);
            }
        }
        private static async Task<string> Ready(ProcessSessionManager manager, ResourceReference resource, string marker)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var page = await manager.ReadOutputAsync(resource, 0, 8192, limit.Token);
                string text = string.Concat(page.Chunks.Select(c => c.Text));
                if (text.Contains(marker, StringComparison.Ordinal)) return text;
                await Task.Delay(20, limit.Token);
            }
        }
        private static string Root() { if (!OperatingSystem.IsWindows()) throw new TestSkippedException("V2 owned process platform unavailable.");
            string path = Path.Combine(Path.GetTempPath(), "miniharness-v2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        private static async Task Sessions()
        {
            string root = Root();
            try
            {
                var options = new ProcessSessionOptions { Directory = Path.Combine(root, "state") };
                var eofProfile = Profile("echo"); eofProfile.Name = "eof"; eofProfile.AllowStdin = false;
                await using var manager = new ProcessSessionManager(options, new[] { Profile("echo"), Profile("hold", 1), eofProfile,
                    new ProcessProfile { Name = "missing", FileName = Path.Combine(root, "missing.exe") } });
                var watch = Stopwatch.StartNew();
                var resource = await manager.StartAsync("echo", Context(root), default);
                Check.Equal("running", resource.State); Check.True(watch.ElapsedMilliseconds < 2000, resource.Result);
                Check.Equal(resource.Id, (await manager.StartAsync("echo", Context(root), default)).Id);
                await Ready(manager, resource, "READY:");
                await Check.ThrowsAsync<ResourceRequestRejectedException>(() => manager.StartAsync("unauthorized", Context(root, "op_denied"), default));
                await Check.ThrowsAsync<ResourceRequestRejectedException>(() => manager.WriteInputAsync(resource, new ResourceInput
                { CommandId = "too-large", ExpectedGeneration = manager.Generation, Text = new string('x', 4097) }, default));
                var input = new ResourceInput { CommandId = "first", ExpectedGeneration = manager.Generation, Text = "hello\n" };
                await manager.WriteInputAsync(resource, input, default); await manager.WriteInputAsync(resource, input, default);
                string output = await Ready(manager, resource, "echo:hello");
                Check.Equal(1, output.Split("echo:hello", StringSplitOptions.None).Length - 1);
                input.Text = "different";
                await Check.ThrowsAsync<ResourceRequestRejectedException>(() => manager.WriteInputAsync(resource, input, default));
                await Check.ThrowsAsync<ResourceRequestRejectedException>(() => manager.WriteInputAsync(resource, new ResourceInput
                { CommandId = "stale", ExpectedGeneration = "old", Text = "bad" }, default));
                await manager.WriteInputAsync(resource, new ResourceInput { CommandId = "eof", ExpectedGeneration = manager.Generation, CloseInput = true }, default);
                var finished = await Until(manager, resource, r => ResourceStates.IsTerminal(r.State));
                Check.Equal("finished", finished.State); Check.Equal(0, finished.Process!.ExitCode); Check.True(finished.Process.TerminationConfirmed);
                var timed = await manager.StartAsync("hold", Context(root, "op_timed"), default);
                timed = await Until(manager, timed, r => ResourceStates.IsTerminal(r.State));
                Check.Equal("failed", timed.State); Check.Equal("lifetime", timed.Process!.StopReason); Check.True(timed.Process.TerminationConfirmed);
                var eof = await manager.StartAsync("eof", Context(root, "op_eof"), default);
                eof = await Until(manager, eof, r => ResourceStates.IsTerminal(r.State));
                Check.Equal(0, eof.Process!.ExitCode); Check.True(eof.Process.InputClosed);
                var missing = await manager.StartAsync("missing", Context(root, "op_missing"), default);
                Check.Equal("failed", missing.State); Check.True(missing.Process!.TerminationConfirmed);
                manager.FaultInjection = phase => { if (phase == "before_job_bind") Thread.Sleep(750); };
                var delayed = await manager.StartAsync("hold", Context(root, "op_delayed"), default);
                delayed = await Until(manager, delayed, r => ResourceStates.IsTerminal(r.State));
                manager.FaultInjection = null;
                Check.True(delayed.Process!.TerminationConfirmed);
                Check.True(DateTimeOffset.UtcNow < delayed.Process.StartedAtUtc!.Value.AddSeconds(1.6), "Lifetime must include suspended startup, not reset after Resume.");
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Output()
        {
            string root = Root();
            try
            {
                var options = new ProcessSessionOptions { Directory = Path.Combine(root, "state"), MaxOutputMemoryBytes = 4096, MaxOutputDiskBytes = 8192, MaxPageBytes = 4096 };
                await using var manager = new ProcessSessionManager(options, new[] { Profile("flood"), Profile("echo") });
                var resource = await manager.StartAsync("flood", Context(root), default);
                resource = await Until(manager, resource, r => ResourceStates.IsTerminal(r.State));
                Check.Equal(7, resource.Process!.ExitCode);
                var page = await manager.ReadOutputAsync(resource, 0, 512, default);
                Check.True(page.Gap && page.Truncated); Check.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(page, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })) <= 512);
                var again = await manager.ReadOutputAsync(resource, 0, 512, default);
                Check.Equal(page.NextCursor, again.NextCursor);
                Check.Equal(string.Concat(page.Chunks.Select(c => c.Text)), string.Concat(again.Chunks.Select(c => c.Text)));
                Check.True(!page.Chunks.Any(c => c.Text.Contains('\uFFFD')));
                Check.True(new FileInfo(Path.Combine(options.Directory, resource.Id + ".output.json")).Length <= options.MaxOutputDiskBytes);
                var foreign = new ResourceReference { Id = resource.Id, Kind = resource.Kind, OwnerTaskId = "other", OwnerOperationId = resource.OwnerOperationId };
                await Check.ThrowsAsync<ResourceRequestRejectedException>(() => manager.ReadOutputAsync(foreign, 0, 128, default));
                var damaged = await manager.StartAsync("echo", Context(root, "op_damaged"), default);
                await Ready(manager, damaged, "READY:");
                string path = Path.Combine(options.Directory, damaged.Id + ".output.json"); File.Delete(path); Directory.CreateDirectory(path);
                await manager.WriteInputAsync(damaged, new ResourceInput { CommandId = "trigger-output", ExpectedGeneration = manager.Generation, Text = "hello\n" }, default);
                damaged = await Until(manager, damaged, r => ResourceStates.IsTerminal(r.State));
                Check.Equal("failed", damaged.State); Check.True(damaged.Process!.OutputDegraded); Check.True(damaged.Process.TerminationConfirmed);
                Check.True(damaged.Process.OutputArtifact == null, "A damaged live log cannot become a complete immutable artifact.");
                var missing = await manager.ReadOutputAsync(damaged, 0, 512, default);
                Check.True(missing.Truncated && missing.Gap);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task ConcurrentPublication()
        {
            string root = Root();
            try
            {
                var options = new ProcessSessionOptions { Directory = Path.Combine(root, "state") };
                await using var owner = new ProcessSessionManager(options, new[] { Profile("echo") });
                await using var reader = new ProcessSessionManager(options, new[] { Profile("echo") });
                var resource = await owner.StartAsync("echo", Context(root), default);
                await Ready(owner, resource, "READY:");
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var reads = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    for (int i = 0; i < 64; i++)
                    {
                        var observed = await reader.ExecuteAsync(resource, ResourceAction.Reconcile, limit.Token);
                        Check.Equal(resource.Id, observed.Id); Check.Equal(resource.OwnerTaskId, observed.OwnerTaskId);
                        var page = await reader.ReadOutputAsync(resource, 0, 512, limit.Token);
                        Check.True(page.NextCursor >= 0); await Task.Yield();
                    }
                })).ToList();
                reads.Add(Task.Run(async () =>
                {
                    await start.Task;
                    for (int i = 0; i < 16; i++)
                        await owner.WriteInputAsync(resource, new ResourceInput { CommandId = "publish-" + i,
                            ExpectedGeneration = owner.Generation, Text = "line-" + i + "\n" }, limit.Token);
                }));
                start.TrySetResult(true); await Task.WhenAll(reads).WaitAsync(limit.Token);
                var stopped = await owner.ExecuteAsync(resource, ResourceAction.Cancel, limit.Token);
                Check.True(stopped.Process!.TerminationConfirmed);
                Check.Equal("cancelled", (await reader.ExecuteAsync(resource, ResourceAction.Reconcile, limit.Token)).State);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Recovery()
        {
            string root = Root(); ResourceReference resource;
            try
            {
                var options = new ProcessSessionOptions { Directory = Path.Combine(root, "state") };
                await using (var manager = new ProcessSessionManager(options, new[] { Profile("orphan"), Profile("hold") }))
                {
                    resource = await manager.StartAsync("orphan", Context(root), default);
                    string output = await Ready(manager, resource, "CHILD:");
                    resource = await manager.ExecuteAsync(resource, ResourceAction.Query, default);
                    Check.Equal("running", resource.State);
                    var cancelled = await manager.ExecuteAsync(resource, ResourceAction.Cancel, default);
                    Check.Equal("cancelled", cancelled.State); Check.True(cancelled.Process!.TerminationConfirmed);
                    resource = await manager.StartAsync("hold", Context(root, "op_shutdown"), default);
                    await Ready(manager, resource, "READY:");
                    // A new host while the original owner is alive must not attach,
                    // kill by PID, restart, or accept stdin into that generation.
                    await using var other = new ProcessSessionManager(options, new[] { Profile("hold") });
                    var unknown = await other.ExecuteAsync(resource, ResourceAction.Reconcile, default);
                    Check.Equal("unknown", unknown.State);
                    Check.Equal("running", (await manager.ExecuteAsync(resource, ResourceAction.Query, default)).State);
                }
                await using var reloaded = new ProcessSessionManager(options, new[] { Profile("hold") });
                var saved = await reloaded.ExecuteAsync(resource, ResourceAction.Reconcile, default);
                Check.Equal("cancelled", saved.State); Check.True(saved.Process!.TerminationConfirmed);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task FaultWindows()
        {
            string root = Root();
            try
            {
                var options = new ProcessSessionOptions { Directory = Path.Combine(root, "state"), MaxSessions = 1, MaxSessionsPerTask = 1 };
                await using var manager = new ProcessSessionManager(options, new[] { Profile("echo"), Profile("hold") });
                manager.FaultInjection = stage => { if (stage == "after_start_intent") throw new IOException("Injected startup ambiguity"); };
                var uncertain = await manager.StartAsync("hold", Context(root), default);
                Check.Equal("unknown", uncertain.State); Check.True(uncertain.Process!.Pid == null);
                manager.FaultInjection = null;
                Check.Equal("unknown", (await manager.StartAsync("hold", Context(root), default)).State);
                await Check.ThrowsAsync<ResourceRequestRejectedException>(() => manager.StartAsync("echo", Context(root, "quota"), default));
                // A separate manager cannot turn an old starting journal into a new OS process.
                await using var later = new ProcessSessionManager(options, new[] { Profile("hold") });
                Check.Equal("unknown", (await later.StartAsync("hold", Context(root), default)).State);
            }
            finally { Directory.Delete(root, true); }
            root = Root();
            try
            {
                await using var manager = new ProcessSessionManager(new ProcessSessionOptions { Directory = Path.Combine(root, "state") }, new[] { Profile("tree") });
                manager.FaultInjection = stage => { if (stage == "before_job_bind") throw new IOException("Injected binding failure"); };
                var rejected = await manager.StartAsync("tree", Context(root), default);
                Check.Equal("failed", rejected.State); Check.True(rejected.Process!.TerminationConfirmed);
                Check.True(rejected.Process.Pid.HasValue);
                bool alive;
                try { using var process = Process.GetProcessById(rejected.Process.Pid!.Value); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
                Check.True(!alive, "The suspended process must be terminated when job binding fails.");
            }
            finally { Directory.Delete(root, true); }
            root = Root();
            try
            {
                await using var manager = new ProcessSessionManager(new ProcessSessionOptions { Directory = Path.Combine(root, "state") }, new[] { Profile("echo") });
                var resource = await manager.StartAsync("echo", Context(root), default);
                await Ready(manager, resource, "READY:");
                manager.FaultInjection = stage => { if (stage == "after_stdin_write") throw new IOException("Injected post-write journal failure"); };
                var input = new ResourceInput { CommandId = "uncertain", ExpectedGeneration = manager.Generation, Text = "once\n" };
                await Check.ThrowsAsync<IOException>(() => manager.WriteInputAsync(resource, input, default));
                Check.Equal("unknown", await manager.ReadInputStatusAsync(resource, input.CommandId, default));
                manager.FaultInjection = null;
                await Check.ThrowsAsync<ResourceRequestRejectedException>(() => manager.WriteInputAsync(resource, input, default));
                var after = await manager.ExecuteAsync(resource, ResourceAction.Reconcile, default);
                Check.True(after.Process!.TerminationConfirmed);
                Check.True(ResourceStates.IsTerminal(after.State));
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task HardKillOwner()
        {
            string root = Root(); Directory.CreateDirectory(Path.Combine(root, "workspace"));
            Process? owner = null;
            try
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(typeof(FixtureMarker).Assembly.Location); start.ArgumentList.Add("owner"); start.ArgumentList.Add(root);
                owner = Process.Start(start)!;
                string ready = Path.Combine(root, "owner-ready.json");
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                while (!File.Exists(ready)) { Check.True(!owner.HasExited, "Owner fixture exited before ready."); await Task.Delay(20, limit.Token); }
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(ready, limit.Token));
                var resource = json.RootElement.GetProperty("Resource").Deserialize<ResourceReference>(MiniHarness.Harness.TaskJson.Options)!;
                int child = int.Parse(Regex.Match(json.RootElement.GetProperty("Output").GetString()!, @"CHILD:(\d+)").Groups[1].Value);
                var pids = new[] { resource.Process!.Pid!.Value, child };
                var identities = pids.Select(pid => { using var p = Process.GetProcessById(pid); return (Pid: pid, Start: p.StartTime); }).ToList();
                owner.Kill(); await owner.WaitForExitAsync(limit.Token); // deliberately NOT Kill(entireProcessTree)
                foreach (var identity in identities)
                {
                    bool alive;
                    do
                    {
                        try { using var process = Process.GetProcessById(identity.Pid); alive = !process.HasExited && process.StartTime == identity.Start; }
                        catch (ArgumentException) { alive = false; }
                        if (alive) await Task.Delay(20, limit.Token);
                    } while (alive);
                }
                await using var restarted = new ProcessSessionManager(new ProcessSessionOptions { Directory = Path.Combine(root, "state") }, new[] { Profile("tree") });
                Check.Equal("unknown", (await restarted.ExecuteAsync(resource, ResourceAction.Reconcile, default)).State);
                Check.Equal(resource.Id, (await restarted.StartAsync("tree", Context(Path.Combine(root, "workspace"), "op_owner", "task_owner"), default)).Id);
            }
            finally
            {
                if (owner != null) { if (!owner.HasExited) { owner.Kill(true); await owner.WaitForExitAsync(); } owner.Dispose(); }
                Directory.Delete(root, true);
            }
        }
    }
}
