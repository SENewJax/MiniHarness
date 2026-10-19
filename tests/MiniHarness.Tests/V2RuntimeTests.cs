using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Coding.Processes;
using MiniHarness.Harness;

namespace MiniHarness.Tests
{
    internal static class V2RuntimeTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v2-wait] durable wait releases execution, stdin and duplicate wake preserve budget", WaitFlow));
            tests.Add(("[v2-steer] waiting steer preserves identity and budget; model safe point rejects old action", Steering));
            tests.Add(("[v2-steer] invalid payloads never yield or invalidate an in-flight model action", InvalidSteering));
            tests.Add(("[v2-resource] live resources block task completion and cancellation confirms tree", Completion));
            tests.Add(("[v2-resource] completion timeout returns to model decision and permits resource cleanup", CompletionTimeout));
            tests.Add(("[v2-wait] deadline wakes without killing and output registered after readiness is consumed once", WaitConditions));
            tests.Add(("[v2-recovery] failed task commit after real startup reconciles the original process without replay", StartupCommitWindow));
        }
        private static ModelBinding Binding() => new ModelBinding { Model = "test" };
        private static string Root() { if (!OperatingSystem.IsWindows()) throw new TestSkippedException("V2 Windows ownership required.");
            string root = Path.Combine(Path.GetTempPath(), "miniharness-v2-runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
        private static TaskCommand Start(string root) => new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request(),
            Facts = new TaskFacts { Workspace = new WorkspaceScope { RootPath = Path.Combine(root, "workspace"), Grants = { "read", "execute" } } } };
        private static TaskRuntime Runtime(IModelProvider model, ITaskStore store, ProcessSessionManager manager, ITaskCompletionPolicy? completion = null)
        {
            var tools = ProcessTool.CreateAll(manager);
            return new TaskRuntime(model, store, new HarnessOptions(), Binding(), tools: new ToolRegistry(tools),
                toolPolicy: new AllowListToolPolicy(tools.Select(t => t.Definition.Name)), resourceManagers: new[] { manager }, completion: completion);
        }
        private static ProcessSessionManager Manager(string root) => new ProcessSessionManager(new ProcessSessionOptions { Directory = Path.Combine(root, "processes") },
            new[] { V2ProcessTests.Profile("echo"), V2ProcessTests.Profile("hold") });
        private static async Task WaitFlow()
        {
            string root = Root(); Directory.CreateDirectory(Path.Combine(root, "workspace"));
            try
            {
                await using var manager = Manager(root); var model = new WaitingModel();
                var store = new FileTaskStore(Path.Combine(root, "tasks"));
                await using var runtime = Runtime(model, store, manager);
                var waiting = (await runtime.ExecuteAsync(Start(root), default)).State;
                Check.Equal(WaitReason.Resource, waiting.Wait); Check.Equal(2, waiting.Budget.ModelCalls);
                var resource = waiting.Resources.Single(); var wait = waiting.ResourceWaits.Single();
                var pause = await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Pause, TaskId = waiting.Id }, default).WaitAsync(TimeSpan.FromSeconds(2));
                await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.WriteResourceInput, TaskId = waiting.Id, ResourceId = resource.Id,
                    ResourceInput = new ResourceInput { ExpectedGeneration = manager.Generation, Text = "exit\n" } }, default);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!ResourceStates.IsTerminal((await manager.ExecuteAsync(resource, ResourceAction.Query, deadline.Token)).State)) await Task.Delay(20, deadline.Token);
                var wake = new TaskCommand { Kind = TaskCommandKind.ResourceWake, TaskId = waiting.Id, Wake = new ResourceWake { WaitId = wait.Id } };
                var paused = (await runtime.ExecuteAsync(wake, default)).State;
                Check.Equal(AgentTaskStatus.Paused, paused.Status); Check.Equal(2, model.Calls);
                await runtime.ExecuteAsync(wake, default);
                var done = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = waiting.Id }, default)).State;
                Check.Equal(TurnStatus.Completed, done.Turns.Single().Status); Check.Equal(3, model.Calls);
                Check.Equal(resource.OwnerOperationId, done.Resources.Single().OwnerOperationId);
                Check.Equal(1, done.History.Count(h => h.Message.Text.StartsWith("[External resource observation", StringComparison.Ordinal)));
                Check.Equal(1, done.History.Count(h => h.Message.Role == "tool" && h.Message.ToolCallId == "wait_call"));
                var stale = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ResourceWake, TaskId = done.Id,
                    Wake = new ResourceWake { WaitId = wait.Id } }, default)).State;
                Check.Equal(3, stale.Budget.ModelCalls);
                var uncertainModel = new WaitingModel();
                await using var uncertainRuntime = Runtime(uncertainModel, new FileTaskStore(Path.Combine(root, "uncertain-input")), manager);
                var uncertainTask = (await uncertainRuntime.ExecuteAsync(Start(root), default)).State;
                manager.FaultInjection = phase => { if (phase == "after_stdin_write") throw new IOException("Injected native stdin uncertainty"); };
                var input = new TaskCommand { Kind = TaskCommandKind.WriteResourceInput, TaskId = uncertainTask.Id,
                    ResourceId = uncertainTask.Resources.Single().Id, ResourceInput = new ResourceInput { ExpectedGeneration = manager.Generation, Text = "uncertain\n" } };
                await Check.ThrowsAsync<IOException>(() => uncertainRuntime.ExecuteAsync(input, default));
                manager.FaultInjection = null;
                var blocked = (await uncertainRuntime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = uncertainTask.Id }, default)).State;
                Check.Equal(WaitReason.Reconciliation, blocked.Wait); Check.Equal(2, uncertainModel.Calls);
                input.ResourceInput.Text = "different";
                await Check.ThrowsAsync<TaskConflictException>(() => uncertainRuntime.ExecuteAsync(input, default));
                await uncertainRuntime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ReconcileResources, TaskId = uncertainTask.Id }, default);
                blocked = (await uncertainRuntime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Steer, TaskId = uncertainTask.Id,
                    Steering = new TaskSteering { Text = "Keep this correction, but do not bypass reconciliation." } }, default)).State;
                Check.Equal(WaitReason.Reconciliation, blocked.Wait); Check.Equal(2, uncertainModel.Calls);
                await uncertainRuntime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = uncertainTask.Id }, default);
            }
            finally { Directory.Delete(root, true); }
        }
        private sealed class WaitingModel : IModelProvider
        {
            public string Condition { get; set; } = "Exit";
            public int WaitMilliseconds { get; set; } = 30000;
            public int Calls { get; private set; }
            public List<ModelRequest> Inputs { get; } = new List<ModelRequest>();
            public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
            {
                Calls++; Inputs.Add(TaskJson.Clone(request));
                if (Calls == 1) return Task.FromResult(HarnessTests.Call("start_process", "{\"profile\":\"echo\"}", "start_call"));
                if (Calls == 2)
                {
                    var result = JsonSerializer.Deserialize<ToolResult>(request.Messages.Last(m => m.Role == "tool").Text, TaskJson.Options)!;
                    var resource = JsonSerializer.Deserialize<ResourceReference>(result.Output, TaskJson.Options)!;
                    return Task.FromResult(HarnessTests.Call("wait_process", JsonSerializer.Serialize(new { resource_id = resource.Id, condition = Condition, wait_ms = WaitMilliseconds }), "wait_call"));
                }
                return Task.FromResult(HarnessTests.Answer("done"));
            }
        }
        private static async Task WaitConditions()
        {
            string root = Root(); Directory.CreateDirectory(Path.Combine(root, "workspace"));
            try
            {
                await using var manager = Manager(root);
                foreach (string condition in new[] { "Exit", "Output" })
                {
                    var model = new WaitingModel { Condition = condition, WaitMilliseconds = condition == "Exit" ? 20 : 30000 };
                    await using var runtime = Runtime(model, new FileTaskStore(Path.Combine(root, condition)), manager);
                    var waiting = (await runtime.ExecuteAsync(Start(root), default)).State;
                    var wait = waiting.ResourceWaits.Single();
                    if (condition == "Exit")
                    {
                        while (DateTimeOffset.UtcNow < wait.DeadlineUtc) await Task.Delay(5);
                    }
                    else
                    {
                        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        while ((await manager.ExecuteAsync(waiting.Resources.Single(), ResourceAction.Query, limit.Token)).Process!.LastSequence == 0) await Task.Delay(10, limit.Token);
                    }
                    var wake = new TaskCommand { Kind = TaskCommandKind.ResourceWake, TaskId = waiting.Id, Wake = new ResourceWake { WaitId = wait.Id } };
                    var done = (await runtime.ExecuteAsync(wake, default)).State;
                    await runtime.ExecuteAsync(wake, default);
                    Check.Equal(3, done.Budget.ModelCalls); Check.Equal("running", done.Resources.Single().State);
                    Check.Equal(condition == "Exit" ? "timed_out" : "consumed", done.ResourceWaits.Single().Status);
                    await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = waiting.Id }, default);
                }
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Steering()
        {
            string root = Root(); Directory.CreateDirectory(Path.Combine(root, "workspace"));
            try
            {
                await using (var manager = Manager(root))
                {
                    var model = new WaitingModel(); await using var runtime = Runtime(model, new FileTaskStore(Path.Combine(root, "waiting")), manager);
                    var waiting = (await runtime.ExecuteAsync(Start(root), default)).State;
                    var steer = new TaskCommand { Kind = TaskCommandKind.Steer, TaskId = waiting.Id,
                        Steering = new TaskSteering { Text = "New priority: inspect B", Constraints = { "Do not modify A" } } };
                    var result = (await runtime.ExecuteAsync(steer, default)).State;
                    await runtime.ExecuteAsync(steer, default);
                    Check.Equal(waiting.Turns.Single().Id, result.Turns.Single().Id); Check.Equal(3, result.Budget.ModelCalls);
                    Check.True(model.Inputs.Last().Messages.Any(m => m.Text == steer.Steering.Text));
                    Check.Equal("invalidated", result.ResourceWaits.Single().Status);
                    await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = result.Id }, default);
                }
                var gated = new GatedModel();
                await using var core = new TaskRuntime(gated, new FileTaskStore(Path.Combine(root, "gated")), new HarnessOptions(), Binding());
                var start = Start(root); string id = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                var running = core.ExecuteAsync(start, default); await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var correction = core.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Steer, TaskId = id, Steering = new TaskSteering { Text = "Change direction" } }, default);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!core.IsSteeringPending(id)) await Task.Delay(10, deadline.Token);
                gated.Release.TrySetResult(true); await running;
                var done = (await correction).State;
                Check.Equal(ToolCallStatus.Rejected, done.Operations.Single().Outcome!.Status);
                Check.True(done.Operations.Single().Outcome!.Output.Contains("user steering", StringComparison.Ordinal));
                Check.Equal(0, done.Budget.ToolCalls);
                Check.Equal(TurnStatus.Completed, done.Turns.Single().Status);
                Check.True(gated.Last!.Messages.Any(m => m.Text == "Change direction"));
                foreach (bool absence in new[] { false, true })
                {
                    var uncertain = new UncertainTool(); var scripted = new ScriptedModel(HarnessTests.Call("uncertain", "{}"), HarnessTests.Answer("safe"));
                    await using var pendingCore = new TaskRuntime(scripted, new FileTaskStore(Path.Combine(root, "pending-" + absence)), new HarnessOptions(), Binding(),
                        tools: new ToolRegistry(new[] { uncertain }), toolPolicy: new AllowListToolPolicy(new[] { "uncertain" }));
                    var original = (await pendingCore.ExecuteAsync(Start(root), default)).State;
                    var deferred = (await pendingCore.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Steer, TaskId = original.Id,
                        Steering = new TaskSteering { Text = "New plan after reconciliation" } }, default)).State;
                    Check.Equal(WaitReason.Reconciliation, deferred.Wait); Check.Equal(1, deferred.DeferredMessages.Count);
                    var resolved = (await pendingCore.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Reconcile, TaskId = original.Id,
                        OperationId = original.Operations.Single().Id, ReconciledOutcome = new ToolOutcome { Effect = absence ? EffectState.None : EffectState.Confirmed,
                            Status = ToolCallStatus.Succeeded, Output = "operator verified" } }, default)).State;
                    Check.Equal(1, uncertain.Count); Check.Equal(0, resolved.DeferredMessages.Count);
                    var messages = scripted.Inputs.Last().Messages;
                    ConversationRules.Validate(messages);
                    Check.True(messages.FindLastIndex(m => m.Role == "tool") < messages.FindIndex(m => m.Text == "New plan after reconciliation"));
                    Check.Equal(1, resolved.Steps.Last().SteeringRevision);
                }
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task InvalidSteering()
        {
            string root = Root(); Directory.CreateDirectory(Path.Combine(root, "workspace"));
            try
            {
                var model = new GatedModel { ToolName = "steer_probe" }; var tool = new SteeringProbeTool();
                await using var runtime = new TaskRuntime(model, new FileTaskStore(Path.Combine(root, "tasks")), new HarnessOptions(), Binding(),
                    tools: new ToolRegistry(new[] { tool }), toolPolicy: new AllowListToolPolicy(new[] { "steer_probe" }));
                var start = Start(root); string id = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                var running = runtime.ExecuteAsync(start, default);
                try
                {
                    await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var invalid = new TaskSteering?[] { null, new TaskSteering { Text = " " },
                        new TaskSteering { Text = new string('x', 16001) }, new TaskSteering { Text = "valid", Constraints = null! },
                        new TaskSteering { Text = "valid", Constraints = { null! } },
                        new TaskSteering { Text = "valid", Constraints = { new string('x', 16001) } },
                        new TaskSteering { Text = "valid", Constraints = Enumerable.Repeat("constraint", 129).ToList() } };
                    foreach (var payload in invalid)
                    {
                        await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(new TaskCommand
                            { Kind = TaskCommandKind.Steer, TaskId = id, Steering = payload }, default).WaitAsync(TimeSpan.FromSeconds(2)));
                        Check.True(!runtime.IsSteeringPending(id));
                    }
                }
                finally { model.Release.TrySetResult(true); }
                var done = (await running.WaitAsync(TimeSpan.FromSeconds(5))).State;
                Check.Equal(TurnStatus.Completed, done.Turns.Single().Status); Check.Equal(1, tool.Count);
                Check.Equal(1, done.Budget.ToolCalls); Check.Equal(2, done.Budget.ModelCalls);
                Check.Equal(ToolCallStatus.Succeeded, done.Operations.Single().Outcome!.Status);
                Check.Equal(0, done.SteeringRevision); Check.Equal(0, done.DeferredMessages.Count);
                Check.True(!done.Events.Any(e => e.Type == "SteeringYield" || e.Type == "SteeringAccepted"));
                ConversationRules.Validate(done.History.Select(h => h.Message));
            }
            finally { Directory.Delete(root, true); }
        }
        private sealed class SteeringProbeTool : ITool
        {
            public int Count { get; private set; }
            public ToolDefinition Definition { get; }
            public SteeringProbeTool()
            {
                using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[]}");
                Definition = new ToolDefinition { Name = "steer_probe", Parameters = schema.RootElement.Clone() };
            }
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) { Count++; return Task.FromResult("executed"); }
        }
        private sealed class GatedModel : IModelProvider
        {
            public string ToolName { get; set; } = "must_not_execute";
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public ModelRequest? Last { get; private set; }
            public async Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
            {
                if (Last == null) { Last = request; Entered.TrySetResult(true); await Release.Task.WaitAsync(ct); return HarnessTests.Call(ToolName, "{}"); }
                Last = request; return HarnessTests.Answer();
            }
        }
        private sealed class UncertainTool : IContextualTool
        {
            public int Count { get; private set; }
            public ToolDefinition Definition { get; }
            public UncertainTool()
            {
                using var json = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
                Definition = new ToolDefinition { Name = "uncertain", Parameters = json.RootElement.Clone() };
            }
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) => throw new NotSupportedException();
            public Task<ToolOutcome> ExecuteAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
            { Count++; return Task.FromResult(new ToolOutcome { Status = ToolCallStatus.Failed, Effect = EffectState.Unknown, Output = "Uncertain effect" }); }
        }
        private static async Task Completion()
        {
            string root = Root(); Directory.CreateDirectory(Path.Combine(root, "workspace"));
            try
            {
                await using var manager = Manager(root);
                var model = new ScriptedModel(HarnessTests.Call("start_process", "{\"profile\":\"hold\"}"), HarnessTests.Answer("finished"));
                await using var runtime = Runtime(model, new FileTaskStore(Path.Combine(root, "tasks")), manager, new AlwaysComplete());
                var blocked = (await runtime.ExecuteAsync(Start(root), default)).State;
                Check.Equal(WaitReason.Resource, blocked.Wait); Check.Equal(AgentTaskStatus.Waiting, blocked.Status);
                var cancelled = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = blocked.Id }, default)).State;
                Check.Equal(AgentTaskStatus.Cancelled, cancelled.Status); Check.Equal("cancelled", cancelled.Resources.Single().State);
                Check.True(cancelled.Resources.Single().Process!.TerminationConfirmed);
                Check.Equal(2, cancelled.Budget.ModelCalls);
                var secondModel = new ScriptedModel(HarnessTests.Call("start_process", "{\"profile\":\"hold\"}"), HarnessTests.Answer("finished"));
                await using var finishRuntime = Runtime(secondModel, new FileTaskStore(Path.Combine(root, "finish")), manager, new AlwaysComplete());
                var pending = (await finishRuntime.ExecuteAsync(Start(root), default)).State;
                await finishRuntime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.CancelResource, TaskId = pending.Id, ResourceId = pending.Resources.Single().Id }, default);
                var complete = (await finishRuntime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ResourceWake, TaskId = pending.Id,
                    Wake = new ResourceWake { WaitId = pending.ResourceWaits.Single().Id } }, default)).State;
                Check.Equal(AgentTaskStatus.Completed, complete.Status); Check.Equal(2, complete.Budget.ModelCalls);
                var pairTool = new PairTool(); var pairManager = new PairManager();
                await using var pairRuntime = new TaskRuntime(new ScriptedModel(HarnessTests.Call("pair", "{}"), HarnessTests.Answer()),
                    new FileTaskStore(Path.Combine(root, "pair")), new HarnessOptions(), Binding(), tools: new ToolRegistry(new[] { pairTool }),
                    toolPolicy: new AllowListToolPolicy(new[] { "pair" }), resourceManagers: new[] { pairManager });
                var pair = (await pairRuntime.ExecuteAsync(Start(root), default)).State;
                var allCancelled = (await pairRuntime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = pair.Id }, default)
                    .WaitAsync(TimeSpan.FromSeconds(2))).State;
                Check.True(allCancelled.Resources.All(r => r.State == "cancelled")); Check.Equal(2, pairManager.Cancels);
                await using var hierarchy = Runtime(new ChildServiceModel(), new FileTaskStore(Path.Combine(root, "hierarchy")), manager);
                var parent = (await hierarchy.ExecuteAsync(Start(root), default)).State;
                var reserved = (await hierarchy.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.StartChild, TaskId = parent.Id,
                    Request = new ModelRequest { Model = "test", Messages = { new Message { Text = "child service" } } },
                    ChildBudget = new TaskBudget { MaxModelCalls = 2, MaxToolCalls = 1, MaxTokens = 1000 } }, default)).State;
                string childId = reserved.Children.Single().TaskId;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                AgentTaskState child;
                do { child = (await hierarchy.GetAsync(childId, deadline.Token))!; if (child.Turns.LastOrDefault()?.Status == TurnStatus.Completed) break;
                    await Task.Delay(10, deadline.Token); } while (true);
                Check.Equal("running", child.Resources.Single().State);
                await Check.ThrowsAsync<TaskConflictException>(() => hierarchy.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.CollectChild,
                    TaskId = parent.Id, ChildTaskId = childId }, default));
                var stoppedParent = (await hierarchy.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = parent.Id }, default)).State;
                Check.Equal(AgentTaskStatus.Cancelled, stoppedParent.Status); Check.True(stoppedParent.Children.Single().Collected);
                Check.True((await hierarchy.GetAsync(childId, default))!.Resources.Single().Process!.TerminationConfirmed);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task CompletionTimeout()
        {
            string root = Root(); Directory.CreateDirectory(Path.Combine(root, "workspace"));
            try
            {
                await using var manager = new ProcessSessionManager(new ProcessSessionOptions
                    { Directory = Path.Combine(root, "processes"), MaxWaitMilliseconds = 1 }, new[] { V2ProcessTests.Profile("hold") });
                var model = new CompletionTimeoutModel();
                await using var runtime = Runtime(model, new FileTaskStore(Path.Combine(root, "tasks")), manager, new AlwaysComplete());
                var waiting = (await runtime.ExecuteAsync(Start(root), default)).State;
                var wait = waiting.ResourceWaits.Single(); model.ResourceId = waiting.Resources.Single().Id;
                Check.True(wait.CompletionCandidate); Check.Equal(2, waiting.Budget.ModelCalls);
                while (DateTimeOffset.UtcNow <= wait.DeadlineUtc) await Task.Delay(1);
                var done = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ResourceWake, TaskId = waiting.Id,
                    Wake = new ResourceWake { WaitId = wait.Id } }, default).WaitAsync(TimeSpan.FromSeconds(10))).State;
                Check.Equal(AgentTaskStatus.Completed, done.Status); Check.Equal(4, done.Budget.ModelCalls);
                Check.True(model.SawTimeout); Check.Equal("cancelled", done.Resources.Single().State);
                Check.True(done.Resources.Single().Process!.TerminationConfirmed);
                Check.Equal(1, done.ResourceWaits.Count); Check.Equal("timed_out", done.ResourceWaits.Single().Status);
                Check.True(!done.ResourceWaits.Single().CompletionCandidate);
                var duplicate = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ResourceWake, TaskId = done.Id,
                    Wake = new ResourceWake { WaitId = wait.Id } }, default)).State;
                Check.Equal(4, duplicate.Budget.ModelCalls);
            }
            finally { Directory.Delete(root, true); }
        }
        private sealed class CompletionTimeoutModel : IModelProvider
        {
            public string ResourceId { get; set; } = "";
            public bool SawTimeout { get; private set; }
            private int calls;
            public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
            {
                calls++;
                if (calls == 1) return Task.FromResult(HarnessTests.Call("start_process", "{\"profile\":\"hold\"}"));
                if (calls == 3)
                {
                    SawTimeout = request.Messages.Any(m => m.Text.Contains("wait_timeout", StringComparison.Ordinal) &&
                        m.Text.Contains(ResourceId, StringComparison.Ordinal));
                    return Task.FromResult(HarnessTests.Call("cancel_process", JsonSerializer.Serialize(new { resource_id = ResourceId }), "cleanup"));
                }
                return Task.FromResult(HarnessTests.Answer("finished"));
            }
        }
        private sealed class AlwaysComplete : ITaskCompletionPolicy
        {
            public Task<TaskCompletionVerdict> EvaluateAsync(AgentTaskState state, ModelResponse candidate, CancellationToken ct)
                => Task.FromResult(new TaskCompletionVerdict { CompleteTask = true });
        }
        private sealed class PairTool : IContextualTool
        {
            public ToolDefinition Definition { get; }
            public PairTool()
            {
                using var json = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
                Definition = new ToolDefinition { Name = "pair", Parameters = json.RootElement.Clone() };
            }
            public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct) => throw new NotSupportedException();
            public Task<ToolOutcome> ExecuteAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
                => Task.FromResult(new ToolOutcome { Status = ToolCallStatus.Succeeded, Effect = EffectState.Confirmed, Output = "pair created",
                    Resources = { new ResourceReference { Id = "a", Kind = "pair", State = "running" }, new ResourceReference { Id = "b", Kind = "pair", State = "running" } } });
        }
        private sealed class ChildServiceModel : IModelProvider
        {
            public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
                => Task.FromResult(request.Messages.First(m => m.Role == "user").Text == "child service" && !request.Messages.Any(m => m.Role == "tool")
                    ? HarnessTests.Call("start_process", "{\"profile\":\"hold\"}") : HarnessTests.Answer("service available"));
        }
        private sealed class PairManager : IResourceManager
        {
            public string Kind => "pair";
            private int calls;
            public int Cancels => calls;
            private readonly TaskCompletionSource<bool> both = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<ResourceReference> ExecuteAsync(ResourceReference resource, ResourceAction action, CancellationToken ct)
            {
                if (action == ResourceAction.Cancel)
                {
                    if (Interlocked.Increment(ref calls) == 2) both.TrySetResult(true);
                    await both.Task.WaitAsync(ct); resource.State = "cancelled";
                }
                return resource;
            }
        }
        private static async Task StartupCommitWindow()
        {
            string root = Root(); Directory.CreateDirectory(Path.Combine(root, "workspace"));
            try
            {
                await using var manager = Manager(root); var store = new FileTaskStore(Path.Combine(root, "tasks"));
                var start = Start(root); string id = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                await using (var interrupted = Runtime(new ScriptedModel(HarnessTests.Call("start_process", "{\"profile\":\"hold\"}")), new StartCommitFailure(store), manager))
                    await Check.ThrowsAsync<CheckpointWriteException>(() => interrupted.ExecuteAsync(start, default));
                var saved = (await store.LoadAsync(id, default))!;
                Check.Equal(OperationStatus.Started, saved.Operations.Single().Status);
                string sessionId = "process_" + TaskJson.Hash(id + ":" + saved.Operations.Single().Id).Substring(0, 32);
                var original = manager.Find(sessionId, id); int pid = original.Process!.Pid!.Value;
                await using var recovered = Runtime(new ScriptedModel(HarnessTests.Answer()), store, manager);
                var blocked = (await recovered.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = id }, default)).State;
                Check.Equal(WaitReason.Reconciliation, blocked.Wait); Check.Equal(1, blocked.Budget.ModelCalls);
                var reconciled = (await recovered.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Reconcile, TaskId = id,
                    OperationId = saved.Operations.Single().Id, ReconciledOutcome = new ToolOutcome { Status = ToolCallStatus.Succeeded,
                        Effect = EffectState.Confirmed, Output = JsonSerializer.Serialize(original, TaskJson.Options), Resources = { original } } }, default)).State;
                Check.Equal(pid, reconciled.Resources.Single().Process!.Pid); Check.Equal(2, reconciled.Budget.ModelCalls);
                var cancelled = (await recovered.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Cancel, TaskId = id }, default)).State;
                Check.True(cancelled.Resources.Single().Process!.TerminationConfirmed);
            }
            finally { Directory.Delete(root, true); }
        }
        private sealed class StartCommitFailure : ITaskStore
        {
            private readonly ITaskStore inner;
            public StartCommitFailure(ITaskStore inner) { this.inner = inner; }
            public Task<AgentTaskState?> LoadAsync(string id, CancellationToken ct) => inner.LoadAsync(id, ct);
            public Task<TaskResponseReference?> ResolveResponseAsync(string id, CancellationToken ct) => inner.ResolveResponseAsync(id, ct);
            public async Task<ITaskLease> AcquireAsync(string id, CancellationToken ct) => new Lease(await inner.AcquireAsync(id, ct));
            private sealed class Lease : ITaskLease
            {
                private readonly ITaskLease inner;
                public Lease(ITaskLease inner) { this.inner = inner; }
                public Task<AgentTaskState?> LoadAsync(CancellationToken ct) => inner.LoadAsync(ct);
                public Task CommitAsync(AgentTaskState state, long revision, CancellationToken ct)
                {
                    if (state.Operations.Any(o => o.Call.Name == "start_process" && o.Status == OperationStatus.Finished))
                        throw new CheckpointWriteException(new IOException("Injected task commit failure after OS startup."));
                    return inner.CommitAsync(state, revision, ct);
                }
                public ValueTask DisposeAsync() => inner.DisposeAsync();
            }
        }
    }
}
