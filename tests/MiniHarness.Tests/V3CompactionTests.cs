using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;

namespace MiniHarness.Tests
{
    internal static class V3CompactionTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v3-compact] repeated bounded compaction preserves raw history, authority and total costs", Compaction));
            tests.Add(("[v3-compact] invalid summary retains cost and raw fallback without hot retry", Invalid));
            tests.Add(("[v3-recovery] saved summary response activates without another paid request", Responded));
            tests.Add(("[v3-budget] cancelled summary retains unknown usage and trusted reconciliation charges once", Cancelled));
            tests.Add(("[v3-recovery] configuration changes block advancement while cleanup remains possible", Binding));
            tests.Add(("[v3-compact] pinned/indivisible overflow sends no illegal request and steering supersedes a summary candidate", BoundariesAndSteering));
            tests.Add(("[v3-budget] background publication failure is visible and stored reasoning response resumes without rebilling", FailedPublication));
            tests.Add(("[v3-budget] exhausted generation budgets still settle a saved final response, never unknown usage", ExhaustedSettlement));
            tests.Add(("[v3-budget] final response resource waits remain wakeable at the model call limit", ExhaustedResourceSettlement));
            tests.Add(("[v3-budget] late child usage reconciliation settles only the delta after collection", ChildUsage));
            tests.Add(("[v3-compact] exhausted explicit maintenance never reopens a completed response", Maintenance));
        }
        internal static ContextOptions ContextConfig() => new ContextOptions { Enabled = true, KeepRecentGroups = 2,
            TriggerRatio = 0.7, TargetRatio = 0.5, MaxSummaryCharacters = 1000, SummaryMaxOutputTokens = 256, MaxSummaryCallsPerTask = 24 };
        internal static HarnessOptions Limits() => new HarnessOptions { MaxContextCharacters = 6500, MaxModelCalls = 30, MaxTotalTokens = 50000 };
        internal static ModelBinding Binding(string version = "same") => new ModelBinding { Model = "test", ConfigurationVersion = version };
        internal static TaskCommand LongStart() => new TaskCommand { Kind = TaskCommandKind.Start, Request = new ModelRequest
        {
            Model = "test", Messages = new List<Message> { new Message { Role = "system", Text = "Never modify protected tests." },
                new Message { Text = "Keep the immutable marker." }, new Message { Role = "assistant", Text = new string('a', 1700) },
                new Message { Text = new string('b', 1700) }, new Message { Role = "assistant", Text = new string('c', 1000) },
                new Message { Text = "Continue" } }
        } };
        internal static string Root() { string path = Path.Combine(Path.GetTempPath(), "miniharness-v3-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        internal static TaskRuntime Runtime(IModelProvider model, string root, IRecoveryAuthorizer? auth = null, string version = "same")
            => new TaskRuntime(model, new FileTaskStore(root), Limits(), Binding(version), contextOptions: ContextConfig(), recoveryAuthorizer: auth);
        private static async Task Compaction()
        {
            string root = Root();
            try
            {
                var model = new SummaryModel(); await using var runtime = Runtime(model, root);
                var start = LongStart(); string originals = JsonSerializer.Serialize(start.Request!.Messages, TaskJson.Options);
                var state = (await runtime.ExecuteAsync(start, default)).State;
                for (int i = 0; i < 3; i++)
                    state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Input, TaskId = state.Id,
                        Request = new ModelRequest { Model = "test", Messages = { new Message { Text = new string((char)('d' + i), 1700) } } } }, default)).State;
                Check.True(state.Compactions.Count(c => c.Status == CompactionStatus.Committed) >= 2);
                Check.Equal(originals, JsonSerializer.Serialize(state.History.Take(start.Request.Messages.Count).Select(h => h.Message), TaskJson.Options));
                Check.Equal(model.Calls, state.Budget.ModelCalls); Check.Equal((long)model.Calls * 5, state.Budget.Usage.TotalTokens);
                Check.Equal(state.Budget.Usage.TotalTokens, state.Turns.Sum(t => t.Usage.TotalTokens));
                Check.True(model.Business.All(r => r.Messages.Any(m => m.Text == "Never modify protected tests.")));
                Check.True(model.Business.All(r => new ContextBuilder(Limits()).Measure(r) <= Limits().MaxContextCharacters));
                Check.Equal(0, state.Evidence.Count); Check.Equal(0, state.Facts.Workspace.Grants.Count);
                var page = await runtime.ReadHistoryAsync(state.Id, null, 65536, default); Check.Equal(state.History.Count, page.Entries.Count);
                string originalHistory = JsonSerializer.Serialize(state.History, TaskJson.Options);
                string damagedId = state.ContextMemory.SummaryId!;
                state.Compactions.Single(c => c.Id == damagedId).Content!.Background += " tampered without matching hash";
                var store = new FileTaskStore(root); await using (var lease = await store.AcquireAsync(state.Id, default)) await lease.CommitAsync(state, state.Revision, default);
                var fresh = new SummaryModel(); await using var rebuilt = Runtime(fresh, root);
                var repaired = (await rebuilt.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Input, TaskId = state.Id,
                    Request = new ModelRequest { Model = "test", Messages = { new Message { Text = "Continue after detecting damaged memory" } } } }, default)).State;
                Check.Equal(CompactionStatus.Failed, repaired.Compactions.Single(c => c.Id == damagedId).Status);
                Check.Equal(originalHistory, JsonSerializer.Serialize(repaired.History.Take(state.History.Count), TaskJson.Options));
                Check.Equal(state.Budget.Usage.TotalTokens + fresh.Calls * 5L, repaired.Budget.Usage.TotalTokens);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Invalid()
        {
            string root = Root();
            try
            {
                var model = new SummaryModel { Invalid = true }; await using var runtime = Runtime(model, root);
                var state = (await runtime.ExecuteAsync(LongStart(), default)).State;
                Check.Equal(1, model.Summaries); Check.True(state.Compactions.All(c => c.Status == CompactionStatus.Failed));
                Check.True(state.ContextMemory.SummaryId == null); Check.True(state.Status != AgentTaskStatus.Failed);
                Check.Equal((long)model.Calls * 5, state.Budget.Usage.TotalTokens); Check.Equal(LongStart().Request!.Messages.Count + 1, state.History.Count);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Responded()
        {
            string root = Root();
            try
            {
                var first = new SummaryModel(); string id;
                await using (var runtime = Runtime(first, root))
                {
                    runtime.FaultInjection = phase => { if (phase == "summary_before_activation") throw new CheckpointWriteException(new IOException("activation window")); };
                    var command = LongStart(); id = "task_" + TaskJson.Hash(command.Id).Substring(0, 32);
                    await Check.ThrowsAsync<CheckpointWriteException>(() => runtime.ExecuteAsync(command, default));
                }
                var second = new SummaryModel(); await using var restarted = Runtime(second, root);
                var recovered = (await restarted.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = id }, default)).State;
                Check.Equal(0, second.Calls); Check.True(recovered.ContextMemory.SummaryId != null);
                Check.Equal(1, recovered.Budget.ModelCalls); Check.Equal(5L, recovered.Budget.Usage.TotalTokens);
                var done = (await restarted.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = id,
                    RecoveryToken = recovered.Recovery.ContinuationToken }, default)).State;
                Check.Equal(0, second.Summaries); Check.Equal(TurnStatus.Completed, done.Turns.Single().Status);
                Check.Equal(2, done.Budget.ModelCalls);
                string largerRoot = Path.Combine(root, "no-gain"); var store = new FileTaskStore(largerRoot);
                var seeded = new AgentTaskState { Model = Binding("same"), ModelLedgerInitialized = true,
                    Budget = new TaskBudget { ModelCalls = 1, Usage = new TokenUsage { InputTokens = 3, OutputTokens = 2 } } };
                var turn = new TaskTurn { Settings = HarnessTests.Request(), ModelCalls = 1, Usage = new TokenUsage { InputTokens = 3, OutputTokens = 2 } };
                turn.Settings.Messages.Clear(); seeded.Turns.Add(turn);
                seeded.History.Add(new HistoryEntry { TurnId = turn.Id, Message = new Message { Text = new string('x', 300) } });
                seeded.History.Add(new HistoryEntry { TurnId = turn.Id, Message = new Message { Role = "assistant", Text = "done" } });
                seeded.History.Add(new HistoryEntry { TurnId = turn.Id, Message = new Message { Text = "current request" } });
                var candidate = new CompactionRecord { TurnId = turn.Id, Status = CompactionStatus.Responded, CoveredCount = 1,
                    SourceIds = { seeded.History[0].Id }, SourceHash = MiniHarness.Harness.Context.ModelContextProjection.PrefixHash(seeded, 1), Binding = Binding("same") };
                var billed = new ModelInvocationRecord { TurnId = turn.Id, CompactionId = candidate.Id, Purpose = ModelInvocationPurpose.Summary,
                    Status = ModelInvocationStatus.Responded, UsageApplied = true, Usage = new TokenUsage { InputTokens = 3, OutputTokens = 2 },
                    Response = HarnessTests.Answer(JsonSerializer.Serialize(new SummaryContent { Background = new string('y', 600), References = { seeded.History[0].Id } }, TaskJson.Options)) };
                candidate.InvocationId = billed.Id; seeded.Compactions.Add(candidate); seeded.ModelInvocations.Add(billed);
                await using (var lease = await store.AcquireAsync(seeded.Id, default)) await lease.CommitAsync(seeded, 0, default);
                var untouched = new SummaryModel(); await using var noGain = Runtime(untouched, largerRoot);
                var rejected = (await noGain.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = seeded.Id }, default)).State;
                Check.Equal(0, untouched.Calls); Check.True(rejected.ContextMemory.SummaryId == null);
                Check.Equal(CompactionStatus.Failed, rejected.Compactions.Single().Status); Check.Equal(5L, rejected.Budget.Usage.TotalTokens);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Cancelled()
        {
            string root = Root();
            try
            {
                var model = new SummaryModel { HoldSummary = true }; string id; AgentTaskState paused;
                await using (var runtime = Runtime(model, root))
                {
                    var start = LongStart(); id = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                    var running = runtime.ExecuteAsync(start, default); await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var pause = runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Pause, TaskId = id }, default);
                    await running; paused = (await pause).State;
                    Check.Equal(AgentTaskStatus.Paused, paused.Status); Check.Equal(1, paused.Budget.ModelCalls); Check.True(!paused.Budget.Usage.Known);
                }
                var next = new SummaryModel(); await using var recovered = Runtime(next, root, new TrustedAuthorization());
                var state = (await recovered.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = id }, default)).State;
                Check.Equal(0, next.Calls); Check.True(!state.Recovery.CanResume);
                var settle = new TaskCommand { Kind = TaskCommandKind.ReconcileModelUsage, TaskId = id,
                    ModelUsage = new ModelUsageReconciliation { InvocationId = state.ModelInvocations.Single().Id, Basis = "host approved upper bound",
                        ConservativeUpperBound = true, Usage = new TokenUsage { InputTokens = 20, OutputTokens = 10 } } };
                state = (await recovered.ExecuteAsync(settle, default)).State;
                state = (await recovered.ExecuteAsync(settle, default)).State;
                Check.True(state.Budget.Usage.Known); Check.Equal(30L, state.Budget.Usage.TotalTokens); Check.Equal(1, state.Budget.ModelCalls);
                Check.Equal(30L, state.Turns.Single().Usage.TotalTokens);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Maintenance()
        {
            string root = Root();
            try
            {
                var model = new SummaryModel(); var limits = Limits(); limits.MaxModelCalls = 1;
                await using var runtime = new TaskRuntime(model, new FileTaskStore(root), limits, Binding("same"), contextOptions: ContextConfig());
                var state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, default)).State;
                Check.Equal(TurnStatus.Completed, state.Turns.Single().Status);
                var compacted = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.CompactContext, TaskId = state.Id }, default)).State;
                Check.Equal(TurnStatus.Completed, compacted.Turns.Single().Status); Check.Equal(WaitReason.Input, compacted.Wait);
                Check.Equal(1, model.Calls); Check.Equal(state.Turns.Single().ResponseHistoryCount, compacted.Turns.Single().ResponseHistoryCount);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task ChildUsage()
        {
            string root = Root();
            try
            {
                var store = new FileTaskStore(root); var parent = new AgentTaskState { Model = Binding("same"), ModelLedgerInitialized = true };
                var parentTurn = new TaskTurn { Status = TurnStatus.Completed, Settings = HarnessTests.Request() }; parentTurn.Settings.Messages.Clear(); parent.Turns.Add(parentTurn);
                var child = new AgentTaskState { ParentTaskId = parent.Id, Model = Binding("same"), ModelLedgerInitialized = true, Status = AgentTaskStatus.Cancelled,
                    Budget = new TaskBudget { MaxModelCalls = 3, MaxTokens = 500, ModelCalls = 1, Usage = new TokenUsage { Known = false } } };
                var childTurn = new TaskTurn { Status = TurnStatus.Interrupted, Settings = HarnessTests.Request(), ModelCalls = 1, Usage = new TokenUsage { Known = false } };
                childTurn.Settings.Messages.Clear(); child.Turns.Add(childTurn);
                var invocation = new ModelInvocationRecord { TurnId = childTurn.Id, Purpose = ModelInvocationPurpose.Summary, Status = ModelInvocationStatus.Unknown, Request = HarnessTests.Request() };
                child.ModelInvocations.Add(invocation);
                parent.Children.Add(new ChildTaskLink { TaskId = child.Id, StartCommandId = "seeded", Allocation = new TaskBudget { MaxModelCalls = 3, MaxToolCalls = 0, MaxTokens = 500 } });
                parent.Budget.ReservedModelCalls = 3; parent.Budget.ReservedTokens = 500;
                foreach (var state in new[] { parent, child }) { await using var lease = await store.AcquireAsync(state.Id, default); await lease.CommitAsync(state, 0, default); }
                await using var runtime = new TaskRuntime(new SummaryModel(), store, Limits(), Binding("same"), recoveryAuthorizer: new TrustedAuthorization());
                var collected = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.CollectChild, TaskId = parent.Id, ChildTaskId = child.Id }, default)).State;
                Check.True(!collected.Budget.Usage.Known); Check.Equal(1, collected.Budget.ModelCalls); Check.Equal(0, collected.Budget.ReservedModelCalls);
                await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ReconcileModelUsage, TaskId = child.Id,
                    ModelUsage = new ModelUsageReconciliation { InvocationId = invocation.Id, Basis = "confirmed provider usage", Usage = new TokenUsage { InputTokens = 3, OutputTokens = 2 } } }, default);
                for (int count = 0; count < 2; count++)
                    collected = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.CollectChild, TaskId = parent.Id, ChildTaskId = child.Id }, default)).State;
                Check.True(collected.Budget.Usage.Known); Check.Equal(5L, collected.Budget.Usage.TotalTokens); Check.Equal(1, collected.Budget.ModelCalls);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task FailedPublication()
        {
            string root = Root();
            try
            {
                var store = new ResultFailureStore(new FileTaskStore(root)); var model = new SummaryModel();
                await using var runtime = new TaskRuntime(model, store, Limits(), Binding("same"), contextOptions: ContextConfig());
                var command = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() };
                string id = "task_" + TaskJson.Hash(command.Id).Substring(0, 32);
                await Check.ThrowsAsync<CheckpointWriteException>(() => runtime.ExecuteAsync(command, default));
                var failure = await runtime.ReadFailureAsync(id, default); Check.Equal(nameof(CheckpointWriteException), failure!.ErrorType);
                Check.Equal(nameof(UnauthorizedAccessException), failure.InnerType); Check.Equal(1, model.Calls);
                store.Fail = false;
                var recovered = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = id }, default)).State;
                Check.Equal(1, recovered.Budget.ModelCalls); Check.Equal(5L, recovered.Budget.Usage.TotalTokens);
                var done = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = id, RecoveryToken = recovered.Recovery.ContinuationToken }, default)).State;
                Check.Equal(TurnStatus.Completed, done.Turns.Single().Status); Check.Equal(1, model.Calls); Check.True(await runtime.ReadFailureAsync(id, default) == null);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task ExhaustedSettlement()
        {
            string root = Root();
            try
            {
                foreach (long tokenLimit in new long[] { 6, 5 })
                    foreach (bool known in new[] { true, false })
                    foreach (bool accepted in new[] { true, false })
                    {
                        string path = Path.Combine(root, tokenLimit + "-" + known + "-" + accepted);
                        var response = HarnessTests.Answer("saved answer"); response.Usage.Known = known;
                        var model = new ScriptedModel(response);
                        var limits = new HarnessOptions { MaxModelCalls = 1, MaxTotalTokens = tokenLimit };
                        var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() };
                        string id = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                        await using (var first = new TaskRuntime(model, new FileTaskStore(path), limits, Binding("same")))
                        {
                            first.FaultInjection = phase => { if (phase == "model_responded") throw new CheckpointWriteException(new IOException("saved response crash")); };
                            await Check.ThrowsAsync<CheckpointWriteException>(() => first.ExecuteAsync(start, default));
                        }
                        var fresh = new SummaryModel();
                        await using var runtime = new TaskRuntime(fresh, new FileTaskStore(path), limits, Binding("same"),
                            completion: new SettlementPolicy(accepted));
                        var recovered = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = id }, default)).State;
                        Check.Equal(known, recovered.Recovery.CanResume);
                        var done = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = id,
                            RecoveryToken = recovered.Recovery.ContinuationToken }, default)).State;
                        Check.Equal(known && accepted, done.Status == AgentTaskStatus.Completed);
                        Check.Equal(known && accepted, done.Turns.Single().Status == TurnStatus.Completed);
                        if (!known || !accepted) Check.Equal(RecoveryClassification.Budget, done.Recovery.Classification);
                        Check.Equal(0, fresh.Calls); Check.Equal(1, done.Budget.ModelCalls);
                        Check.Equal(5L, done.Budget.Usage.TotalTokens); Check.Equal(1, done.ModelInvocations.Count);
                        Check.Equal("saved answer", done.Turns.Single().LastResponse!.Message.Text);
                    }
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task ExhaustedResourceSettlement()
        {
            string root = Root();
            try
            {
                var store = new FileTaskStore(root); var limits = new HarnessOptions { MaxModelCalls = 1 };
                var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() };
                string id = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                await using (var first = new TaskRuntime(new SummaryModel(), store, limits, Binding("same")))
                {
                    first.FaultInjection = phase => { if (phase == "model_responded") throw new CheckpointWriteException(new IOException("saved response crash")); };
                    await Check.ThrowsAsync<CheckpointWriteException>(() => first.ExecuteAsync(start, default));
                }
                var saved = (await store.LoadAsync(id, default))!;
                saved.Resources.Add(new ResourceReference { Id = "resource-final", OwnerTaskId = id, Kind = "settlement", State = "running" });
                await using (var lease = await store.AcquireAsync(id, default)) await lease.CommitAsync(saved, saved.Revision, default);
                var manager = new SettlementResource(); var model = new SummaryModel();
                await using var runtime = new TaskRuntime(model, store, limits, Binding("same"), completion: new SettlementPolicy(true), resourceManagers: new[] { manager });
                var recovered = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = id }, default)).State;
                var waiting = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = id,
                    RecoveryToken = recovered.Recovery.ContinuationToken }, default)).State;
                Check.Equal(WaitReason.Resource, waiting.Wait); Check.True(waiting.Recovery.CanWake);
                manager.Finished = true;
                var done = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ResourceWake, TaskId = id,
                    Wake = new ResourceWake { WaitId = waiting.ResourceWaits.Single().Id } }, default)).State;
                Check.Equal(AgentTaskStatus.Completed, done.Status); Check.Equal(0, model.Calls);
                Check.Equal(1, done.Budget.ModelCalls); Check.Equal(5L, done.Budget.Usage.TotalTokens);
            }
            finally { Directory.Delete(root, true); }
        }
        private sealed class SettlementResource : IResourceManager
        {
            public string Kind => "settlement";
            public bool Finished { get; set; }
            public Task<ResourceReference> ExecuteAsync(ResourceReference resource, ResourceAction action, CancellationToken ct)
            { resource.State = Finished ? "finished" : "running"; return Task.FromResult(resource); }
        }
        private sealed class SettlementPolicy : ITaskCompletionPolicy
        {
            private readonly bool accepted;
            public SettlementPolicy(bool accepted) { this.accepted = accepted; }
            public Task<TaskCompletionVerdict> EvaluateAsync(AgentTaskState state, ModelResponse candidate, CancellationToken ct)
                => Task.FromResult(new TaskCompletionVerdict { CompleteTask = accepted, CanContinue = !accepted, Feedback = "Need further verification" });
        }
        private sealed class ResultFailureStore : ITaskStore
        {
            private readonly ITaskStore inner; public bool Fail { get; set; } = true;
            public ResultFailureStore(ITaskStore inner) { this.inner = inner; }
            public Task<AgentTaskState?> LoadAsync(string id, CancellationToken ct) => inner.LoadAsync(id, ct);
            public Task<TaskResponseReference?> ResolveResponseAsync(string id, CancellationToken ct) => inner.ResolveResponseAsync(id, ct);
            public async Task<ITaskLease> AcquireAsync(string id, CancellationToken ct) => new Lease(await inner.AcquireAsync(id, ct), this);
            private sealed class Lease : ITaskLease
            {
                private readonly ITaskLease inner; private readonly ResultFailureStore owner;
                public Lease(ITaskLease inner, ResultFailureStore owner) { this.inner = inner; this.owner = owner; }
                public Task<AgentTaskState?> LoadAsync(CancellationToken ct) => inner.LoadAsync(ct);
                public Task CommitAsync(AgentTaskState state, long revision, CancellationToken ct)
                {
                    if (owner.Fail && state.ModelInvocations.Any(i => i.ResultApplied)) throw new CheckpointWriteException(new UnauthorizedAccessException("Injected publication failure"));
                    return inner.CommitAsync(state, revision, ct);
                }
                public ValueTask DisposeAsync() => inner.DisposeAsync();
            }
        }
        private static async Task Binding()
        {
            string root = Root();
            try
            {
                string id;
                await using (var first = Runtime(new SummaryModel(), root))
                    id = (await first.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, default)).State.Id;
                var model = new SummaryModel(); await using var second = Runtime(model, root, version: "changed");
                var state = (await second.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = id }, default)).State;
                Check.Equal(RecoveryClassification.ModelConfiguration, state.Recovery.Classification);
                await Check.ThrowsAsync<TaskConflictException>(() => second.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Input, TaskId = id, Request = HarnessTests.Request() }, default));
                await second.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = id }, default); Check.Equal(0, model.Calls);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task BoundariesAndSteering()
        {
            string root = Root();
            try
            {
                var blockedModel = new SummaryModel();
                await using (var runtime = Runtime(blockedModel, Path.Combine(root, "pinned")))
                {
                    var request = HarnessTests.Request(); request.Messages.Insert(0, new Message { Role = "system", Text = new string('p', 8000) });
                    var state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = request }, default)).State;
                    Check.Equal(0, blockedModel.Calls); Check.Equal(WaitReason.Budget, state.Wait); Check.Equal(2, state.History.Count);
                }
                var batchModel = new SummaryModel();
                await using (var runtime = Runtime(batchModel, Path.Combine(root, "batch")))
                {
                    var request = HarnessTests.Request(); request.Messages.Add(HarnessTests.Call("add", "{}", "batch").Message);
                    request.Messages.Add(new Message { Role = "tool", ToolCallId = "batch", Text = new string('x', 10000) });
                    request.Messages.Add(new Message { Text = "current" });
                    var state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = request }, default)).State;
                    Check.Equal(0, batchModel.Calls); Check.Equal(WaitReason.Budget, state.Wait);
                }
                var model = new SummaryModel { HoldForSteering = true };
                await using var core = Runtime(model, Path.Combine(root, "steering"));
                var command = LongStart(); string id = "task_" + TaskJson.Hash(command.Id).Substring(0, 32);
                var running = core.ExecuteAsync(command, default); await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var correcting = core.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Steer, TaskId = id,
                    Steering = new TaskSteering { Text = "Inspect the corrected goal first.", Constraints = { "Do not change protected tests." } } }, default);
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!core.IsSteeringPending(id)) await Task.Delay(10, limit.Token);
                model.Release.TrySetResult(true); await running;
                var done = (await correcting.WaitAsync(limit.Token)).State;
                Check.Equal(TurnStatus.Completed, done.Turns.Single().Status);
                Check.True(done.Compactions.Any(c => c.Error == "control_pending"));
                Check.True(model.Business.Last().Messages.Any(m => m.Text == "Inspect the corrected goal first."));
                Check.True(done.Compactions.Where(c => c.Status == CompactionStatus.Committed).All(c => c.SteeringRevision == 1));
                Check.True(!done.Steps.Any(s => s.Status == StepStatus.Preparing));
                Check.Equal((long)model.Calls * 5, done.Budget.Usage.TotalTokens);
                var natural = new NaturalGate(); await using var terminalCore = Runtime(natural, Path.Combine(root, "natural"));
                var naturalStart = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() };
                string naturalId = "task_" + TaskJson.Hash(naturalStart.Id).Substring(0, 32);
                var naturalRunning = terminalCore.ExecuteAsync(naturalStart, default); await natural.Entered.Task.WaitAsync(limit.Token);
                var newGoal = terminalCore.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Steer, TaskId = naturalId,
                    Steering = new TaskSteering { Text = "New goal after natural answer" } }, default);
                while (!terminalCore.IsSteeringPending(naturalId)) await Task.Delay(10, limit.Token);
                natural.Release.TrySetResult(true); await naturalRunning; await newGoal;
                Check.Equal(2, natural.Calls); Check.True(natural.Last!.Messages.Any(m => m.Text == "New goal after natural answer"));
                string workspace = Path.Combine(root, "changing-workspace"); Directory.CreateDirectory(workspace);
                string file = Path.Combine(workspace, "version.txt"); await File.WriteAllTextAsync(file, "before");
                var changing = new SummaryModel { HoldForSteering = true }; await using var changingCore = Runtime(changing, Path.Combine(root, "changing-store"));
                var changingStart = LongStart(); changingStart.Facts = new TaskFacts { Goal = "Keep current authoritative revision", Workspace = new WorkspaceScope { RootPath = workspace } };
                var preparing = changingCore.ExecuteAsync(changingStart, default); await changing.Entered.Task.WaitAsync(limit.Token);
                await File.WriteAllTextAsync(file, "after"); changing.Release.TrySetResult(true);
                var refreshed = (await preparing.WaitAsync(limit.Token)).State;
                Check.True(changing.Business.Last().Messages.Any(m => m.Role == "system" && m.Text.Contains(refreshed.Facts.Workspace.Revision, StringComparison.Ordinal)));
                Check.True(refreshed.Events.Any(e => e.Type == "ContextPreparationInvalidated"));
            }
            finally { Directory.Delete(root, true); }
        }
        private sealed class NaturalGate : IModelProvider
        {
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Calls { get; private set; }
            public ModelRequest? Last { get; private set; }
            public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
            {
                Calls++; Last = request;
                if (Calls == 1) { Entered.TrySetResult(true); await Release.Task.WaitAsync(ct); }
                return HarnessTests.Answer(Calls == 1 ? "old answer" : "new answer");
            }
        }
        internal sealed class TrustedAuthorization : IRecoveryAuthorizer
        {
            public bool AllowsUsageReconciliation => true;
            public Task<RecoveryAuthorization> CheckAsync(AgentTaskState task, ModelBinding current, CancellationToken ct)
                => Task.FromResult(new RecoveryAuthorization { Allowed = true });
        }
        internal sealed class SummaryModel : IModelProvider
        {
            public bool Invalid { get; set; }
            public bool HoldSummary { get; set; }
            public bool HoldForSteering { get; set; }
            public TaskCompletionSource<bool> Release { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Calls { get; private set; }
            public int Summaries { get; private set; }
            public List<ModelRequest> Business { get; } = new List<ModelRequest>();
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
            {
                Calls++;
                if (request.Messages[0].Text.StartsWith("Create historical memory", StringComparison.Ordinal))
                {
                    Summaries++; Entered.TrySetResult(true); if (HoldSummary) await Task.Delay(-1, ct);
                    if (HoldForSteering) await Release.Task.WaitAsync(ct);
                    using var source = JsonDocument.Parse(request.Messages.Last().Text);
                    string id = source.RootElement.GetProperty("source")[0].GetProperty("id").GetString()!;
                    return HarnessTests.Answer(Invalid ? "{}" : JsonSerializer.Serialize(new SummaryContent
                    { Background = "Historical observations only. Tests are not proof; keep immutable marker.", References = { id } }, TaskJson.Options));
                }
                Business.Add(TaskJson.Clone(request)); return HarnessTests.Answer(new string('z', 500));
            }
        }
    }
}
