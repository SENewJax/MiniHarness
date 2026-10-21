using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Harness.Delivery;

namespace MiniHarness.Tests
{
    internal static class V4BoundaryTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v4-delivery] generated package corruption or deletion cannot cross final commit", PackageIntegrity));
            tests.Add(("[v4-progress] review rejection preserves stop while approval permits local settlement", StoppedReview));
            tests.Add(("[v4-artifact] explicit protection includes ignored directories and nested outputs", ExplicitProtection));
        }

        private static async Task PackageIntegrity()
        {
            foreach (string phase in new[] { "delivery_sealed", "delivery_before_commit" })
            foreach (string name in new[] { "manifest.json", "changes.json", "delivery.md" })
            foreach (bool delete in new[] { false, true })
            {
                string root = V4Tests.Root();
                try
                {
                    var store = new FileTaskStore(Path.Combine(root, "tasks")); var model = new V4Tests.Model();
                    await using var runtime = new TaskRuntime(model, store, new HarnessOptions { MaxModelCalls = 1 },
                        V3CompactionTests.Binding("same"), delivery: V4Tests.Services(root));
                    string id = (await runtime.ExecuteAsync(V4Tests.Start(root), default)).State.Id;
                    bool injected = false;
                    runtime.FaultInjection = p =>
                    {
                        if (p != phase) return;
                        var saved = store.LoadAsync(id, default).GetAwaiter().GetResult()!;
                        var reference = saved.Acceptance!.Delivery!.Manifest!;
                        string path = Path.Combine(root, "sealed", id, reference.Location);
                        if (name != "manifest.json")
                        {
                            var manifest = JsonSerializer.Deserialize<DeliveryManifest>(File.ReadAllBytes(path), TaskJson.Options)!;
                            reference = manifest.Artifacts.Single(a => a.Id == name);
                            path = Path.Combine(root, "sealed", id, reference.Location);
                        }
                        if (delete) File.Delete(path); else File.WriteAllText(path, "damaged sealed content");
                        injected = true;
                    };
                    var state = (await runtime.ExecuteAsync(V4Tests.Import(root, id), default)).State;
                    Check.True(injected); Check.Equal(WaitReason.Infrastructure, state.Wait);
                    Check.True(state.Status != AgentTaskStatus.Completed);
                    Check.True(state.Acceptance!.Delivery!.Status != DeliveryStatus.Committed);
                    Check.True(!state.Events.Any(e => e.Type == "TaskCompleted"));
                    Check.Equal(1, model.Calls);
                    var persisted = (await store.LoadAsync(id, default))!;
                    Check.Equal(WaitReason.Infrastructure, persisted.Wait);
                }
                finally { Directory.Delete(root, true); }
            }
        }

        private static async Task StoppedReview()
        {
            foreach (bool approve in new[] { false, true })
            {
                string root = V4Tests.Root();
                try
                {
                    var model = new V4Tests.Model(); var store = new FileTaskStore(Path.Combine(root, "tasks"));
                    await using var runtime = new TaskRuntime(model, store, new HarnessOptions { MaxModelCalls = 10 },
                        V3CompactionTests.Binding("same"), delivery: V4Tests.Services(root));
                    var command = V4Tests.Start(root, true);
                    command.Facts!.AcceptanceSpec!.Criteria.RemoveAt(0);
                    command.Facts.AcceptanceSpec.Criteria.Insert(0, new AcceptanceCriterion
                        { Id = "tests", Kind = CriterionKind.VerificationProfile, Profile = "tests", VerifierFingerprint = "v1" });
                    command.Facts.AcceptanceSpec.ContentHash = "";
                    var state = (await runtime.ExecuteAsync(command, default)).State;
                    Check.Equal(WaitReason.NoProgress, state.Wait); Check.Equal(3, model.Calls);
                    state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.BeginVerification, TaskId = state.Id,
                        Acceptance = new AcceptanceCommand { CriterionId = "tests", AttemptId = "attempt_1" } }, default)).State;
                    state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.SubmitVerification, TaskId = state.Id,
                        Acceptance = new AcceptanceCommand { Evidence = new EvidenceEnvelope { AttemptId = "attempt_1", Status = VerificationAttemptStatus.Passed,
                            Evidence = new EvidenceRecord { TaskId = state.Id, OperationId = "external:attempt_1", Requirement = "verification:tests",
                                WorkspaceRevision = state.Facts.Workspace.Revision, VerifierFingerprint = "v1", Passed = true, ExitCode = 0 } } } }, default)).State;
                    Check.Equal(WaitReason.Review, state.Wait); Check.Equal(3, model.Calls);
                    var review = new TaskCommand { Kind = TaskCommandKind.ReviewDelivery, TaskId = state.Id,
                        Acceptance = new AcceptanceCommand { Review = new ReviewDecision
                            { CandidateFingerprint = state.Acceptance!.Candidate!.Fingerprint, Accept = approve, Comment = "host decision" } } };
                    state = (await runtime.ExecuteAsync(review, default)).State;
                    Check.Equal(3, model.Calls);
                    if (approve) Check.Equal(AgentTaskStatus.Completed, state.Status);
                    else
                    {
                        Check.Equal(WaitReason.NoProgress, state.Wait); Check.True(state.Acceptance!.Progress.Stopped);
                        Check.Equal(0, state.Acceptance.Progress.ExtraAttempts);
                        await runtime.ExecuteAsync(review, default); Check.Equal(3, model.Calls);
                        state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = state.Id }, default)).State;
                        Check.Equal(WaitReason.NoProgress, state.Wait); Check.Equal(3, model.Calls);
                        state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ContinueAfterNoProgress, TaskId = state.Id,
                            Acceptance = new AcceptanceCommand { ExpectedHash = state.Acceptance!.Progress.StopFingerprint, Reason = "bounded review revision" } }, default)).State;
                        Check.Equal(WaitReason.Review, state.Wait); Check.Equal(3, model.Calls);
                        for (int calls = 4; calls <= 5; calls++)
                        {
                            state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ReviewDelivery, TaskId = state.Id,
                                Acceptance = new AcceptanceCommand { Review = new ReviewDecision
                                    { CandidateFingerprint = state.Acceptance!.Candidate!.Fingerprint, Accept = false } } }, default)).State;
                            Check.Equal(calls, model.Calls); Check.Equal(WaitReason.Review, state.Wait);
                        }
                        Check.True(state.Acceptance!.Progress.Stopped); Check.Equal(0, state.Acceptance.Progress.ExtraAttempts);
                        state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.ReviewDelivery, TaskId = state.Id,
                            Acceptance = new AcceptanceCommand { Review = new ReviewDecision
                                { CandidateFingerprint = state.Acceptance!.Candidate!.Fingerprint, Accept = false } } }, default)).State;
                        Check.Equal(5, model.Calls); Check.Equal(WaitReason.NoProgress, state.Wait);
                    }
                }
                finally { Directory.Delete(root, true); }
            }
        }

        private static async Task ExplicitProtection()
        {
            foreach (string scope in new[] { "bin", "obj", "tests" })
            foreach (string change in new[] { "modify", "delete", "add" })
            {
                string root = V4Tests.Root();
                try
                {
                    string directory = Path.Combine(root, "workspace", scope, "bin", "obj"); Directory.CreateDirectory(directory);
                    string file = Path.Combine(directory, "protected.txt"); File.WriteAllText(file, "original");
                    var model = new V4Tests.Model();
                    await using var runtime = new TaskRuntime(model, new FileTaskStore(Path.Combine(root, "tasks")), new HarnessOptions { MaxModelCalls = 1 },
                        V3CompactionTests.Binding("same"), delivery: V4Tests.Services(root));
                    var command = V4Tests.Start(root); command.Facts!.AcceptanceSpec!.ProtectedPaths.Add(scope);
                    command.Facts.AcceptanceSpec.ContentHash = "";
                    var state = (await runtime.ExecuteAsync(command, default)).State;
                    Check.Equal(1, state.Acceptance!.Protection.Files.Count);
                    if (change == "delete") File.Delete(file);
                    else if (change == "add") File.WriteAllText(Path.Combine(directory, "new.txt"), "new");
                    else File.WriteAllText(file, "modified");
                    state = (await runtime.ExecuteAsync(V4Tests.Import(root, state.Id), default)).State;
                    Check.True(state.Status != AgentTaskStatus.Completed); Check.True(state.Acceptance!.ProtectionViolations.Count > 0);
                    Check.Equal(1, model.Calls);
                }
                finally { Directory.Delete(root, true); }
            }
            string controls = V4Tests.Root();
            try
            {
                string workspace = Path.Combine(controls, "workspace");
                Directory.CreateDirectory(Path.Combine(workspace, "bin"));
                var spec = new AcceptanceSpec { ProtectedPaths = { "bin" } };
                var baseline = await SafeFiles.Capture(workspace, spec, 1024, default);
                Check.Equal(0, baseline.Files.Count);
                File.WriteAllText(Path.Combine(workspace, "bin", "new.txt"), "new");
                Check.Equal(1, (await SafeFiles.Check(workspace, spec, baseline, 1024, default)).Count);
                spec.AllowNewFilesUnder.Add("bin");
                Check.Equal(0, (await SafeFiles.Check(workspace, spec, baseline, 1024, default)).Count);
                spec.ProtectedPaths.Add("bin/new.txt");
                baseline = await SafeFiles.Capture(workspace, spec, 1024, default);
                Check.Equal(1, baseline.Files.Count);
                Check.Equal(0, (await SafeFiles.Check(workspace, spec, baseline, 1024, default)).Count);
                Check.True(!SafeFiles.Files(workspace).Any(f => Path.GetFileName(f) == "new.txt"));
                File.WriteAllText(Path.Combine(workspace, "bin", "new.txt"), "changed original");
                Check.Equal(1, (await SafeFiles.Check(workspace, spec, baseline, 1024, default)).Count);
            }
            finally { Directory.Delete(controls, true); }
        }
    }
}
