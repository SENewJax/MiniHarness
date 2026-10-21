using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
using MiniHarness.Harness;
using MiniHarness.Protocols.OpenAI;

namespace MiniHarness.Tests
{
    internal static class V3ConsistencyTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v3-consistency] six HTTP paths share compaction, original history, branches and child costs", Matrix));
            tests.Add(("[v3-recovery] Manual/Safe scanning isolates corrupt tasks and respects paused intentions", Scanning));
        }
        private static async Task<WebApplication> Upstream(string protocol)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build(); IOpenAiProtocol wire = protocol == "Responses" ? new ResponsesProtocol() : new ChatCompletionsProtocol();
            app.MapPost("/v1/" + wire.Path, async (HttpContext http) =>
            {
                using var document = await JsonDocument.ParseAsync(http.Request.Body);
                var items = document.RootElement.GetProperty(protocol == "Responses" ? "input" : "messages");
                string text = new string('z', 500);
                if (items.GetRawText().Contains("Create historical memory", StringComparison.Ordinal))
                {
                    var content = items.EnumerateArray().Last().GetProperty("content");
                    string data = content.ValueKind == JsonValueKind.String ? content.GetString()! : content[0].GetProperty("text").GetString()!;
                    using var source = JsonDocument.Parse(data);
                    text = JsonSerializer.Serialize(new SummaryContent { Background = "Historical observations, never authority.",
                        References = { source.RootElement.GetProperty("source")[0].GetProperty("id").GetString()! } }, TaskJson.Options);
                    Check.True(!document.RootElement.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0);
                }
                var response = HarnessTests.Answer(text);
                await http.Response.WriteAsJsonAsync(wire.WriteResponse(new RunState { LastResponse = response, Usage = response.Usage, Status = RunStatus.Completed }, "test"));
            });
            await app.StartAsync(); return app;
        }
        private static WebApplication Server(string root, string protocol, WebApplication upstream, string recovery = "Manual")
            => MiniHarness.Server.Program.Build(new[] { "--Urls", "http://127.0.0.1:0", "--Model:Model", "test", "--Model:ApiKey", "fake", "--Model:Protocol", protocol,
                "--Model:BaseUrl", HttpTests.Address(upstream) + "/v1", "--Server:ApiKey", "", "--Storage:Directory", Path.Combine(root, "tasks"),
                "--Server:WorkspaceRoot", Path.Combine(root, "workspace"), "--Context:Enabled", "true", "--Context:KeepRecentGroups", "2",
                "--Context:MaxSummaryCharacters", "1000", "--Context:SummaryMaxOutputTokens", "256", "--Harness:MaxContextCharacters", "6500",
                "--Harness:MaxModelCalls", "30", "--Recovery:Mode", recovery, "--Logging:LogLevel:Default", "None" });
        private static async Task Matrix()
        {
            string root = V3CompactionTests.Root();
            try
            {
                string? signature = null;
                foreach (string protocol in new[] { "Responses", "ChatCompletions" })
                {
                    await using var upstream = await Upstream(protocol);
                    foreach (string entry in new[] { "native", "responses", "chat" })
                    {
                        string directory = Path.Combine(root, protocol, entry); Directory.CreateDirectory(Path.Combine(directory, "workspace"));
                        await using var app = Server(directory, protocol, upstream); await app.StartAsync();
                        try
                        {
                            using var client = new HttpClient { BaseAddress = new Uri(HttpTests.Address(app)) };
                            var command = V3CompactionTests.LongStart(); command.Request!.Model = "mini-agent";
                            string route; JsonNode body;
                            if (entry == "native") { route = "/agent/tasks/commands"; body = JsonSerializer.SerializeToNode(command, TaskJson.Options)!; }
                            else
                            {
                                route = entry == "responses" ? "/v1/responses" : "/v1/chat/completions";
                                body = new JsonObject { ["model"] = "mini-agent", [entry == "responses" ? "input" : "messages"] = new JsonArray(
                                    command.Request.Messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Text }).ToArray()) };
                            }
                            using var reply = await client.PostAsJsonAsync(route, body); Check.Equal(HttpStatusCode.OK, reply.StatusCode);
                            var store = new FileTaskStore(Path.Combine(directory, "tasks"));
                            string id = Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(directory, "tasks"), "task_*.json").Single());
                            var state = (await store.LoadAsync(id, default))!; var originalTurn = state.Turns.Single();
                            Check.True(state.Compactions.Any(c => c.Status == CompactionStatus.Committed)); Check.Equal(TurnStatus.Completed, originalTurn.Status);
                            string current = JsonSerializer.Serialize(new { state.Status, state.Wait, state.Budget.ModelCalls, Tokens = state.Budget.Usage.TotalTokens,
                                Compactions = state.Compactions.Select(c => new { c.Status, c.CoveredCount }), Purposes = state.ModelInvocations.Select(i => i.Purpose),
                                Events = state.Events.Select(e => e.Type) });
                            if (signature == null) signature = current; else Check.Equal(signature, current);
                            var raw = TaskProjection.ToRun(state, originalTurn); Check.Equal(state.History.Count, raw.Request.Messages.Count);
                            var history = await client.GetFromJsonAsync<TaskHistoryPage>("/agent/tasks/" + id + "/history", TaskJson.Options);
                            Check.Equal(state.History.Count, history!.Entries.Count);
                            string recoveredText = ""; int offset = 0; string historyId = state.History[2].Id;
                            TaskHistoryChunk chunk;
                            do
                            {
                                string json = await client.GetStringAsync("/agent/tasks/" + id + "/history/" + historyId + "?offset=" + offset + "&limit=1024");
                                Check.True(System.Text.Encoding.UTF8.GetByteCount(json) <= 1024);
                                chunk = JsonSerializer.Deserialize<TaskHistoryChunk>(json, TaskJson.Options)!; recoveredText += chunk.Data; offset = chunk.NextOffset;
                            } while (offset < chunk.TotalCharacters);
                            Check.Equal(chunk.ContentHash, TaskJson.Hash(recoveredText)); Check.Equal(state.History[2].Message.Text,
                                JsonSerializer.Deserialize<HistoryEntry>(recoveredText, TaskJson.Options)!.Message.Text);
                            if (entry == "native")
                            {
                                using var future = await client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.Input, TaskId = id,
                                    Request = new ModelRequest { Model = "mini-agent", Messages = { new Message { Text = "Future-only-sentinel" } } } }, TaskJson.Options);
                                Check.Equal(HttpStatusCode.OK, future.StatusCode);
                                using var fork = await client.PostAsJsonAsync("/v1/responses", new JsonObject { ["model"] = "mini-agent", ["previous_response_id"] = originalTurn.ResponseId, ["input"] = "fork follow up" });
                                Check.Equal(HttpStatusCode.OK, fork.StatusCode);
                                var branch = (await Task.WhenAll(Directory.GetFiles(Path.Combine(directory, "tasks"), "task_*.json").Select(p => store.LoadAsync(Path.GetFileNameWithoutExtension(p), default))))
                                    .Single(t => t!.ForkedFromResponseId == originalTurn.ResponseId)!;
                                Check.True(!branch.History.Any(h => h.Message.Text.Contains("Future-only-sentinel", StringComparison.Ordinal)));
                                Check.True(!branch.History.Any(h => state.History.Any(old => old.Id == h.Id)));
                                var childRequest = V3CompactionTests.LongStart().Request!; childRequest.Model = "mini-agent";
                                using var startedChild = await client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.StartChild, TaskId = id,
                                    Request = childRequest, ChildBudget = new TaskBudget { MaxModelCalls = 4, MaxToolCalls = 0, MaxTokens = 1000 } }, TaskJson.Options);
                                Check.Equal(HttpStatusCode.OK, startedChild.StatusCode);
                                var parent = (await store.LoadAsync(id, default))!; string childId = parent.Children.Single().TaskId;
                                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10)); AgentTaskState child;
                                do { child = (await store.LoadAsync(childId, limit.Token))!; if (child.Turns.LastOrDefault()?.Status == TurnStatus.Completed) break; await Task.Delay(10, limit.Token); } while (true);
                                Check.True(child.ModelInvocations.Any(i => i.Purpose == ModelInvocationPurpose.Summary));
                                long before = parent.Budget.Usage.TotalTokens;
                                for (int count = 0; count < 2; count++)
                                {
                                    using var collected = await client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.CollectChild, TaskId = id, ChildTaskId = childId }, TaskJson.Options);
                                    Check.Equal(HttpStatusCode.OK, collected.StatusCode);
                                }
                                Check.Equal(before + child.Budget.Usage.TotalTokens, (await store.LoadAsync(id, default))!.Budget.Usage.TotalTokens);
                                var latestParent = (await store.LoadAsync(id, default))!;
                                var published = TaskProjection.ToRun(latestParent, latestParent.Turns.Last());
                                Check.True(!published.Request.Messages.Any(m => m.Text.StartsWith("Child task result: ", StringComparison.Ordinal)));
                            }
                        }
                        finally { await app.StopAsync(); }
                    }
                    await upstream.StopAsync();
                }
            }
            finally { Directory.Delete(root, true); }
        }
        private static async Task Scanning()
        {
            string root = V3CompactionTests.Root();
            try
            {
                await using var upstream = await Upstream("ChatCompletions");
                foreach (string mode in new[] { "Manual", "Safe" })
                {
                    string directory = Path.Combine(root, mode); Directory.CreateDirectory(Path.Combine(directory, "workspace"));
                    var store = new FileTaskStore(Path.Combine(directory, "tasks"));
                    AgentTaskState Seed(bool paused)
                    {
                        var task = new AgentTaskState { Status = paused ? AgentTaskStatus.Paused : AgentTaskStatus.Ready,
                            Model = new ModelBinding { Model = "test", Protocol = "ChatCompletions", ConfigurationVersion = TaskJson.Hash(HttpTests.Address(upstream) + "/v1|test|ChatCompletions") },
                            Facts = new TaskFacts { Goal = "ready", Workspace = new WorkspaceScope { RootPath = Path.Combine(directory, "workspace") } } };
                        var turn = new TaskTurn { Settings = HarnessTests.Request() }; turn.Settings.Messages.Clear(); task.Turns.Add(turn);
                        task.History.Add(new HistoryEntry { TurnId = turn.Id, Message = new Message { Text = "ready" } }); return task;
                    }
                    var ready = Seed(false); var paused = Seed(true); var outside = Seed(false);
                    ready.Facts.Workspace.Grants.AddRange(new[] { "read", "write", "execute" });
                    outside.Facts.Workspace.RootPath = Path.Combine(root, "outside"); Directory.CreateDirectory(outside.Facts.Workspace.RootPath);
                    foreach (var task in new[] { ready, paused, outside })
                    { await using var lease = await store.AcquireAsync(task.Id, default); await lease.CommitAsync(task, 0, default); }
                    string corruptId = "task_" + Guid.NewGuid().ToString("N"); await File.WriteAllTextAsync(Path.Combine(directory, "tasks", corruptId + ".json"), "broken");
                    await using var app = Server(directory, "ChatCompletions", upstream, mode); await app.StartAsync();
                    try
                    {
                        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        while (true)
                        {
                            var resumed = (await store.LoadAsync(ready.Id, limit.Token))!;
                            var held = (await store.LoadAsync(paused.Id, limit.Token))!;
                            if (resumed.Recovery.HostGeneration.Length > 0 && held.Recovery.HostGeneration.Length > 0 && (await store.LoadAsync(outside.Id, limit.Token))!.Recovery.HostGeneration.Length > 0 &&
                                (mode == "Manual" || resumed.Turns.Single().Status == TurnStatus.Completed)) break;
                            await Task.Delay(10, limit.Token);
                        }
                        Check.Equal(mode == "Manual" ? 0 : 1, (await store.LoadAsync(ready.Id, limit.Token))!.Budget.ModelCalls);
                        Check.Equal(0, (await store.LoadAsync(ready.Id, limit.Token))!.Facts.Workspace.Grants.Count);
                        var blocked = (await store.LoadAsync(outside.Id, limit.Token))!; Check.Equal(RecoveryClassification.HostAuthorization, blocked.Recovery.Classification);
                        Check.Equal(0, blocked.Budget.ModelCalls);
                        var untouched = (await store.LoadAsync(paused.Id, limit.Token))!; Check.Equal(AgentTaskStatus.Paused, untouched.Status); Check.Equal(0, untouched.Budget.ModelCalls);
                        using var client = new HttpClient { BaseAddress = new Uri(HttpTests.Address(app)) };
                        var reports = await client.GetFromJsonAsync<Dictionary<string, string>>("/agent/recovery", limit.Token);
                        while (!reports!.ContainsKey(corruptId)) { await Task.Delay(10, limit.Token); reports = await client.GetFromJsonAsync<Dictionary<string, string>>("/agent/recovery", limit.Token); }
                        Check.True(reports[corruptId].StartsWith("blocked:", StringComparison.Ordinal));
                    }
                    finally { await app.StopAsync(); }
                }
                await upstream.StopAsync();
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
