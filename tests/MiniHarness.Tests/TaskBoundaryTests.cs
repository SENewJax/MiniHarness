using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;

namespace MiniHarness.Tests
{
    internal static partial class TaskStageTests
    {
        private static ModelBinding TestBinding() => new ModelBinding { Model = "test", Protocol = "Responses" };
        private static async Task ControlBoundaryScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-controls-" + Guid.NewGuid().ToString("N"));
            var store = new FileTaskStore(directory); var prepare = new ControlledPreparation();
            var pause = new TaskCommand { Kind = TaskCommandKind.Pause };
            try
            {
                await using (var runtime = new TaskRuntime(new ScriptedModel(HarnessTests.Answer(), HarnessTests.Answer(), HarnessTests.Answer()),
                    store, new HarnessOptions(), TestBinding(), preparation: prepare))
                {
                    prepare.BlockNext();
                    var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() };
                    pause.TaskId = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                    var first = runtime.ExecuteAsync(start, CancellationToken.None);
                    await prepare.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var pausing = runtime.ExecuteAsync(pause, CancellationToken.None);
                    var duplicate = runtime.ExecuteAsync(pause, CancellationToken.None);
                    await pausing.WaitAsync(TimeSpan.FromSeconds(10)); await first;
                    Check.Equal((await pausing).Receipt.AcceptedRevision, (await duplicate).Receipt.AcceptedRevision);
                    var originalShape = new { pause.Id, pause.TaskId, pause.Kind, pause.Request, pause.ForkedFromResponseId, pause.Facts,
                        pause.OperationId, pause.Approve, pause.Results, pause.Evidence, pause.ReconciledOutcome };
                    Check.Equal(TaskJson.Hash(JsonSerializer.Serialize(originalShape, TaskJson.Options)), (await pausing).Receipt.Fingerprint);
                    await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = pause.TaskId }, CancellationToken.None);
                    prepare.BlockNext();
                    var second = runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Input, TaskId = pause.TaskId, Request = HarnessTests.Request() }, CancellationToken.None);
                    await prepare.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await runtime.ExecuteAsync(pause, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    var conflict = TaskJson.Clone(pause); conflict.Kind = TaskCommandKind.Cancel;
                    await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(conflict, CancellationToken.None));
                    Check.Equal(1, prepare.Cancellations); Check.True(!second.IsCompleted);
                    prepare.Release.TrySetResult(true); Check.Equal(TurnStatus.Completed, (await second).State.Turns.Last().Status);
                    prepare.BlockNext();
                    var third = runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Input, TaskId = pause.TaskId, Request = HarnessTests.Request() }, CancellationToken.None);
                    await prepare.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Pause, TaskId = pause.TaskId }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    Check.Equal(AgentTaskStatus.Paused, (await third).State.Status);
                    Check.Equal(TurnStatus.Completed, (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = pause.TaskId }, CancellationToken.None)).State.Turns.Last().Status);
                }
                prepare = new ControlledPreparation(); prepare.BlockNext();
                await using (var restarted = new TaskRuntime(new ScriptedModel(HarnessTests.Answer()), store, new HarnessOptions(), TestBinding(), preparation: prepare))
                {
                    var input = restarted.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Input, TaskId = pause.TaskId, Request = HarnessTests.Request() }, CancellationToken.None);
                    await prepare.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await restarted.ExecuteAsync(pause, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    Check.Equal(0, prepare.Cancellations); prepare.Release.TrySetResult(true);
                    Check.Equal(TurnStatus.Completed, (await input).State.Turns.Last().Status);
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private sealed class ControlledPreparation : IContextPreparation
        {
            private bool block;
            public int Cancellations { get; private set; }
            public TaskCompletionSource<bool> Entered { get; private set; } = Signal();
            public TaskCompletionSource<bool> Release { get; private set; } = Signal();
            private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public void BlockNext() { block = true; Entered = Signal(); Release = Signal(); }
            public async Task<PreparedContext> PrepareAsync(ContextPreparation input, CancellationToken ct)
            {
                if (block)
                {
                    block = false; Entered.TrySetResult(true);
                    try { await Release.Task.WaitAsync(ct); }
                    catch (OperationCanceledException) { Cancellations++; throw; }
                }
                return new PreparedContext { Messages = input.History.Select(h => TaskJson.Clone(h.Message)).ToList() };
            }
        }
        private static async Task ReplanBoundaryScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-replan-" + Guid.NewGuid().ToString("N"));
            string workspace = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspace);
            string source = Path.Combine(workspace, "source.txt"); await File.WriteAllTextAsync(source, "before");
            try
            {
                var edit = new FileScenarioTool("edit", async () => { await File.WriteAllTextAsync(source, "after"); return "edited"; });
                var inspect = new FileScenarioTool("inspect", () => File.ReadAllTextAsync(source));
                var batch = HarnessTests.Call("edit", "{}", "edit-old");
                batch.Message.ToolCalls.Add(new ToolCall { Id = "inspect-old", Name = "inspect", Arguments = "{}" });
                var model = new ScriptedModel(batch, HarnessTests.Call("inspect", "{}", "inspect-new"), HarnessTests.Answer("verified"), HarnessTests.Answer("next"));
                await using var runtime = new TaskRuntime(model, new FileTaskStore(Path.Combine(directory, "state")), new HarnessOptions(), TestBinding(),
                    tools: new ToolRegistry(new ITool[] { edit, inspect }), policy: new InspectApproval());
                var pending = await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request(),
                    Facts = new TaskFacts { Workspace = new WorkspaceScope { RootPath = workspace } } }, CancellationToken.None);
                Check.Equal(1, edit.Count); Check.Equal(0, inspect.Count); Check.Equal(WaitReason.Approval, pending.State.Wait);
                var stale = pending.State.Operations.Single(o => o.Call.Id == "inspect-old");
                var fresh = pending.State.Operations.Single(o => o.Call.Id == "inspect-new");
                Check.Equal(ToolCallStatus.Rejected, stale.Outcome!.Status); Check.Equal(EffectState.None, stale.Effect);
                Check.True(pending.State.Steps.Single(s => s.Id == stale.StepId).Capabilities.Workspace.Revision !=
                    pending.State.Steps.Single(s => s.Id == fresh.StepId).Capabilities.Workspace.Revision);
                Check.True(model.Inputs[1].Messages.Any(m => m.Role == "tool" && m.ToolCallId == "inspect-old" && m.Text.Contains("invalidated")));
                await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Approval,
                    TaskId = pending.State.Id, OperationId = stale.Id, Approve = true }, CancellationToken.None));
                var complete = await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Approval, TaskId = pending.State.Id, OperationId = fresh.Id, Approve = true }, CancellationToken.None);
                Check.Equal(1, edit.Count); Check.Equal(1, inspect.Count); Check.Equal(TurnStatus.Completed, complete.State.Turns.Last().Status);
                Check.Equal(TurnStatus.Completed, (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Input, TaskId = pending.State.Id,
                    Request = HarnessTests.Request() }, CancellationToken.None)).State.Turns.Last().Status);
            }
            finally { Directory.Delete(directory, true); }
        }
        private sealed class FileScenarioTool : ITool
        {
            private readonly Func<Task<string>> run;
            public int Count { get; private set; }
            public ToolDefinition Definition { get; }
            public FileScenarioTool(string name, Func<Task<string>> run)
            {
                this.run = run;
                using var json = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
                Definition = new ToolDefinition { Name = name, Parameters = json.RootElement.Clone() };
            }
            public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct) { Count++; return run(); }
        }
        private sealed class InspectApproval : IContextualToolPolicy
        {
            public string Version => "inspect-approval-v1";
            public Task<ToolAdmission> CheckAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct)
                => Task.FromResult(new ToolAdmission { Allowed = call.Name != "inspect" || context.Approval == ApprovalState.Approved,
                    NeedsUser = call.Name == "inspect" && context.Approval == ApprovalState.None, Reason = "inspect approval required" });
        }
    }
}
