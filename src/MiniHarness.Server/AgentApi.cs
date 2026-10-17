using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MiniHarness.Abstractions;
using MiniHarness.Model.OpenAI;
using MiniHarness.Protocols.OpenAI;

namespace MiniHarness.Server
{
    public sealed class AgentApi
    {
        private readonly IAgentService service;
        private readonly ServerOptions server;
        private readonly ModelOptions model;
        private readonly ILogger<AgentApi> logger;
        private readonly ChatCompletionsProtocol chat = new ChatCompletionsProtocol();
        private readonly ResponsesProtocol responses = new ResponsesProtocol();

        public AgentApi(IAgentService service, ServerOptions server, ModelOptions model, ILogger<AgentApi> logger)
        {
            this.service = service; this.server = server; this.model = model; this.logger = logger;
        }

        public void Map(WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                if ((context.Request.Path.StartsWithSegments("/v1") || context.Request.Path.StartsWithSegments("/agent")) && !Authorized(context))
                {
                    await ErrorAsync(context, 401, "invalid_api_key", "Invalid server API key.").ConfigureAwait(false);
                    return;
                }
                await next(context).ConfigureAwait(false);
            });
            app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
            app.MapGet("/v1/models", () => Results.Json(new
            {
                @object = "list", data = new[] { new { id = server.ModelAlias, @object = "model", created = 0, owned_by = "miniharness" } }
            }));
            app.MapPost("/v1/chat/completions", (HttpContext context) => HandleAsync(context, chat));
            app.MapPost("/v1/responses", (HttpContext context) => HandleAsync(context, responses));
            app.MapGet("/v1/responses/{id}", (HttpContext context, string id) => GetResponseAsync(context, id));
        }

        private bool Authorized(HttpContext context)
        {
            if (server.ApiKey.Length == 0) return true;
            string header = context.Request.Headers.Authorization.ToString();
            string supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header.Substring(7) : "";
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                SHA256.HashData(Encoding.UTF8.GetBytes(server.ApiKey)));
        }

        private async Task HandleAsync(HttpContext http, IOpenAiProtocol protocol)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(server.RequestTimeoutSeconds));
            try
            {
                using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: deadline.Token).ConfigureAwait(false);
                var api = ReadRequest(protocol, json.RootElement);
                if (api.ModelRequest.Model != server.ModelAlias)
                {
                    await ErrorAsync(http, 404, "model_not_found", "Unknown agent model alias.").ConfigureAwait(false);
                    return;
                }
                api.ModelRequest.Model = model.Model;
                api.ModelRequest.StoreResponse = api.Store;
                var state = await service.RespondAsync(new AgentInput
                {
                    Request = api.ModelRequest, PreviousResponseId = api.PreviousResponseId, ToolsSpecified = api.ToolsSpecified,
                    CancelTaskWhenWaitCancelled = server.CancelOnDisconnect
                }, deadline.Token).ConfigureAwait(false);
                if (state.TaskId != null) http.Response.Headers["X-Agent-Task-Id"] = state.TaskId;
                http.Response.Headers["X-Agent-Run-Id"] = state.Id;
                http.Response.Headers["X-Agent-Status"] = state.Status.ToString();
                if (state.StopReason != null) http.Response.Headers["X-Agent-Stop-Reason"] = SafeHeader(state.StopReason);
                if (state.Status == RunStatus.Cancelled)
                {
                    if (!http.RequestAborted.IsCancellationRequested)
                        await ErrorAsync(http, 408, "agent_timeout", "Agent request timed out.", state.Id).ConfigureAwait(false);
                    return;
                }
                if (state.Status == RunStatus.BudgetExhausted)
                {
                    await ErrorAsync(http, 429, "agent_budget_exhausted", state.StopReason ?? "Agent budget exhausted.", state.Id).ConfigureAwait(false);
                    return;
                }
                if (state.Status == RunStatus.WaitingForUser && state.LastResponse?.Finish != ModelFinish.Length &&
                    state.LastResponse?.Finish != ModelFinish.Refused && state.LastResponse?.Finish != ModelFinish.ContentFiltered)
                {
                    await ErrorAsync(http, 409, "agent_requires_attention", state.StopReason ?? "Agent requires attention.", state.Id).ConfigureAwait(false);
                    return;
                }
                var response = protocol.WriteResponse(state, server.ModelAlias);
                if (api.Stream)
                    await SseWriter.WriteAsync(http.Response, protocol is ResponsesProtocol, response, api.IncludeUsage, deadline.Token).ConfigureAwait(false);
                else await http.Response.WriteAsJsonAsync(response, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                if (!http.Response.HasStarted) await ErrorAsync(http, 408, "agent_timeout", "Agent request timed out.").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ProtocolException || ex is JsonException || ex is ArgumentException)
            {
                if (!http.Response.HasStarted)
                    await ErrorAsync(http, 400, "invalid_request_error", ex is ProtocolException || ex is ArgumentException
                        ? ex.Message : "Malformed request or unsupported field value.").ConfigureAwait(false);
            }
            catch (ModelException ex)
            {
                logger.LogWarning("Model request failed: {Reason}", ex.Message);
                if (!http.Response.HasStarted) await ErrorAsync(http, 502, "upstream_error", ex.Message).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                if (!http.Response.HasStarted) await ErrorAsync(http, 404, "response_not_found", "Previous response not found.").ConfigureAwait(false);
            }
            catch (TaskConflictException ex)
            {
                if (!http.Response.HasStarted) await ErrorAsync(http, 409, "task_conflict", ex.Message).ConfigureAwait(false);
            }
            catch (BadHttpRequestException ex)
            {
                if (!http.Response.HasStarted) await ErrorAsync(http, ex.StatusCode, "invalid_request_error", "Invalid HTTP request.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent execution failed; no automatic replay will occur.");
                if (!http.Response.HasStarted) await ErrorAsync(http, 500, "agent_error", "Agent execution or checkpoint storage failed.").ConfigureAwait(false);
            }
        }

        private static ApiRequest ReadRequest(IOpenAiProtocol protocol, JsonElement json)
        {
            try { return protocol.ReadRequest(json); }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is KeyNotFoundException || ex is FormatException)
            {
                throw new ProtocolException("Malformed request or unsupported field value.");
            }
        }

        private async Task GetResponseAsync(HttpContext http, string id)
        {
            try
            {
                var state = await service.ReadResponseAsync(id, http.RequestAborted).ConfigureAwait(false);
                if (state == null || !state.Request.StoreResponse)
                {
                    await ErrorAsync(http, 404, "response_not_found", "Response not found.").ConfigureAwait(false);
                    return;
                }
                bool protocolFinish = state.LastResponse?.Finish == ModelFinish.Length || state.LastResponse?.Finish == ModelFinish.Refused ||
                    state.LastResponse?.Finish == ModelFinish.ContentFiltered;
                if ((state.Status != RunStatus.Completed && state.Status != RunStatus.WaitingForTools &&
                    !(state.Status == RunStatus.WaitingForUser && protocolFinish)))
                {
                    await ErrorAsync(http, 409, "response_unavailable", "Run status: " + state.Status, id).ConfigureAwait(false);
                    return;
                }
                await http.Response.WriteAsJsonAsync(responses.WriteResponse(state, server.ModelAlias), http.RequestAborted).ConfigureAwait(false);
            }
            catch (ArgumentException) { await ErrorAsync(http, 400, "invalid_request_error", "Invalid response ID.").ConfigureAwait(false); }
        }

        private static string SafeHeader(string text)
        {
            var builder = new StringBuilder();
            foreach (char c in text) if (c >= 32 && c < 127) builder.Append(c);
            return builder.ToString();
        }

        internal static Task ErrorAsync(HttpContext context, int status, string code, string message, string? runId = null)
        {
            context.Response.StatusCode = status;
            return context.Response.WriteAsJsonAsync(new
            {
                error = new { message, type = code == "invalid_request_error" ? code : "api_error", code, param = (string?)null, run_id = runId }
            }, context.RequestAborted);
        }
    }
}
