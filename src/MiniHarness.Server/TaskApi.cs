using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Model.OpenAI;

namespace MiniHarness.Server
{
    public sealed class TaskApi
    {
        private readonly ITaskApplicationService runtime;
        private readonly ModelOptions model;
        private readonly ServerOptions server;

        private readonly ILogger<TaskApi> logger;
        public TaskApi(ITaskApplicationService runtime, ModelOptions model, ServerOptions server, ILogger<TaskApi> logger)
        { this.runtime = runtime; this.model = model; this.server = server; this.logger = logger; }
        public void Map(WebApplication app)
        {
            app.MapGet("/agent/tasks/{id}/failure", (HttpContext http, string id) => GuardAsync(http, async () =>
                await http.Response.WriteAsJsonAsync(runtime is ITaskDiagnostics diagnostics ? await diagnostics.ReadFailureAsync(id, http.RequestAborted) : null, TaskJson.Options, http.RequestAborted)));
            app.MapGet("/agent/tasks/{id}/history/{historyId}", (HttpContext http, string id, string historyId) => GuardAsync(http, async () =>
            {
                if (runtime is not ITaskDiagnostics diagnostics || !int.TryParse(http.Request.Query["offset"].FirstOrDefault() ?? "0", out var offset) ||
                    !int.TryParse(http.Request.Query["limit"].FirstOrDefault() ?? "65536", out var maxBytes)) throw new ArgumentException("Invalid history chunk request.");
                await http.Response.WriteAsJsonAsync(await diagnostics.ReadHistoryEntryAsync(id, historyId, offset, maxBytes, http.RequestAborted), TaskJson.Options, http.RequestAborted);
            }));
            app.MapGet("/agent/tasks/{id}/recovery", (HttpContext http, string id) => GuardAsync(http, async () =>
                await http.Response.WriteAsJsonAsync((await runtime.GetAsync(id, http.RequestAborted) ?? throw new ArgumentException("Task not found.")).Recovery, TaskJson.Options, http.RequestAborted)));
            app.MapGet("/agent/tasks/{id}/context", (HttpContext http, string id) => GuardAsync(http, async () =>
            {
                var state = await runtime.GetAsync(id, http.RequestAborted) ?? throw new ArgumentException("Task not found.");
                await http.Response.WriteAsJsonAsync(new { state.ContextMemory, state.ContextDiagnostics, Summaries = state.Compactions.Select(c => new { c.Id, c.Status, c.CoveredCount, c.SourceHash, c.Error }) }, TaskJson.Options, http.RequestAborted);
            }));
            app.MapGet("/agent/tasks/{id}/history", (HttpContext http, string id) => GuardAsync(http, async () =>
            {
                if (runtime is not ITaskDiagnostics diagnostics || !int.TryParse(http.Request.Query["limit"].FirstOrDefault() ?? "65536", out var maxBytes))
                    throw new ArgumentException("History diagnostics or limit unavailable.");
                await http.Response.WriteAsJsonAsync(await diagnostics.ReadHistoryAsync(id, http.Request.Query["after"].FirstOrDefault(), maxBytes, http.RequestAborted), TaskJson.Options, http.RequestAborted);
            }));
            app.MapGet("/agent/recovery", (HttpContext http, TaskRecoveryService recovery) => GuardAsync(http, async () =>
                await http.Response.WriteAsJsonAsync(recovery.Results, TaskJson.Options, http.RequestAborted)));
            app.MapGet("/agent/tasks/{id}/resources/{resourceId}/output", (HttpContext http, string id, string resourceId) => GuardAsync(http, async () =>
            {
                if (!long.TryParse(http.Request.Query["after"].FirstOrDefault() ?? "0", out var after) ||
                    !int.TryParse(http.Request.Query["limit"].FirstOrDefault() ?? "8192", out var limit)) throw new ArgumentException("Invalid output cursor or byte limit.");
                if (runtime is not IResourceOutputAccess access) throw new ArgumentException("Resource output is unavailable.");
                await http.Response.WriteAsJsonAsync(await access.ReadResourceOutputAsync(id, resourceId, after, limit, http.RequestAborted),
                    new JsonSerializerOptions(TaskJson.Options) { WriteIndented = false }, http.RequestAborted);
            }));
            app.MapPost("/agent/tasks/commands", (HttpContext http) => GuardAsync(http, async () =>
            {
                using var execution = new RequestExecution(server, http.RequestAborted);
                var command = await JsonSerializer.DeserializeAsync<TaskCommand>(http.Request.Body, TaskJson.Options, execution.Token)
                    ?? throw new ArgumentException("A task command is required.");
                if (command.Request != null)
                {
                    if (command.Request.Model != server.ModelAlias) throw new ArgumentException("Unknown agent model alias.");
                    command.Request.Model = model.Model;
                }
                if (http.Request.Query["wait"] == "false")
                {
                    var submission = await runtime.SubmitAsync(command, execution.Options, execution.Token);
                    http.Response.StatusCode = 202;
                    http.Response.Headers.Location = "/agent/tasks/" + submission.TaskId;
                    await http.Response.WriteAsJsonAsync(submission, TaskJson.Options, execution.Token);
                }
                else await http.Response.WriteAsJsonAsync(await runtime.ExecuteAsync(command, execution.Options, execution.Token), TaskJson.Options, execution.Token);
            }));
            app.MapGet("/agent/tasks/{id}", (HttpContext http, string id) => GuardAsync(http, async () =>
            {
                var state = await runtime.GetAsync(id, http.RequestAborted);
                if (state == null) { await AgentApi.ErrorAsync(http, 404, "task_not_found", "Task not found."); return; }
                await http.Response.WriteAsJsonAsync(state, TaskJson.Options, http.RequestAborted);
            }));
            app.MapGet("/agent/tasks/{id}/events", (HttpContext http, string id) => GuardAsync(http, async () =>
            {
                string cursor = http.Request.Query["after"].ToString();
                if (cursor.Length == 0) cursor = http.Request.Headers["Last-Event-ID"].ToString();
                if (cursor.Length == 0) cursor = "0";
                if (!long.TryParse(cursor, out long after) || after < 0) throw new ArgumentException("Invalid event cursor.");
                var events = await runtime.ObserveAsync(id, after, http.RequestAborted);
                if (http.Request.Query["follow"] != "true")
                { await http.Response.WriteAsJsonAsync(events, TaskJson.Options, http.RequestAborted); return; }
                http.Response.ContentType = "text/event-stream"; http.Response.Headers.CacheControl = "no-cache";
                var compact = new JsonSerializerOptions(TaskJson.Options) { WriteIndented = false };
                while (!http.RequestAborted.IsCancellationRequested)
                {
                    foreach (var item in events)
                    {
                        await http.Response.WriteAsync("id: " + item.Sequence + "\nevent: agent.event\ndata: " + JsonSerializer.Serialize(item, compact) + "\n\n", http.RequestAborted);
                        after = item.Sequence;
                        await http.Response.Body.FlushAsync(http.RequestAborted);
                    }
                    var state = await runtime.GetAsync(id, http.RequestAborted);
                    if (state == null || state.Status == AgentTaskStatus.Completed || state.Status == AgentTaskStatus.Cancelled || state.Status == AgentTaskStatus.Failed) break;
                    await Task.Delay(100, http.RequestAborted);
                    events = await runtime.ObserveAsync(id, after, http.RequestAborted);
                }
            }));
        }
        private async Task GuardAsync(HttpContext http, Func<Task> action)
        {
            try { await action(); }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
            catch (OperationCanceledException)
            { if (!http.Response.HasStarted) await AgentApi.ErrorAsync(http, 408, "agent_timeout", "Agent request timed out."); }
            catch (TaskConflictException ex) { if (!http.Response.HasStarted) await AgentApi.ErrorAsync(http, 409, "task_conflict", ex.Message); }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
            { if (!http.Response.HasStarted) await AgentApi.ErrorAsync(http, 400, "invalid_request_error", ex is ArgumentException ? ex.Message : "Malformed task command."); }
            catch (ModelException ex) { if (!http.Response.HasStarted) await AgentApi.ErrorAsync(http, 502, "upstream_error", ex.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Task request failed.");
                if (!http.Response.HasStarted) await AgentApi.ErrorAsync(http, 500, "task_error", "Task execution or storage failed.");
            }
        }
    }
}
