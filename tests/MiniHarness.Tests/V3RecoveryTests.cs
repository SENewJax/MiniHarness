using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Testing;

namespace MiniHarness.Tests
{
    internal static class V3RecoveryTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v3-recovery] real host kill/restart classifies ten fault windows and resumes only after trusted reconciliation", RestartMatrix));
            tests.Add(("[v3-recovery] competing recovery tokens advance one logical point once", ResumeRace));
        }
        private static async Task RestartMatrix()
        {
            string root = Path.Combine(ValidationPaths.Root, "v3-restart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var results = new List<object>();
            foreach (string phase in new[] { "preparing", "summary_started", "summary_responded", "summary_before_activation", "model_started", "operation_started", "effect_uncommitted", "operation_committed", "resource_wait_saved", "recovery_recorded" })
            {
                string directory = Path.Combine(root, phase); Directory.CreateDirectory(directory);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                using var first = await StartHost(directory, phase == "recovery_recorded" ? "preparing" : phase, phase == "resource_wait_saved", deadline.Token);
                var command = phase.StartsWith("summary", StringComparison.Ordinal) || phase == "preparing" || phase == "recovery_recorded" ? V3CompactionTests.LongStart() :
                    new TaskCommand { Kind = TaskCommandKind.Start, Request = new ModelRequest { Model = "test",
                        Messages = { new Message { Text = phase == "resource_wait_saved" ? "resource fixture" : "counter fixture" } } } };
                command.Request!.Model = "mini-agent";
                using (var accepted = await first.Client.PostAsJsonAsync("/agent/tasks/commands?wait=false", command, TaskJson.Options, deadline.Token))
                    Check.True(accepted.StatusCode == HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(deadline.Token));
                using (await RecoveryHost.ReadMarkerAsync(Path.Combine(directory, "phase.json"), deadline.Token)) { }
                string id = "task_" + TaskJson.Hash(command.Id).Substring(0, 32);
                var store = new FileTaskStore(Path.Combine(directory, "tasks")); var before = (await store.LoadAsync(id, deadline.Token))!;
                string oldGeneration = before.Recovery.HostGeneration;
                var processIdentities = new List<(int Pid, DateTime Start)>();
                if (phase == "resource_wait_saved")
                {
                    string outputPath = Path.Combine(directory, "processes", before.Resources.Single().Id + ".output.json"); string output;
                    do
                    {
                        using var pageJson = await RecoveryHost.ReadMarkerAsync(outputPath, deadline.Token);
                        output = string.Concat(pageJson.RootElement.Deserialize<ProcessOutputPage>(TaskJson.Options)!.Chunks.Select(c => c.Text));
                        if (System.Text.RegularExpressions.Regex.IsMatch(output, @"CHILD:\d+\r?\n")) break;
                        await Task.Delay(10, deadline.Token);
                    } while (true);
                    int childPid = int.Parse(System.Text.RegularExpressions.Regex.Match(output, @"CHILD:(\d+)").Groups[1].Value);
                    foreach (int pid in new[] { before.Resources.Single().Process!.Pid!.Value, childPid })
                    { using var process = Process.GetProcessById(pid); processIdentities.Add((pid, process.StartTime)); }
                }
                await first.KillAsync(deadline.Token);
                foreach (var identity in processIdentities)
                    while (true)
                    {
                        bool alive;
                        try { using var process = Process.GetProcessById(identity.Pid); alive = !process.HasExited && process.StartTime == identity.Start; }
                        catch (ArgumentException) { alive = false; }
                        if (!alive) break; await Task.Delay(10, deadline.Token);
                    }
                if (phase == "recovery_recorded")
                {
                    using var middle = await StartHost(directory, "recovery_recorded", false, deadline.Token);
                    using (await RecoveryHost.ReadMarkerAsync(Path.Combine(directory, "phase.json"), deadline.Token)) { }
                    await middle.KillAsync(deadline.Token);
                }
                using var next = await StartHost(directory, "none", phase == "resource_wait_saved", deadline.Token);
                // A startup scan may report busy rather than steal a lease.
                // Explicit Recover uses the same runtime gate and is safe to
                // race the scanner; it never calls the model.
                using var recoveryReply = await next.Client.PostAsJsonAsync("/agent/tasks/commands",
                    new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = id }, TaskJson.Options, deadline.Token);
                Check.True(recoveryReply.IsSuccessStatusCode, await recoveryReply.Content.ReadAsStringAsync(deadline.Token));
                var recovered = (await recoveryReply.Content.ReadFromJsonAsync<TaskCommandResult>(TaskJson.Options, deadline.Token))!.State;
                Check.True(recovered.Recovery.HostGeneration != oldGeneration);
                Check.Equal(before.Budget.ModelCalls, recovered.Budget.ModelCalls); Check.Equal(before.Budget.ToolCalls, recovered.Budget.ToolCalls);
                Check.Equal(phase == "effect_uncommitted" || phase == "operation_committed" ? 1 : 0,
                    File.Exists(Path.Combine(directory, "effects.count")) ? int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "effects.count"), deadline.Token)) : 0);
                if (phase == "summary_started" || phase == "model_started")
                {
                    Check.True(!recovered.Budget.Usage.Known); Check.True(!recovered.Recovery.CanResume);
                    Check.Equal(ModelInvocationStatus.Unknown, recovered.ModelInvocations.Single().Status);
                    using var settled = await next.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.ReconcileModelUsage, TaskId = id,
                        ModelUsage = new ModelUsageReconciliation { InvocationId = recovered.ModelInvocations.Single().Id, Basis = "fixture host approved conservative bound",
                            ConservativeUpperBound = true, Usage = new TokenUsage { InputTokens = 3, OutputTokens = 2 } } }, TaskJson.Options, deadline.Token);
                    Check.Equal(HttpStatusCode.OK, settled.StatusCode);
                    if (phase == "summary_started")
                    {
                        // Billing reconciliation does not silently retry a lost
                        // summary. The user explicitly requests a new, charged
                        // compaction when raw history still exceeds capacity.
                        using var compacted = await next.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand
                            { Kind = TaskCommandKind.CompactContext, TaskId = id }, TaskJson.Options, deadline.Token);
                        Check.Equal(HttpStatusCode.OK, compacted.StatusCode);
                    }
                    var ready = (await store.LoadAsync(id, deadline.Token))!; Check.True(ready.Budget.Usage.Known);
                    using var resumed = await next.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = id,
                        RecoveryToken = ready.Recovery.ContinuationToken }, TaskJson.Options, deadline.Token);
                    Check.Equal(HttpStatusCode.OK, resumed.StatusCode);
                    Check.Equal(TurnStatus.Completed, (await store.LoadAsync(id, deadline.Token))!.Turns.Single().Status);
                }
                else if (phase == "operation_started" || phase == "effect_uncommitted")
                {
                    Check.Equal(OperationStatus.Unknown, recovered.Operations.Single().Status); Check.Equal(WaitReason.Reconciliation, recovered.Wait);
                    using var confirmed = await next.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.Reconcile, TaskId = id,
                        OperationId = recovered.Operations.Single().Id, ReconciledOutcome = new ToolOutcome { Effect = phase == "operation_started" ? EffectState.None : EffectState.Confirmed,
                            Status = ToolCallStatus.Succeeded, Output = "1" } }, TaskJson.Options, deadline.Token);
                    Check.Equal(HttpStatusCode.OK, confirmed.StatusCode); Check.Equal(1, int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "effects.count"), deadline.Token)));
                    Check.Equal(TurnStatus.Completed, (await store.LoadAsync(id, deadline.Token))!.Turns.Single().Status);
                }
                else if (phase == "resource_wait_saved")
                {
                    Check.Equal("unknown", recovered.Resources.Single().State); Check.True(!recovered.Recovery.CanWake);
                    Check.Equal(before.ResourceWaits.Single().Id, recovered.ResourceWaits.Single().Id);
                    using var confirmed = await next.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.ReconcileResourceState, TaskId = id,
                        ResourceId = recovered.Resources.Single().Id, ResourceReconciliation = new ResourceReconciliation { ExpectedGeneration = recovered.Resources.Single().Process!.Generation,
                            State = "cancelled", TerminationConfirmed = true, Basis = "Fixture independently verified original root/child identities gone after Job owner death; side effects remain separate." } }, TaskJson.Options, deadline.Token);
                    Check.Equal(HttpStatusCode.OK, confirmed.StatusCode);
                    var ready = (await store.LoadAsync(id, deadline.Token))!; Check.Equal(before.Budget.ModelCalls, ready.Budget.ModelCalls);
                    using var resumed = await next.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.Resume,
                        TaskId = id, RecoveryToken = ready.Recovery.ContinuationToken }, TaskJson.Options, deadline.Token);
                    Check.Equal(HttpStatusCode.OK, resumed.StatusCode); Check.Equal(TurnStatus.Completed, (await store.LoadAsync(id, deadline.Token))!.Turns.Single().Status);
                }
                else
                {
                    if (phase.StartsWith("summary", StringComparison.Ordinal)) Check.True(recovered.ContextMemory.SummaryId != null);
                    Check.True(recovered.Recovery.CanResume);
                    var resume = new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = id, RecoveryToken = recovered.Recovery.ContinuationToken };
                    using var response = await next.Client.PostAsJsonAsync("/agent/tasks/commands", resume, TaskJson.Options, deadline.Token);
                    Check.Equal(HttpStatusCode.OK, response.StatusCode);
                    var done = (await store.LoadAsync(id, deadline.Token))!; Check.Equal(TurnStatus.Completed, done.Turns.Single().Status);
                    if (phase == "operation_committed") Check.Equal(1, int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "effects.count"), deadline.Token)));
                }
                results.Add(new { phase, taskId = id, classification = recovered.Recovery.Classification, recovered.Budget.ModelCalls,
                    recovered.Budget.ToolCalls, known = recovered.Budget.Usage.Known, passed = true });
                await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, TaskJson.Options), deadline.Token);
            }
        }
        private static async Task ResumeRace()
        {
            string root = V3CompactionTests.Root();
            try
            {
                var store = new FileTaskStore(root); var state = new AgentTaskState { Model = V3CompactionTests.Binding() };
                var turn = new TaskTurn { Settings = HarnessTests.Request() }; turn.Settings.Messages.Clear(); state.Turns.Add(turn);
                state.History.Add(new HistoryEntry { TurnId = turn.Id, Message = new Message { Text = "continue" } });
                await using (var lease = await store.AcquireAsync(state.Id, default)) await lease.CommitAsync(state, 0, default);
                var model = new V3CompactionTests.SummaryModel(); await using var runtime = V3CompactionTests.Runtime(model, root);
                var recovered = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = state.Id }, default)).State;
                var commands = Enumerable.Range(0, 6).Select(index => runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume,
                    TaskId = state.Id, Automatic = index % 2 == 0, RecoveryToken = recovered.Recovery.ContinuationToken }, default)).ToArray();
                await Task.WhenAll(commands); Check.Equal(1, model.Calls);
                var done = (await store.LoadAsync(state.Id, default))!; Check.Equal(1, done.Budget.ModelCalls); Check.Equal(TurnStatus.Completed, done.Turns.Single().Status);
            }
            finally { Directory.Delete(root, true); }
        }
        private static Task<RecoveryHost> StartHost(string root, string phase, bool resources, CancellationToken ct)
        {
            var configuration = resources ? new[] { "--Harness:MaxContextCharacters", "30000", "--Processes:Enabled", "true", "--Processes:Limits:Directory", Path.Combine(root, "processes"),
                "--Processes:Profiles:0:Name", "tree", "--Processes:Profiles:0:FileName", "dotnet", "--Processes:Profiles:0:Arguments:0", typeof(MiniHarness.ProcessFixture.FixtureMarker).Assembly.Location,
                "--Processes:Profiles:0:Arguments:1", "tree", "--Processes:Profiles:0:LifetimeSeconds", "60" } : Array.Empty<string>();
            return RecoveryHost.StartAsync(root, phase, true, configuration, ct);
        }
    }
}
