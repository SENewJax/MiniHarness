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

        // The target file was changed (or its state cannot be ruled out), even
        // though the operation is reported as failed. Effect.Unknown drives the
        // runtime into reconciliation rather than falsely claiming "no effect"
        // and allowing an automatic, possibly destructive retry.
        protected static ToolOutcome Uncertain(string message) => new ToolOutcome
        {
            Status = ToolCallStatus.Failed, Output = message, Effect = EffectState.Unknown
        };

        // Write was confirmed on disk, but bookkeeping (the out-of-workspace change
        // ledger) failed afterwards. The code change is real; only its record is
        // missing, so this is an uncertain effect requiring reconciliation, never
        // Effect.None. The message carries the confirmed hash to aid reconciliation.
        protected static ToolOutcome WrittenButUnrecorded(string relativePath, string confirmedHash, string detail) => Uncertain(
            "File was written and confirmed (" + relativePath + ", sha256=" + confirmedHash +
            "), but the change record could not be persisted, so the effect is unrecorded. " +
            "Reconcile the file state; do not re-apply the edit blindly. Underlying error: " + detail);

        protected static ToolOutcome Ok(string output, EffectState effect = EffectState.None) => new ToolOutcome
        {
            Status = ToolCallStatus.Succeeded, Output = output, Effect = effect
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
