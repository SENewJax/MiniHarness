using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Harness.Acceptance;
using MiniHarness.Harness.Delivery;

namespace MiniHarness.Tests
{
    internal static class V4Tests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v4-acceptance] contract fingerprints, latest generations and unique settlement", Evidence));
            tests.Add(("[v4-acceptance] V3 constructor signatures remain callable and legacy tasks keep their completion semantics", BinaryCompatibility));
            tests.Add(("[v4-artifact] protected baseline, immutable bytes, ownership and portable verification", Artifacts));
            tests.Add(("[v4-progress] settled steps count once across reload, loops stop and controls do not reset", Progress));
            tests.Add(("[v4-delivery] exhausted model budget permits local sealed delivery and isolated bundle audit", Automatic));
            tests.Add(("[v4-delivery] review binds the candidate and cannot replace failed automatic checks", Review));
            tests.Add(("[v4-recovery] saved sealing windows resume without replay or extra fees", Recovery));
            tests.Add(("[v4-recovery] admitted control wins final commit and protects completed history", Controls));
            tests.Add(("[v4-delivery] child completion preserves parent evidence isolation and cumulative cost", Children));
            tests.Add(("[v4-progress] amendments retain stops and explicit continuation grants exactly its bounded attempts", Management));
        }
        internal static AcceptanceSpec Spec(bool review = false)
        {
            var spec = new AcceptanceSpec { Criteria = { new AcceptanceCriterion { Id = "result", Kind = CriterionKind.ArtifactPresence, ArtifactName = "result.txt" } } };
            if (review) spec.Criteria.Add(new AcceptanceCriterion { Id = "review", Kind = CriterionKind.HumanReview });
            AcceptanceBinding.Validate(spec); return spec;
        }
        internal sealed class Host : IAcceptanceAuthorizer
        {
            public string PolicyVersion => "v4-1";
            public string Actor => "test-host";
            public bool Allows(AgentTaskState task, TaskCommandKind action) => true;
            public bool ValidSpec(AgentTaskState task, AcceptanceSpec spec) => true;
        }
        internal sealed class Model : IModelProvider
        {
            public int Calls { get; private set; }
            public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct) { Calls++; return Task.FromResult(HarnessTests.Answer("Completed work; please verify.")); }
        }
        internal static DeliveryServices Services(string root) => new DeliveryServices { Authorizer = new Host(),
            Artifacts = new FileArtifactStore(Path.Combine(root, "sealed"), new[] { Path.Combine(root, "workspace") }, new DeliveryOptions()), Options = new DeliveryOptions() };
        internal static TaskCommand Start(string root, bool review = false) => new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request(),
            Facts = new TaskFacts { Goal = "Deliver result", AcceptanceSpec = Spec(review), Workspace = new WorkspaceScope { RootPath = Path.Combine(root, "workspace") } } };
        internal static TaskCommand Import(string root, string taskId) => new TaskCommand { Kind = TaskCommandKind.RegisterDeliveryArtifact, TaskId = taskId,
            Acceptance = new AcceptanceCommand { Artifact = new ArtifactReference { Id = "result.txt", Location = Path.Combine(root, "workspace", "result.txt"),
                ContentHash = SafeFiles.Hash(File.ReadAllBytes(Path.Combine(root, "workspace", "result.txt"))) } } };
        internal static string Root()
        { string root = V3CompactionTests.Root(); Directory.CreateDirectory(Path.Combine(root, "workspace")); File.WriteAllText(Path.Combine(root, "workspace", "result.txt"), "42"); return root; }
        private static async Task BinaryCompatibility()
        {
            string root = Root();
            try
            {
                var signature = typeof(TaskRuntime).GetConstructors().Single(c => c.GetParameters().Length == 18);
                var arguments = new object?[18]; arguments[0] = new Model(); arguments[1] = new FileTaskStore(Path.Combine(root, "tasks"));
                arguments[2] = new HarnessOptions(); arguments[3] = V3CompactionTests.Binding("same");
                await using var runtime = (TaskRuntime)signature.Invoke(arguments);
                var state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }, default)).State;
                Check.Equal(TurnStatus.Completed, state.Turns.Single().Status); Check.True(state.Acceptance == null);
                var initializerSignature = typeof(MiniHarness.Server.HostTaskInitializer).GetConstructors().Single(c => c.GetParameters().Length == 3);
                var initializer = (ITaskInitializer)initializerSignature.Invoke(new object?[] { new MiniHarness.Server.ServerOptions(), new MiniHarness.Server.CodingTaskSetup(), null });
                var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }; initializer.Initialize(start);
                Check.True(start.Facts!.AcceptanceSpec == null);
            }
            finally { Directory.Delete(root, true); }
        }
        private static Task Evidence()
        {
            var spec = new AcceptanceSpec { Criteria = { new AcceptanceCriterion { Id = "test", Kind = CriterionKind.VerificationProfile, Profile = "tests", VerifierFingerprint = "v1" } } };
            AcceptanceBinding.Validate(spec); string hash = spec.ContentHash; spec.Criteria.Reverse(); Check.Equal(hash, AcceptanceBinding.SpecHash(spec));
            var task = new AgentTaskState { Facts = new TaskFacts { AcceptanceSpec = spec }, Acceptance = new AcceptanceState() };
            EvidenceEnvelope Result(string id, bool passed) => new EvidenceEnvelope { AttemptId = id, Source = "host:test", Status = passed ? VerificationAttemptStatus.Passed : VerificationAttemptStatus.Failed,
                Evidence = new EvidenceRecord { TaskId = task.Id, WorkspaceRevision = task.Facts.Workspace.Revision, VerifierFingerprint = "v1", Passed = passed, ExitCode = passed ? 0 : 1 } };
            var first = EvidenceAdmission.Begin(task, "test", "a", ""); var firstResult = Result("a", true);
            EvidenceAdmission.Settle(task, firstResult, false); Check.True(AcceptanceEvaluator.Evaluate(task).CanFinalize);
            var second = EvidenceAdmission.Begin(task, "test", "b", ""); Check.True(!AcceptanceEvaluator.Evaluate(task).CanFinalize);
            EvidenceAdmission.Settle(task, Result("b", false), false);
            EvidenceAdmission.Settle(task, firstResult, false); Check.Equal(AssessmentStatus.Failed, AcceptanceEvaluator.Evaluate(task).Criteria.Single().Status);
            Check.True(first.Generation < second.Generation);
            bool conflict = false; try { EvidenceAdmission.Settle(task, Result("a", false), false); } catch (TaskConflictException) { conflict = true; } Check.True(conflict);
            var unknown = EvidenceAdmission.Begin(task, "test", "c", ""); unknown.Status = VerificationAttemptStatus.Unknown;
            conflict = false; try { EvidenceAdmission.Begin(task, "test", "d", ""); } catch (TaskConflictException) { conflict = true; } Check.True(conflict);
            conflict = false; try { EvidenceAdmission.Settle(task, Result("c", true), false); } catch (TaskConflictException) { conflict = true; } Check.True(conflict);
            EvidenceAdmission.Settle(task, Result("c", true), true); Check.True(AcceptanceEvaluator.Evaluate(task).CanFinalize);
            var cancelled = EvidenceAdmission.Begin(task, "test", "e", ""); cancelled.Status = VerificationAttemptStatus.Cancelled;
            Check.Equal(AssessmentStatus.Failed, AcceptanceEvaluator.Evaluate(task).Criteria.Single().Status);
            return Task.CompletedTask;
        }
        private static async Task Artifacts()
        {
            string root = Root();
            try
            {
                var options = new DeliveryOptions(); var store = Services(root).Artifacts; string id = "task_" + Guid.NewGuid().ToString("N");
                var source = Import(root, id).Acceptance!.Artifact!; var frozen = await store.ImportAsync(id, source, default);
                File.WriteAllText(source.Location, "different"); Check.Equal("42", System.Text.Encoding.UTF8.GetString(await store.ReadAsync(id, frozen, default)));
                await Check.ThrowsAsync<ArgumentException>(() => store.ReadAsync("task_" + Guid.NewGuid().ToString("N"), frozen, default));
                var spec = Spec(); spec.ProtectedPaths.Add("result.txt");
                var baseline = await SafeFiles.Capture(Path.Combine(root, "workspace"), spec, options.MaxArtifactBytes, default);
                File.WriteAllText(source.Location, "changed");
                Check.Equal(1, (await SafeFiles.Check(Path.Combine(root, "workspace"), spec, baseline, options.MaxArtifactBytes, default)).Count);
                bool rejected = false; try { SafeFiles.Within(root, "../outside"); } catch (ArgumentException) { rejected = true; } Check.True(rejected);
                await File.WriteAllTextAsync(source.Location, "42");
                var services = Services(root); services.Options.MaxArtifacts = 3;
                await using var runtime = new TaskRuntime(new Model(), new FileTaskStore(Path.Combine(root, "tasks")), new HarnessOptions { MaxModelCalls = 1 }, V3CompactionTests.Binding("same"), delivery: services);
                var state = (await runtime.ExecuteAsync(Start(root), default)).State;
                for (int index = 0; index < 3; index++)
                { var import = Import(root, state.Id); import.Acceptance!.Artifact!.Id = "extra-" + index; state = (await runtime.ExecuteAsync(import, default)).State; }
                var overQuota = Import(root, state.Id); overQuota.Acceptance!.Artifact!.Id = "extra-four";
                await Check.ThrowsAsync<IOException>(() => runtime.ExecuteAsync(overQuota, default));
                Check.Equal(3, (await runtime.GetAsync(state.Id, default))!.Acceptance!.FrozenArtifacts.Count);
            }
            finally { Directory.Delete(root, true); }
        }
        private static Task Progress()
        {
            var task = new AgentTaskState { Facts = new TaskFacts { AcceptanceSpec = Spec() }, Acceptance = new AcceptanceState() };
            task.Acceptance.Progress.Tracking = true;
            var options = new DeliveryOptions { NoProgressLimit = 3 };
            for (int index = 0; index < 3; index++)
            {
                var step = new TaskStep { Status = StepStatus.Responded }; ProgressPolicy.Sample(task, step, options);
                task = TaskJson.Clone(task); ProgressPolicy.Sample(task, step, options);
            }
            Check.True(task.Acceptance!.Progress.Stopped); Check.Equal(3, task.Acceptance.Progress.EvaluatedSteps.Count);
            Check.Equal(3, task.Acceptance.Progress.Unchanged); return Task.CompletedTask;
        }
        private static async Task Management()
        {
            string root = Root();
            try
            {
                var spec = new AcceptanceSpec { Criteria = { new AcceptanceCriterion { Id = "tests", Kind = CriterionKind.VerificationProfile, Profile = "tests", VerifierFingerprint = "v1" } } };
                AcceptanceBinding.Validate(spec);
                var services = Services(root); services.Authorizer = new MiniHarness.Server.HostAcceptancePolicy(new MiniHarness.Server.DeliveryServerOptions
                    { Enabled = true, AllowContinue = true, AllowAmendment = true, Template = TaskJson.Clone(spec) });
                var model = new Model(); await using var runtime = new TaskRuntime(model, new FileTaskStore(Path.Combine(root, "tasks")),
                    new HarnessOptions { MaxModelCalls = 10 }, V3CompactionTests.Binding("same"), delivery: services);
                var start = Start(root); start.Facts!.AcceptanceSpec = spec;
                var state = (await runtime.ExecuteAsync(start, default)).State; Check.Equal(WaitReason.NoProgress, state.Wait); Check.Equal(3, model.Calls);
                state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Resume, TaskId = state.Id }, default)).State; Check.Equal(3, model.Calls);
                state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Steer, TaskId = state.Id, Steering = new TaskSteering { Text = "Try again" } }, default)).State;
                Check.Equal(3, model.Calls); string stop = state.Acceptance!.Progress.StopFingerprint;
                var continuation = new TaskCommand { Kind = TaskCommandKind.ContinueAfterNoProgress, TaskId = state.Id,
                    Acceptance = new AcceptanceCommand { ExpectedHash = "old", Reason = "bounded retry" } };
                await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(continuation, default));
                continuation.Id = Guid.NewGuid().ToString("N"); continuation.Acceptance.ExpectedHash = stop;
                state = (await runtime.ExecuteAsync(continuation, default)).State;
                Check.Equal(5, model.Calls); Check.Equal(WaitReason.NoProgress, state.Wait); Check.Equal(1, state.Acceptance!.Progress.Continued);
                await runtime.ExecuteAsync(continuation, default); Check.Equal(5, model.Calls);
                continuation = TaskJson.Clone(continuation); continuation.Id = Guid.NewGuid().ToString("N");
                await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(continuation, default));
                continuation.Id = Guid.NewGuid().ToString("N"); continuation.Acceptance!.ExpectedHash = state.Acceptance.Progress.StopFingerprint;
                state = (await runtime.ExecuteAsync(continuation, default)).State; Check.Equal(7, model.Calls);
                continuation.Id = Guid.NewGuid().ToString("N"); continuation.Acceptance.ExpectedHash = state.Acceptance!.Progress.StopFingerprint;
                await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(continuation, default));
                var weakened = Spec(); weakened.Revision = 2; weakened.ContentHash = "";
                await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.AmendAcceptanceSpec, TaskId = state.Id,
                    Acceptance = new AcceptanceCommand { ExpectedHash = state.Facts.AcceptanceSpec!.ContentHash, Reason = "remove host requirement", Spec = weakened } }, default));
                var amended = TaskJson.Clone(spec); amended.Revision = 2; amended.ContentHash = "";
                amended.Criteria.Add(new AcceptanceCriterion { Id = "optional", Kind = CriterionKind.ArtifactPresence, ArtifactName = "optional", Required = false });
                state = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.AmendAcceptanceSpec, TaskId = state.Id,
                    Acceptance = new AcceptanceCommand { ExpectedHash = state.Facts.AcceptanceSpec!.ContentHash, Reason = "add an optional output", Spec = amended } }, default)).State;
                Check.Equal(2, state.Facts.AcceptanceSpec!.Revision); Check.Equal(7, model.Calls); Check.Equal(WaitReason.NoProgress, state.Wait);
                Check.Equal(2, state.Acceptance!.Progress.Continued); Check.Equal(35L, state.Budget.Usage.TotalTokens);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Automatic()
        {
            string root = Root();
            try
            {
                var model = new Model(); await using var runtime = new TaskRuntime(model, new FileTaskStore(Path.Combine(root, "tasks")),
                    new HarnessOptions { MaxModelCalls = 1 }, V3CompactionTests.Binding("same"), delivery: Services(root));
                var waiting = (await runtime.ExecuteAsync(Start(root), default)).State;
                Check.Equal(WaitReason.Evidence, waiting.Wait);
                var done = (await runtime.ExecuteAsync(Import(root, waiting.Id), default)).State;
                Check.Equal(AgentTaskStatus.Completed, done.Status); Check.Equal(DeliveryStatus.Committed, done.Acceptance!.Delivery!.Status);
                Check.Equal(1, model.Calls); Check.Equal(5L, done.Budget.Usage.TotalTokens);
                byte[] bundle = await runtime.ReadDeliveryBundleAsync(done.Id, default);
                string copy = Path.Combine(root, "detached"); Directory.CreateDirectory(copy);
                using (var zip = new ZipArchive(new MemoryStream(bundle))) zip.ExtractToDirectory(copy);
                Directory.Delete(Path.Combine(root, "workspace"), true);
                await DeliveryManifestBuilder.Verify(copy, done.Acceptance.Delivery.Manifest!.ContentHash, default);
                string blob = Directory.GetFiles(Path.Combine(copy, "blobs")).First(); await File.WriteAllTextAsync(blob, "tampered");
                await Check.ThrowsAsync<InvalidDataException>(() => DeliveryManifestBuilder.Verify(copy, done.Acceptance.Delivery.Manifest.ContentHash, default));
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Review()
        {
            string root = Root();
            try
            {
                var model = new Model(); await using var runtime = new TaskRuntime(model, new FileTaskStore(Path.Combine(root, "tasks")),
                    new HarnessOptions { MaxModelCalls = 1 }, V3CompactionTests.Binding("same"), delivery: Services(root));
                var first = (await runtime.ExecuteAsync(Start(root, true), default)).State;
                var initialImport = Import(root, first.Id); initialImport.Acceptance!.Artifact!.Id = "z-old"; initialImport.Acceptance.Artifact.LogicalName = "result.txt";
                var waiting = (await runtime.ExecuteAsync(initialImport, default)).State;
                Check.Equal(WaitReason.Review, waiting.Wait);
                await File.WriteAllTextAsync(Path.Combine(root, "workspace", "result.txt"), "external change");
                var stale = new TaskCommand { Kind = TaskCommandKind.ReviewDelivery, TaskId = first.Id, Acceptance = new AcceptanceCommand
                    { Review = new ReviewDecision { CandidateFingerprint = waiting.Acceptance!.Candidate!.Fingerprint, Accept = true } } };
                await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(stale, default));
                Check.Equal(0, (await runtime.GetAsync(first.Id, default))!.Acceptance!.Reviews.Count);
                await File.WriteAllTextAsync(Path.Combine(root, "workspace", "result.txt"), "42");
                waiting = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.FinalizeDelivery, TaskId = first.Id, Acceptance = new AcceptanceCommand() }, default)).State;
                await File.WriteAllTextAsync(Path.Combine(root, "workspace", "result.txt"), "43");
                var newerImport = Import(root, first.Id); newerImport.Acceptance!.Artifact!.Id = "a-new"; newerImport.Acceptance.Artifact.LogicalName = "result.txt";
                waiting = (await runtime.ExecuteAsync(newerImport, default)).State;
                var subject = new AcceptanceCriterion { SubjectArtifact = "result.txt" };
                string latestHash = SafeFiles.Hash(System.Text.Encoding.UTF8.GetBytes("43"));
                Check.Equal(latestHash, AcceptanceBinding.Subject(waiting, subject));
                var reordered = TaskJson.Clone(waiting); reordered.Acceptance!.FrozenArtifacts.Reverse();
                Check.Equal(latestHash, AcceptanceBinding.Subject(reordered, subject));
                Check.Equal(2L, waiting.Acceptance!.FrozenArtifacts.Max(a => a.Generation ?? 0));
                var review = new TaskCommand { Kind = TaskCommandKind.ReviewDelivery, TaskId = first.Id, Acceptance = new AcceptanceCommand
                    { Review = new ReviewDecision { CandidateFingerprint = "old", Accept = true } } };
                await Check.ThrowsAsync<TaskConflictException>(() => runtime.ExecuteAsync(review, default));
                review.Id = Guid.NewGuid().ToString("N"); review.Acceptance.Review!.CandidateFingerprint = waiting.Acceptance!.Candidate!.Fingerprint;
                var done = (await runtime.ExecuteAsync(review, default)).State; Check.Equal(AgentTaskStatus.Completed, done.Status); Check.Equal(1, model.Calls);
                Check.True(done.Acceptance!.Assessment.Criteria.All(c => c.Status == AssessmentStatus.Passed));
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Recovery()
        {
            foreach (string phase in new[] { "delivery_preparing", "delivery_sealed", "delivery_before_commit" })
            {
                string root = Root();
                try
                {
                    string id; var model = new Model();
                    await using (var runtime = new TaskRuntime(model, new FileTaskStore(Path.Combine(root, "tasks")), new HarnessOptions { MaxModelCalls = 1 }, V3CompactionTests.Binding("same"), delivery: Services(root)))
                    {
                        id = (await runtime.ExecuteAsync(Start(root), default)).State.Id;
                        runtime.FaultInjection = p => { if (p == phase) throw new CheckpointWriteException(new IOException("injected seal failure")); };
                        await Check.ThrowsAsync<CheckpointWriteException>(() => runtime.ExecuteAsync(Import(root, id), default));
                    }
                    var fresh = new Model(); await using var recovered = new TaskRuntime(fresh, new FileTaskStore(Path.Combine(root, "tasks")), new HarnessOptions { MaxModelCalls = 1 }, V3CompactionTests.Binding("same"), delivery: Services(root), recoveryAuthorizer: new LegacyEvidenceRestriction());
                    var restored = (await recovered.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Recover, TaskId = id }, default)).State;
                    Check.Equal(0, restored.Facts.RequiredEvidence.Count);
                    var done = (await recovered.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.FinalizeDelivery, TaskId = id, Acceptance = new AcceptanceCommand() }, default)).State;
                    Check.Equal(AgentTaskStatus.Completed, done.Status); Check.Equal(0, fresh.Calls); Check.Equal(5L, done.Budget.Usage.TotalTokens);
                }
                finally { Directory.Delete(root, true); }
            }
        }
        private static async Task Controls()
        {
            foreach (var kind in new[] { TaskCommandKind.Cancel, TaskCommandKind.Pause, TaskCommandKind.Steer })
            {
            string root = Root();
            try
            {
                await using var runtime = new TaskRuntime(new Model(), new FileTaskStore(Path.Combine(root, "tasks")), new HarnessOptions { MaxModelCalls = 1 }, V3CompactionTests.Binding("same"), delivery: Services(root));
                var start = (await runtime.ExecuteAsync(Start(root), default)).State;
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                runtime.FaultInjection = p => { if (p == "delivery_before_commit") { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); } };
                var sealing = runtime.ExecuteAsync(Import(root, start.Id), default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var cancelling = runtime.ExecuteAsync(new TaskCommand { Kind = kind, TaskId = start.Id,
                    Steering = kind == TaskCommandKind.Steer ? new TaskSteering { Text = "Reconsider delivery first." } : null }, default);
                await Task.Delay(150); release.TrySetResult(true); await sealing; var done = (await cancelling).State;
                Check.Equal(kind == TaskCommandKind.Cancel ? AgentTaskStatus.Cancelled : kind == TaskCommandKind.Pause ? AgentTaskStatus.Paused : AgentTaskStatus.Waiting, done.Status);
                Check.True(done.Acceptance?.Delivery?.Status != DeliveryStatus.Committed);
            }
            finally { Directory.Delete(root, true); }
            }
        }
        private sealed class LegacyEvidenceRestriction : IRecoveryAuthorizer
        {
            public bool AllowsUsageReconciliation => false;
            public Task<RecoveryAuthorization> CheckAsync(AgentTaskState task, ModelBinding binding, CancellationToken ct)
            { var facts = TaskJson.Clone(task.Facts); facts.RequiredEvidence.Add("legacy-only"); return Task.FromResult(new RecoveryAuthorization { Allowed = true, RestrictedFacts = facts }); }
        }
        private static async Task Children()
        {
            string root = Root();
            try
            {
                var model = new Model(); await using var runtime = new TaskRuntime(model, new FileTaskStore(Path.Combine(root, "tasks")),
                    new HarnessOptions { MaxModelCalls = 6 }, V3CompactionTests.Binding("same"), delivery: Services(root));
                var parent = (await runtime.ExecuteAsync(Start(root), default)).State;
                parent = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.StartChild, TaskId = parent.Id, Request = HarnessTests.Request(),
                    ChildBudget = new TaskBudget { MaxModelCalls = 1, MaxToolCalls = 0, MaxTokens = 100 } }, default)).State;
                string childId = parent.Children.Single().TaskId;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                AgentTaskState? child;
                do { child = await runtime.GetAsync(childId, timeout.Token); if (child?.Status == AgentTaskStatus.Waiting) break; await Task.Delay(20, timeout.Token); } while (true);
                child = (await runtime.ExecuteAsync(Import(root, childId), default)).State; Check.Equal(AgentTaskStatus.Completed, child.Status);
                parent = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.CollectChild, TaskId = parent.Id, ChildTaskId = childId }, default)).State;
                Check.Equal(0, parent.Acceptance!.FrozenArtifacts.Count); Check.True(parent.Status != AgentTaskStatus.Completed);
                parent = (await runtime.ExecuteAsync(Import(root, parent.Id), default)).State;
                Check.Equal(AgentTaskStatus.Completed, parent.Status); Check.Equal(2, parent.Budget.ModelCalls); Check.Equal(10L, parent.Budget.Usage.TotalTokens);
                Check.Equal(2, model.Calls);
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
