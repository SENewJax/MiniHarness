using System;
using System.IO;
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
        private readonly ITaskRuntime runtime;
        private readonly ModelOptions model;
        private readonly ServerOptions server;
        private readonly ILogger<TaskApi> logger;
        public TaskApi(ITaskRuntime runtime, ModelOptions model, ServerOptions server, ILogger<TaskApi> logger)
        { this.runtime = runtime; this.model = model; this.server = server; this.logger = logger; }
        public void Map(WebApplication app)
        {
            app.MapPost("/agent/tasks/commands", (HttpContext http) => GuardAsync(http, async () =>
            {
                var command = await JsonSerializer.DeserializeAsync<TaskCommand>(http.Request.Body, TaskJson.Options, http.RequestAborted)
                    ?? throw new ArgumentException("A task command is required.");
                if (command.Request != null)
                {
                    if (command.Request.Model != server.ModelAlias) throw new ArgumentException("Unknown agent model alias.");
                    command.Request.Model = model.Model;
                }
                if (command.Kind == TaskCommandKind.Start) ConfigureWorkspace(command);
                if (http.Request.Query["wait"] == "false")
                {
                    var submission = await runtime.SubmitAsync(command, http.RequestAborted);
                    http.Response.StatusCode = 202;
                    http.Response.Headers.Location = "/agent/tasks/" + submission.TaskId;
                    await http.Response.WriteAsJsonAsync(submission, TaskJson.Options, http.RequestAborted);
                }
                else await http.Response.WriteAsJsonAsync(await runtime.ExecuteAsync(command, http.RequestAborted), TaskJson.Options, http.RequestAborted);
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
        private void ConfigureWorkspace(TaskCommand command)
        {
            command.Facts ??= new TaskFacts { Goal = command.Request?.Messages.FirstOrDefault(m => m.Role == "user")?.Text ?? "" };
            string requested = command.Facts.Workspace.RootPath;
            if (server.WorkspaceRoot.Length == 0)
            {
                if (requested.Length > 0) throw new ArgumentException("Configure Server:WorkspaceRoot before using a workspace.");
            }
            else
            {
                string allowed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.WorkspaceRoot));
                string resolved = requested.Length == 0 ? allowed : Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
                if (!resolved.Equals(allowed, StringComparison.OrdinalIgnoreCase) && !resolved.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Workspace is outside the server scope.");
                for (string? path = resolved; path != null; path = Directory.GetParent(path)?.FullName)
                {
                    if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new ArgumentException("Linked workspace paths are not permitted.");
                    if (path.Equals(allowed, StringComparison.OrdinalIgnoreCase)) break;
                }
                command.Facts.Workspace.RootPath = resolved;
            }
            command.Facts.Workspace.Grants = server.WorkspaceGrants.ToList();
        }
        private async Task GuardAsync(HttpContext http, Func<Task> action)
        {
            try { await action(); }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
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
