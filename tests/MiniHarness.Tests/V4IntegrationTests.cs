using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MiniHarness.Abstractions;
using MiniHarness.Coding;
using MiniHarness.Harness;
using MiniHarness.Harness.Acceptance;
using MiniHarness.Harness.Delivery;
using MiniHarness.Protocols.OpenAI;
using MiniHarness.Testing;

namespace MiniHarness.Tests
{
    internal static class V4IntegrationTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v4-evidence] real verification freezes profile/logs and protected mutation cannot qualify", Verification));
            tests.Add(("[v4-recovery] independent OS hosts survive sealing kills and retain review/no-progress", Restart));
            tests.Add(("[v4-consistency] two upstreams and three HTTP entries share acceptance, sealing and diagnostics", Matrix));
            tests.Add(("[v4-artifact] a shared inventory separates new external files and detects mixed edits", Inventory));
        }
        internal static string[] Configuration(string root) => new[] { "--Delivery:Enabled", "true", "--Delivery:Directory", Path.Combine(root, "sealed"),
            "--Delivery:AllowArtifactImport", "true", "--Delivery:AllowReview", "true", "--Delivery:Template:Criteria:0:Id", "result",
            "--Delivery:Template:Criteria:0:Kind", "ArtifactPresence", "--Delivery:Template:Criteria:0:ArtifactName", "result.txt",
            "--Context:Enabled", "false", "--Harness:MaxModelCalls", "1", "--Harness:MaxTotalTokens", "10000" };
        private static async Task Inventory()
        {
            string root = V4Tests.Root();
            try
            {
                var task = new AgentTaskState { Facts = new TaskFacts { Workspace = new WorkspaceScope { RootPath = Path.Combine(root, "workspace") } }, Acceptance = new AcceptanceState() };
                var ledger = new ChangeLedger(Path.Combine(root, "ledger")); var provider = new DeliveryChangeSetProvider(ledger);
                task.Acceptance.WorkspaceBaseline = await provider.CaptureBaselineAsync(task.Facts.Workspace, default);
                await File.WriteAllTextAsync(Path.Combine(task.Facts.Workspace.RootPath, "external.txt"), "external input");
                var snapshot = await provider.FreezeAsync(task, default);
                Check.Equal(0, snapshot.Entries.Count); Check.Equal("external.txt", snapshot.ExternalEntries.Single().Path); Check.Equal(0, snapshot.Conflicts.Count);
                string file = Path.Combine(task.Facts.Workspace.RootPath, "result.txt"); byte[] initial = await File.ReadAllBytesAsync(file);
                ledger.EnsureBaseline(task.Id, "result.txt", initial); await File.WriteAllTextAsync(file, "43");
                ledger.RecordModified(task.Id, "result.txt", SafeFiles.Hash(initial), SafeFiles.Hash(await File.ReadAllBytesAsync(file)), new ToolExecutionContext { OperationId = "agent" });
                Check.Equal(0, (await provider.FreezeAsync(task, default)).Conflicts.Count);
                await File.WriteAllTextAsync(file, "outside"); Check.True((await provider.FreezeAsync(task, default)).Conflicts.Contains("result.txt"));
                await File.WriteAllTextAsync(file, "42");
                var services = V4Tests.Services(root); services.Changes = provider;
                var patch = new ApplyPatchTool(ledger);
                string Arguments(string oldText, string newText) => JsonSerializer.Serialize(new { path = "result.txt", expected_hash = SafeFiles.Hash(System.Text.Encoding.UTF8.GetBytes(oldText)), old_text = oldText, new_text = newText });
                var model = new ScriptedModel(HarnessTests.Call("apply_patch", Arguments("42", "43")), HarnessTests.Call("apply_patch", Arguments("outside", "44"), "second"));
                await using var runtime = new TaskRuntime(model, new FileTaskStore(Path.Combine(root, "tasks")), new HarnessOptions { MaxModelCalls = 3 }, V3CompactionTests.Binding("same"),
                    tools: new ToolRegistry(new[] { patch }), toolPolicy: new AllowListToolPolicy(new[] { "apply_patch" }), delivery: services);
                bool injected = false; runtime.FaultInjection = phase => { if (phase == "operation_committed" && !injected) { injected = true; File.WriteAllText(file, "outside"); } };
                var start = V4Tests.Start(root); start.Facts!.Workspace.Grants.AddRange(new[] { "read", "write" });
                var rejected = (await runtime.ExecuteAsync(start, default)).State;
                Check.Equal(WaitReason.Reconciliation, rejected.Wait); Check.Equal("outside", await File.ReadAllTextAsync(file));
                Check.Equal(ToolCallStatus.Rejected, rejected.Operations.Last().Outcome!.Status);
                rejected = (await runtime.ExecuteAsync(V4Tests.Import(root, rejected.Id), default)).State;
                Check.True(rejected.Status != AgentTaskStatus.Completed); Check.True(rejected.Acceptance!.Assessment.Blockers.Contains("change_set_conflict"));
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Verification()
        {
            string root = V4Tests.Root();
            try
            {
                string workspace = Path.Combine(root, "workspace"), logs = Path.Combine(root, "logs"); Directory.CreateDirectory(logs);
                await File.WriteAllTextAsync(Path.Combine(workspace, "Tests.cs"), "immutable test");
                var revision = new DirectoryWorkspaceRevision();
                var verifier = new VerificationTool(new[] { new VerificationProfile { Name = "tests", FileName = Environment.ProcessPath!,
                    Arguments = new[] { "--self", "tests", "1" }, MinimumTests = 1, TestCountRegex = "Tests run: (\\d+)" } }, revision, logs);
                var spec = new AcceptanceSpec { ProtectedPaths = { "Tests.cs" }, Criteria = { new AcceptanceCriterion { Id = "tests", Kind = CriterionKind.VerificationProfile,
                    Profile = "tests", VerifierFingerprint = verifier.Fingerprint("tests") } } };
                AcceptanceBinding.Validate(spec);
                var services = new DeliveryServices { Authorizer = new V4Tests.Host(), Artifacts = new FileArtifactStore(Path.Combine(root, "sealed"), new[] { logs }, new DeliveryOptions()) };
                var model = new ScriptedModel(HarnessTests.Call("run_verification", "{\"profile\":\"tests\"}"), HarnessTests.Answer("verified"));
                await using var runtime = new TaskRuntime(model, new FileTaskStore(Path.Combine(root, "tasks")), new HarnessOptions { MaxModelCalls = 3 }, V3CompactionTests.Binding("same"),
                    tools: new ToolRegistry(new[] { verifier }), toolPolicy: new AllowListToolPolicy(new[] { "run_verification" }), workspaceRevision: revision, delivery: services);
                var start = V4Tests.Start(root); start.Facts!.AcceptanceSpec = spec; start.Facts.Workspace.Grants.Add("execute");
                var done = (await runtime.ExecuteAsync(start, default)).State;
                Check.Equal(AgentTaskStatus.Completed, done.Status); Check.Equal(1, done.Acceptance!.Attempts.Count);
                Check.Equal(VerificationAttemptStatus.Passed, done.Acceptance.Attempts.Single().Status);
                Check.Equal(verifier.Fingerprint("tests"), done.Acceptance.Attempts.Single().Result!.Evidence.VerifierFingerprint);
                Directory.Delete(logs, true); foreach (var artifact in done.Acceptance.FrozenArtifacts) await services.Artifacts.ReadAsync(done.Id, artifact, default);
                var patch = new ApplyPatchTool(new ChangeLedger(Path.Combine(root, "changes")));
                var attempted = new ScriptedModel(HarnessTests.Call("apply_patch", JsonSerializer.Serialize(new { path = "Tests.cs", expected_hash = SafeFiles.Hash(System.Text.Encoding.UTF8.GetBytes("immutable test")), old_text = "immutable", new_text = "weakened" })), HarnessTests.Answer("done"));
                await using var protectedRuntime = new TaskRuntime(attempted, new FileTaskStore(Path.Combine(root, "protected-tasks")), new HarnessOptions { MaxModelCalls = 2 }, V3CompactionTests.Binding("same"),
                    tools: new ToolRegistry(new[] { patch }), toolPolicy: new AllowListToolPolicy(new[] { "apply_patch" }), delivery: services);
                var protectedStart = V4Tests.Start(root); protectedStart.Facts!.AcceptanceSpec!.ContentHash = "";
                protectedStart.Facts.AcceptanceSpec.ProtectedPaths.Add("Tests.cs"); AcceptanceBinding.Validate(protectedStart.Facts.AcceptanceSpec);
                protectedStart.Facts.Workspace.Grants.Add("write");
                var rejected = (await protectedRuntime.ExecuteAsync(protectedStart, default)).State;
                Check.Equal(ToolCallStatus.Rejected, rejected.Operations.Single().Outcome!.Status);
                Check.Equal("immutable test", await File.ReadAllTextAsync(Path.Combine(workspace, "Tests.cs")));
                Check.True(rejected.Status != AgentTaskStatus.Completed);
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Restart()
        {
            string root = Path.Combine(ValidationPaths.Root, "v4-hosts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            foreach (string phase in new[] { "delivery_preparing", "delivery_sealed", "delivery_before_commit", "delivery_committed" })
            {
                string directory = Path.Combine(root, phase); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                using var first = await RecoveryHost.StartAsync(directory, phase, true, Configuration(directory), timeout.Token);
                await File.WriteAllTextAsync(Path.Combine(directory, "workspace", "result.txt"), "42", timeout.Token);
                var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }; start.Request!.Model = "mini-agent";
                using var started = await first.Client.PostAsJsonAsync("/agent/tasks/commands", start, TaskJson.Options, timeout.Token);
                Check.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync(timeout.Token));
                var initial = (await started.Content.ReadFromJsonAsync<TaskCommandResult>(TaskJson.Options, timeout.Token))!.State;
                using var imported = await first.Client.PostAsJsonAsync("/agent/tasks/commands?wait=false", V4Tests.Import(directory, initial.Id), TaskJson.Options, timeout.Token);
                Check.True(imported.IsSuccessStatusCode); using (await RecoveryHost.ReadMarkerAsync(Path.Combine(directory, "phase.json"), timeout.Token)) { }
                var before = (await new FileTaskStore(Path.Combine(directory, "tasks")).LoadAsync(initial.Id, timeout.Token))!;
                await first.KillAsync(timeout.Token);
                using var second = await RecoveryHost.StartAsync(directory, "none", true, Configuration(directory), timeout.Token);
                using var recovered = await second.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { TaskId = initial.Id, Kind = TaskCommandKind.Recover }, TaskJson.Options, timeout.Token);
                Check.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync(timeout.Token));
                var recoveredTask = (await recovered.Content.ReadFromJsonAsync<TaskCommandResult>(TaskJson.Options, timeout.Token))!.State;
                using var final = await second.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { TaskId = initial.Id, Kind = TaskCommandKind.FinalizeDelivery,
                    Acceptance = new AcceptanceCommand { ExpectedHash = recoveredTask.Acceptance!.Candidate!.Fingerprint } }, TaskJson.Options, timeout.Token);
                Check.True(final.IsSuccessStatusCode, await final.Content.ReadAsStringAsync(timeout.Token));
                var done = (await final.Content.ReadFromJsonAsync<TaskCommandResult>(TaskJson.Options, timeout.Token))!.State;
                Check.Equal(AgentTaskStatus.Completed, done.Status); Check.Equal(before.Budget.ModelCalls, done.Budget.ModelCalls);
                Check.Equal(before.Budget.Usage.TotalTokens, done.Budget.Usage.TotalTokens);
                if (phase != "delivery_preparing") Check.Equal(before.Acceptance!.Delivery!.Manifest!.ContentHash, done.Acceptance!.Delivery!.Manifest!.ContentHash);
                using var bundle = await second.Client.GetAsync("/agent/tasks/" + initial.Id + "/delivery/bundle", timeout.Token); Check.True(bundle.IsSuccessStatusCode);
            }
            foreach (bool review in new[] { true, false })
            {
                string directory = Path.Combine(root, review ? "review" : "no-progress"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                var config = Configuration(directory).ToList();
                if (review) config.AddRange(new[] { "--Delivery:Template:Criteria:1:Id", "review", "--Delivery:Template:Criteria:1:Kind", "HumanReview" });
                else config.AddRange(new[] { "--Harness:MaxModelCalls", "10", "--Harness:MaxContextCharacters", "200000", "--Coding:Enabled", "true", "--Coding:ArtifactDirectory", Path.Combine(directory, "logs"),
                    "--Coding:Profiles:0:Name", "never", "--Coding:Profiles:0:FileName", "dotnet", "--Coding:Profiles:0:Arguments:0", "--version",
                    "--Delivery:Template:Criteria:0:Kind", "VerificationProfile", "--Delivery:Template:Criteria:0:Profile", "never" });
                using var first = await RecoveryHost.StartAsync(directory, "none", true, config, timeout.Token);
                await File.WriteAllTextAsync(Path.Combine(directory, "workspace", "result.txt"), "42", timeout.Token);
                var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }; start.Request!.Model = "mini-agent";
                using var started = await first.Client.PostAsJsonAsync("/agent/tasks/commands", start, TaskJson.Options, timeout.Token);
                Check.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync(timeout.Token));
                var before = (await started.Content.ReadFromJsonAsync<TaskCommandResult>(TaskJson.Options, timeout.Token))!.State;
                if (review)
                {
                    using var registered = await first.Client.PostAsJsonAsync("/agent/tasks/commands", V4Tests.Import(directory, before.Id), TaskJson.Options, timeout.Token);
                    Check.True(registered.IsSuccessStatusCode); before = (await registered.Content.ReadFromJsonAsync<TaskCommandResult>(TaskJson.Options, timeout.Token))!.State;
                }
                Check.Equal(review ? WaitReason.Review : WaitReason.NoProgress, before.Wait);
                await first.KillAsync(timeout.Token);
                using var second = await RecoveryHost.StartAsync(directory, "none", true, config, timeout.Token);
                using var recovered = await second.Client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { TaskId = before.Id, Kind = TaskCommandKind.Recover }, TaskJson.Options, timeout.Token);
                Check.True(recovered.IsSuccessStatusCode); var after = (await recovered.Content.ReadFromJsonAsync<TaskCommandResult>(TaskJson.Options, timeout.Token))!.State;
                Check.Equal(before.Wait, after.Wait); Check.Equal(before.Budget.ModelCalls, after.Budget.ModelCalls);
                Check.Equal(before.Acceptance!.Progress.Unchanged, after.Acceptance!.Progress.Unchanged); Check.True(!after.Recovery.CanResume);
                Check.Equal(before.Acceptance.Candidate?.Fingerprint, after.Acceptance.Candidate?.Fingerprint);
            }
        }
        private static async Task Matrix()
        {
            string root = V4Tests.Root();
            try
            {
                string? signature = null;
                foreach (string protocol in new[] { "Responses", "ChatCompletions" })
                {
                    var wire = protocol == "Responses" ? (IOpenAiProtocol)new ResponsesProtocol() : new ChatCompletionsProtocol();
                    var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
                    await using var upstream = builder.Build();
                    upstream.MapPost(protocol == "Responses" ? "/v1/responses" : "/v1/chat/completions", async http =>
                    { var answer = HarnessTests.Answer("done"); await http.Response.WriteAsJsonAsync(wire.WriteResponse(new RunState { LastResponse = answer, Usage = answer.Usage, Status = RunStatus.Completed }, "test")); });
                    await upstream.StartAsync();
                    foreach (string entry in new[] { "native", "responses", "chat" })
                    {
                        string directory = Path.Combine(root, protocol, entry); Directory.CreateDirectory(Path.Combine(directory, "workspace")); File.WriteAllText(Path.Combine(directory, "workspace", "result.txt"), "42");
                        var config = new List<string>(Configuration(directory));
                        config.AddRange(new[] { "--Urls", "http://127.0.0.1:0", "--Model:Model", "test", "--Model:ApiKey", "fake", "--Model:Protocol", protocol, "--Model:BaseUrl", HttpTests.Address(upstream) + "/v1",
                            "--Server:ApiKey", "", "--Server:WorkspaceRoot", Path.Combine(directory, "workspace"), "--Storage:Directory", Path.Combine(directory, "tasks"), "--Logging:LogLevel:Default", "None" });
                        await using var server = MiniHarness.Server.Program.Build(config.ToArray()); await server.StartAsync();
                        using var client = new HttpClient { BaseAddress = new Uri(HttpTests.Address(server)) };
                        string route; object body;
                        if (entry == "native") { route = "/agent/tasks/commands"; var start = new TaskCommand { Kind = TaskCommandKind.Start, Request = HarnessTests.Request() }; start.Request.Model = "mini-agent"; body = start; }
                        else { route = entry == "responses" ? "/v1/responses" : "/v1/chat/completions"; body = new JsonObject { ["model"] = "mini-agent", [entry == "responses" ? "input" : "messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "test" }) }; }
                        using var response = await client.PostAsJsonAsync(route, body, TaskJson.Options);
                        Check.True(entry == "native" ? response.IsSuccessStatusCode : response.StatusCode == System.Net.HttpStatusCode.Conflict,
                            await response.Content.ReadAsStringAsync());
                        var store = new FileTaskStore(Path.Combine(directory, "tasks")); string path = Directory.GetFiles(Path.Combine(directory, "tasks"), "task_*.json").Single();
                        var state = (await store.LoadAsync(Path.GetFileNameWithoutExtension(path), default))!;
                        if (entry != "native") Check.Equal(state.Id, response.Headers.GetValues("X-Agent-Task-Id").Single());
                        using var imported = await client.PostAsJsonAsync("/agent/tasks/commands", V4Tests.Import(directory, state.Id), TaskJson.Options);
                        Check.True(imported.IsSuccessStatusCode, await imported.Content.ReadAsStringAsync());
                        var done = (await imported.Content.ReadFromJsonAsync<TaskCommandResult>(TaskJson.Options))!.State;
                        string current = JsonSerializer.Serialize(new { done.Status, done.Wait, done.Budget.ModelCalls, done.Budget.ToolCalls,
                            Criteria = done.Acceptance!.Assessment.Criteria.Select(c => new { c.CriterionId, c.Status }), Spec = done.Facts.AcceptanceSpec!.ContentHash }, TaskJson.Options);
                        signature ??= current; Check.Equal(signature, current);
                        long revision = done.Revision;
                        using var diagnostics = await client.GetAsync("/agent/tasks/" + state.Id + "/acceptance"); Check.True(diagnostics.IsSuccessStatusCode);
                        Check.Equal(revision, (await store.LoadAsync(state.Id, default))!.Revision);
                        await server.StopAsync();
                    }
                    await upstream.StopAsync();
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
