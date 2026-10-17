using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding
{
    // Shared tool plumbing: narrow JSON dialect for output and the rule that
    // expected workspace failures are corrective observations (Failed / no
    // effect), never exceptions that would force task-level reconciliation.
    public abstract class CodingToolBase : IContextualTool
    {
        protected static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        protected WorkspaceOptions Options { get; }
        public abstract ToolDefinition Definition { get; }

        protected CodingToolBase(WorkspaceOptions? options = null) { Options = options ?? new WorkspaceOptions(); }

        public async Task<ToolOutcome> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await RunAsync(arguments, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (WorkspaceAccessException ex) { return Fail(ex.Message); }
            catch (Exception ex) when (IsExpected(ex))
            {
                // Read/write operational errors (locked path, IO error, denied
                // access) are model-correctable and carry no confirmed effect.
                return Fail(ex.GetType().Name + ": " + ex.Message);
            }
        }

        protected abstract Task<ToolOutcome> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken);

        // Coding tools are context-bound; the non-contextual ITool path is never used.
        Task<string> ITool.ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
            => throw new NotSupportedException("Coding tools require a ToolExecutionContext.");

        private static bool IsExpected(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;

        protected static ToolOutcome Fail(string message) => new ToolOutcome
        {
            Status = ToolCallStatus.Failed, Output = message, Effect = EffectState.None
        };

        protected static ToolOutcome Ok(string output) => new ToolOutcome
        {
            Status = ToolCallStatus.Succeeded, Output = output, Effect = EffectState.None
        };

        protected static ToolDefinition Def(string name, string description, string schema)
        {
            using var doc = JsonDocument.Parse(schema);
            return new ToolDefinition { Name = name, Description = description, Parameters = doc.RootElement.Clone(), Strict = true };
        }

        protected static string String(JsonElement args, string name, string fallback = "")
        {
            if (args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? fallback;
            return fallback;
        }

        protected static int Int(JsonElement args, string name, int fallback)
        {
            if (args.TryGetProperty(name, out var value) && value.TryGetInt32(out int parsed)) return parsed;
            return fallback;
        }

        protected static string ToJson(object value) => JsonSerializer.Serialize(value, Json);
    }
}
