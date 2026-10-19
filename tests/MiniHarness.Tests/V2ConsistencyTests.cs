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
using Microsoft.Extensions.DependencyInjection;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.ProcessFixture;
using MiniHarness.Protocols.OpenAI;

namespace MiniHarness.Tests
{
    internal static class V2ConsistencyTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
            => tests.Add(("[v2-consistency] six HTTP paths share wait, incremental output, stdin, automatic wake and shutdown", Matrix));
        private static async Task Matrix()
        {
            if (!OperatingSystem.IsWindows()) throw new TestSkippedException("Owned background process platform unavailable.");
            string root = Path.Combine(Path.GetTempPath(), "miniharness-v2-http-" + Guid.NewGuid().ToString("N"));
            bool failed = false;
            string workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
            try
            {
                string? signature = null;
                foreach (string protocol in new[] { "Responses", "ChatCompletions" })
                {
                    IOpenAiProtocol wire = protocol == "Responses" ? new ResponsesProtocol() : new ChatCompletionsProtocol();
                    var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
                    await using var upstream = builder.Build();
                    upstream.MapPost("/v1/" + wire.Path, async (HttpContext http) =>
                    {
                        using var json = await JsonDocument.ParseAsync(http.Request.Body);
                        var messages = json.RootElement.GetProperty(protocol == "Responses" ? "input" : "messages");
                        var observations = new List<ToolResult>();
                        foreach (var item in messages.EnumerateArray())
                        {
                            string? text = null;
                            if (protocol == "Responses" && item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output") text = item.GetProperty("output").GetString();
                            if (protocol == "ChatCompletions" && item.GetProperty("role").GetString() == "tool") text = item.GetProperty("content").GetString();
                            if (text != null) observations.Add(JsonSerializer.Deserialize<ToolResult>(text, TaskJson.Options)!);
                        }
                        ModelResponse response;
                        bool service = messages.GetRawText().Contains("Return service", StringComparison.Ordinal);
                        if (messages.GetRawText().Contains("resource_changed", StringComparison.Ordinal) || service && observations.Count > 0) response = HarnessTests.Answer("finished");
                        else if (observations.Count == 0) response = HarnessTests.Call("start_process", service ? "{\"profile\":\"heartbeat\"}" : "{\"profile\":\"echo\"}", "start");
                        else
                        {
                            var resource = JsonSerializer.Deserialize<ResourceReference>(observations.First().Output, TaskJson.Options)!;
                            response = HarnessTests.Call("wait_process", JsonSerializer.Serialize(new { resource_id = resource.Id, condition = "Exit", wait_ms = 30000 }), "wait");
                        }
                        await http.Response.WriteAsJsonAsync(wire.WriteResponse(new RunState { LastResponse = response, Usage = response.Usage,
                            Status = response.Finish == ModelFinish.ToolCalls ? RunStatus.WaitingForTools : RunStatus.Completed }, "test"));
                    });
                    await upstream.StartAsync();
                    foreach (string entry in new[] { "chat", "responses", "native" })
                    {
                        string directory = Path.Combine(root, protocol, entry);
                        await using var app = MiniHarness.Server.Program.Build(new[]
                        {
                            "--Urls", "http://127.0.0.1:0", "--Model:Model", "test", "--Model:ApiKey", "fake", "--Model:Protocol", protocol,
                            "--Model:BaseUrl", HttpTests.Address(upstream) + "/v1/", "--Storage:Directory", Path.Combine(directory, "tasks"),
                            "--Server:ApiKey", "", "--Server:WorkspaceRoot", workspace, "--Processes:Enabled", "true",
                            "--Processes:Limits:Directory", Path.Combine(directory, "processes"), "--Processes:PollMilliseconds", "100",
                            "--Processes:Profiles:0:Name", "echo", "--Processes:Profiles:0:FileName", "dotnet",
                            "--Processes:Profiles:0:Arguments:0", typeof(FixtureMarker).Assembly.Location,
                            "--Processes:Profiles:0:Arguments:1", "echo", "--Processes:Profiles:0:AllowStdin", "true", "--Logging:LogLevel:Default", "None",
                            "--Processes:Profiles:1:Name", "heartbeat", "--Processes:Profiles:1:FileName", "dotnet",
                            "--Processes:Profiles:1:Arguments:0", typeof(FixtureMarker).Assembly.Location, "--Processes:Profiles:1:Arguments:1", "heartbeat"
                        });
                        try
                        {
                        await app.StartAsync();
                        if (signature == null) app.Services.GetRequiredService<MiniHarness.Coding.Processes.ProcessSessionManager>().FaultInjection =
                            stage => { if (stage == "before_notify") throw new IOException("Drop notification; fallback must reconcile."); };
                        using var client = new HttpClient { BaseAddress = new Uri(HttpTests.Address(app)) };
                        string route = entry == "native" ? "/agent/tasks/commands" : entry == "responses" ? "/v1/responses" : "/v1/chat/completions";
                        var body = entry == "native" ? JsonSerializer.SerializeToNode(new TaskCommand { Kind = TaskCommandKind.Start,
                            Request = new ModelRequest { Model = "mini-agent", Messages = { new Message { Text = "Run background echo" } } } }, TaskJson.Options) : HttpTests.Body(entry == "responses", "Run background echo");
                        using var response = await client.PostAsJsonAsync(route, body);
                        Check.Equal(entry == "native" ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
                        var store = new FileTaskStore(Path.Combine(directory, "tasks"));
                        string id = Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(directory, "tasks"), "task_*.json").Single());
                        var waiting = (await store.LoadAsync(id, default))!;
                        Check.Equal(WaitReason.Resource, waiting.Wait); Check.Equal(2, waiting.Budget.ModelCalls);
                        var resource = waiting.Resources.Single();
                        Check.True(waiting.Facts.Workspace.Grants.Contains("execute"));
                        if (signature == null)
                        {
                            // A real ten-second silent Exit wait must not poll the model.
                            await Task.Delay(TimeSpan.FromSeconds(10));
                            Check.Equal(2, (await store.LoadAsync(id, default))!.Budget.ModelCalls);
                            using (var events = await client.GetAsync("/agent/tasks/" + id + "/events?follow=true", HttpCompletionOption.ResponseHeadersRead))
                            using (var reader = new StreamReader(await events.Content.ReadAsStreamAsync()))
                                Check.True((await reader.ReadLineAsync())!.StartsWith("id: ", StringComparison.Ordinal));
                            Check.Equal(WaitReason.Resource, (await store.LoadAsync(id, default))!.Wait);
                        }
                        string outputRoute = "/agent/tasks/" + id + "/resources/" + resource.Id + "/output?after=0&limit=8192";
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        while (true)
                        {
                            var page = JsonSerializer.Deserialize<ProcessOutputPage>(await client.GetStringAsync(outputRoute, deadline.Token), TaskJson.Options)!;
                            if (string.Concat(page.Chunks.Select(c => c.Text)).Contains("READY:", StringComparison.Ordinal)) break;
                            await Task.Delay(20, deadline.Token);
                        }
                        using var written = await client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.WriteResourceInput,
                            TaskId = id, ResourceId = resource.Id, ResourceInput = new ResourceInput { ExpectedGeneration = resource.Process!.Generation, Text = "exit\n" } });
                        Check.Equal(HttpStatusCode.OK, written.StatusCode);
                        AgentTaskState done;
                        do { done = (await store.LoadAsync(id, deadline.Token))!; if (done.Turns.Single().Status == TurnStatus.Completed) break;
                            await Task.Delay(20, deadline.Token); } while (true);
                        Check.Equal(3, done.Budget.ModelCalls); Check.Equal(2, done.Budget.ToolCalls);
                        Check.True(done.Events.Count(e => e.Type == "ResourceOutputAvailable") <= 32);
                        Check.Equal("finished", done.Resources.Single().State); Check.True(done.Resources.Single().Process!.TerminationConfirmed);
                        string current = JsonSerializer.Serialize(new { done.Status, done.Wait, done.Budget.ModelCalls, done.Budget.ToolCalls,
                            done.Budget.Usage, Resource = done.Resources.Single().State,
                            Events = done.Events.Where(e => e.Type != "ResourceOutputAvailable").Select(e => e.Type).ToArray() });
                        if (signature == null) signature = current; else Check.Equal(signature, current);
                        if (protocol == "ChatCompletions" && entry == "native")
                        {
                            var request = new TaskCommand { Kind = TaskCommandKind.Start, Request = new ModelRequest { Model = "mini-agent",
                                Messages = { new Message { Text = "Return service without a resource wait" } } } };
                            using var serviceResponse = await client.PostAsJsonAsync("/agent/tasks/commands", request);
                            Check.Equal(HttpStatusCode.OK, serviceResponse.StatusCode);
                            var service = JsonSerializer.Deserialize<TaskCommandResult>(await serviceResponse.Content.ReadAsStringAsync(), TaskJson.Options)!.State;
                            using var hintDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                            while (!service.Events.Any(e => e.Type == "ResourceOutputAvailable"))
                            { await Task.Delay(20, hintDeadline.Token); service = (await store.LoadAsync(service.Id, hintDeadline.Token))!; }
                            Check.Equal(TurnStatus.Completed, service.Turns.Single().Status); Check.Equal(2, service.Budget.ModelCalls);
                            using var stopped = await client.PostAsJsonAsync("/agent/tasks/commands", new TaskCommand { Kind = TaskCommandKind.CancelResource,
                                TaskId = service.Id, ResourceId = service.Resources.Single().Id });
                            Check.Equal(HttpStatusCode.OK, stopped.StatusCode);
                            var ended = (await store.LoadAsync(service.Id, default))!;
                            Check.Equal("cancelled", ended.Resources.Single().State); Check.Equal(2, ended.Budget.ModelCalls);
                        }
                        }
                        finally { await app.StopAsync(); }
                    }
                }
            }
            catch (Exception error) { failed = true; Console.WriteLine("V2 matrix failure retained at " + root + "\n" + error); throw; }
            finally { if (!failed) Directory.Delete(root, true); }
        }
    }
}
