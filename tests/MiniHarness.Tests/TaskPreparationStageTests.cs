using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;

namespace MiniHarness.Tests
{
    internal static partial class TaskStageTests
    {
        private static async Task PreparationEvidenceScenario()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miniharness-stage45-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileTaskStore(Path.Combine(directory, "checkpoints"));
                var prepare = new BlockingPreparation(); var model = new ScriptedModel(HarnessTests.Answer());
                var binding = new ModelBinding { Model = "test", Protocol = "Responses", ConfigurationVersion = "v1" };
                await using (var runtime = new TaskRuntime(model, store, new HarnessOptions(), binding, preparation: prepare))
                {
                    var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request(), Facts = new TaskFacts { Goal = "original goal" } };
                    string id = "task_" + TaskJson.Hash(start.Id).Substring(0, 32);
                    var running = runtime.ExecuteAsync(start, CancellationToken.None);
                    await prepare.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    binding.Model = "changed outside runtime"; binding.ConfigurationVersion = "v2";
                    var pausing = runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Pause, TaskId = id }, CancellationToken.None);
                    var stopped = await running; await pausing;
                    Check.Equal(AgentTaskStatus.Paused, stopped.State.Status); Check.Equal(0, model.Inputs.Count);
                    Check.Equal("original goal", stopped.State.Facts.Goal); Check.Equal("test", stopped.State.History.Single().Message.Text);
                    var resumed = await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = id }, CancellationToken.None);
                    Check.Equal(1, model.Inputs.Count); Check.Equal("test", model.Inputs[0].Model);
                    Check.Equal("v1", resumed.State.Steps.Last().Model.ConfigurationVersion);
                    Check.Equal("original goal", resumed.State.Facts.Goal);
                    Check.Equal(TurnStatus.Completed, resumed.State.Turns.Single().Status);
                    Check.Equal(AgentTaskStatus.Waiting, resumed.State.Status);
                    Check.True(resumed.State.Steps.Last().PreparedRequest!.Messages.Any(m => m.Text.Contains("original goal", StringComparison.Ordinal)));
                }
                string workspace = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspace);
                string source = Path.Combine(workspace, "source.txt"); await File.WriteAllTextAsync(source, "version one");
                var evidenceModel = new ScriptedModel(HarnessTests.Answer("candidate"));
                await using (var strict = new TaskRuntime(evidenceModel, store, new HarnessOptions(),
                    new ModelBinding { Model = "test", Protocol = "Responses" }, completion: new EvidenceCompletionPolicy()))
                {
                    var pending = await strict.ExecuteAsync(new TaskCommand
                    {
                        Kind = TaskCommandKind.Start, Request = HarnessTests.Request(),
                        Facts = new TaskFacts { Goal = "verified goal", Workspace = new WorkspaceScope { RootPath = workspace }, RequiredEvidence = { "tests" } }
                    }, CancellationToken.None);
                    Check.Equal(WaitReason.Evidence, pending.State.Wait);
                    string originalRevision = pending.State.Facts.Workspace.Revision;
                    await File.WriteAllTextAsync(source, "version two from external editor");
                    var stale = await strict.ExecuteAsync(new TaskCommand
                    {
                        Kind = TaskCommandKind.Evidence, TaskId = pending.State.Id,
                        Evidence = { new EvidenceRecord { Requirement = "tests", Passed = true, ExitCode = 0, WorkspaceRevision = originalRevision } }
                    }, CancellationToken.None);
                    Check.Equal(WaitReason.Evidence, stale.State.Wait);
                    Check.True(originalRevision != stale.State.Facts.Workspace.Revision);
                    var complete = await strict.ExecuteAsync(new TaskCommand
                    {
                        Kind = TaskCommandKind.Evidence, TaskId = pending.State.Id,
                        Evidence = { new EvidenceRecord
                        {
                            Requirement = "tests", Passed = true, ExitCode = 0, Output = "verified current workspace",
                            WorkspaceRevision = stale.State.Facts.Workspace.Revision,
                            Artifacts = { new ArtifactReference { Id = "test-log", Location = "memory://test-log", ContentHash = "verified-log-hash" } }
                        } }
                    }, CancellationToken.None);
                    Check.Equal(AgentTaskStatus.Completed, complete.State.Status);
                    Check.Equal(TurnStatus.Completed, complete.State.Turns.Single().Status);
                    Check.Equal(1, evidenceModel.Inputs.Count); Check.Equal(1, complete.State.Budget.ModelCalls);
                    Check.Equal(complete.State.Id, complete.State.Artifacts.Single().OwnerTaskId);
                    Check.Equal(2, complete.State.Evidence.Count);
                    var loaded = (await store.LoadAsync(complete.State.Id, CancellationToken.None))!;
                    Check.Equal(AgentTaskStatus.Completed, loaded.Status); Check.Equal("verified goal", loaded.Facts.Goal);
                    Check.Equal(complete.State.Artifacts.Single().ContentHash, loaded.Artifacts.Single().ContentHash);
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private sealed class BlockingPreparation : IContextPreparation
        {
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int calls;
            public async Task<PreparedContext> PrepareAsync(ContextPreparation input, CancellationToken cancellationToken)
            {
                if (++calls == 1)
                {
                    input.Facts.Goal = "mutation of isolated view"; input.History[0].Message.Text = "mutation of isolated history";
                    Entered.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                return new PreparedContext { Messages = input.History.Select(h => TaskJson.Clone(h.Message)).ToList() };
            }
        }
    }
}
