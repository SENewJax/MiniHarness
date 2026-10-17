using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public sealed class ToolRegistry : IToolRegistry
    {
        private readonly Dictionary<string, ITool> tools;
        public IReadOnlyList<ToolDefinition> Definitions { get; }
        public ToolRegistry(IEnumerable<ITool> tools)
        {
            this.tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                ToolSchema.ValidateDefinition(tool.Definition);
                // Add catches duplicate names with the same ordinal comparison used at lookup.
                this.tools.Add(tool.Definition.Name, tool);
            }
            Definitions = this.tools.Values.Select(t => TaskJson.Clone(t.Definition)).ToList().AsReadOnly();
        }
        public bool TryGet(string name, out ITool? tool) => tools.TryGetValue(name, out tool);
    }

    // Registration-time gate for the deliberately narrow schema dialect the
    // executor can actually validate. Rejecting unsupported/malformed schemas
    // here turns a latent runtime failure into an explicit composition error.
    internal static class ToolSchema
    {
        private static readonly HashSet<string> PrimitiveTypes = new HashSet<string>(StringComparer.Ordinal)
            { "string", "number", "integer", "boolean" };

        public static void ValidateDefinition(ToolDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
                throw new ArgumentException("Tool name is required.");
            ValidateParameters(definition.Parameters, definition.Name);
        }

        public static void ValidateParameters(JsonElement schema, string toolName)
        {
            if (schema.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Tool '" + toolName + "' must declare an object parameters schema.");
            if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                if (type.GetString() != "object")
                    throw new ArgumentException("Tool '" + toolName + "' parameters schema must have type \"object\".");
            }
            JsonElement properties = default;
            bool hasProperties = schema.TryGetProperty("properties", out properties);
            if (hasProperties)
            {
                if (properties.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Tool '" + toolName + "' properties must be a JSON object.");
                foreach (var property in properties.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object ||
                        !property.Value.TryGetProperty("type", out var propertyType) ||
                        propertyType.ValueKind != JsonValueKind.String ||
                        !PrimitiveTypes.Contains(propertyType.GetString()!))
                        throw new ArgumentException("Tool '" + toolName + "' property '" + property.Name +
                            "' must declare a supported primitive type (string/number/integer/boolean).");
                }
            }
            if (schema.TryGetProperty("required", out var required))
            {
                if (required.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("Tool '" + toolName + "' required must be an array.");
                foreach (var item in required.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("Tool '" + toolName + "' required entries must be property names.");
                    string name = item.GetString()!;
                    if (!hasProperties || !properties.TryGetProperty(name, out _))
                        throw new ArgumentException("Tool '" + toolName + "' requires unknown property '" + name + "'.");
                }
            }
        }
    }

    public sealed class AllowListToolPolicy : IToolPolicy, IVersionedToolPolicy
    {
        private readonly HashSet<string> allowed;
        public string Version => TaskJson.Hash(string.Join("\n", allowed.OrderBy(n => n, StringComparer.Ordinal)));
        public AllowListToolPolicy(IEnumerable<string> names) { allowed = new HashSet<string>(names, StringComparer.Ordinal); }
        public Task<ToolAdmission> CheckAsync(ToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool permitted = allowed.Contains(call.Name);
            return Task.FromResult(new ToolAdmission { Allowed = permitted, Reason = permitted ? "" : "Tool denied by server allow list." });
        }
    }

    public sealed class ToolExecutor : IToolExecutor
    {
        private readonly HarnessOptions options;
        public ToolExecutor(HarnessOptions options) { this.options = options; }

        public async Task<ToolResult> ExecuteAsync(ITool tool, ToolCall call, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.ToolTimeoutSeconds));
            try
            {
                using var arguments = JsonDocument.Parse(call.Arguments);
                ObjectArguments.Validate(arguments.RootElement, tool.Definition.Parameters);
                // Tools must cooperate with cancellation; no abandoned background side effects.
                string output = await tool.ExecuteAsync(arguments.RootElement, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (output.Length > options.MaxToolOutputCharacters)
                    output = output.Substring(0, options.MaxToolOutputCharacters - 20) + "\n[output truncated]";
                return Result(call.Id, ToolCallStatus.Succeeded, output);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return Result(call.Id, ToolCallStatus.TimedOut, "Tool execution timed out; verify effects before retrying.");
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException)
            {
                return Result(call.Id, ToolCallStatus.Failed, ex.Message);
            }
        }
        private static ToolResult Result(string id, ToolCallStatus status, string output)
            => new ToolResult { CallId = id, Status = status, Output = output };
    }

    // Deliberately narrow: server sample tools use flat object schemas with primitive properties.
    // Arbitrary client JSON Schema is forwarded to the upstream provider, never interpreted here.
    // "properties" and "required" may both be omitted; ToolSchema rejects unsupported dialects
    // at registration, so this method only handles missing keys rather than throwing on them.
    internal static class ObjectArguments
    {
        public static void Validate(JsonElement value, JsonElement schema)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Arguments must be a JSON object.");
            bool hasProperties = schema.TryGetProperty("properties", out var properties);
            if (!hasProperties)
            {
                // No declared properties: the dialect only permits an empty argument object.
                if (value.EnumerateObject().Any())
                    throw new ArgumentException("Tool accepts no arguments.");
                return;
            }
            if (properties.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Tool schema properties must be a JSON object.");
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                foreach (var requiredName in required.EnumerateArray())
                    if (!value.TryGetProperty(requiredName.GetString()!, out _))
                        throw new ArgumentException("Missing argument: " + requiredName.GetString());
            foreach (var property in value.EnumerateObject())
            {
                if (!properties.TryGetProperty(property.Name, out var definition))
                    throw new ArgumentException("Unknown argument: " + property.Name);
                if (!definition.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Argument type is not declared: " + property.Name);
                bool valid = type.GetString() switch
                {
                    "string" => property.Value.ValueKind == JsonValueKind.String,
                    "number" => property.Value.ValueKind == JsonValueKind.Number,
                    "integer" => property.Value.TryGetInt64(out _),
                    "boolean" => property.Value.ValueKind == JsonValueKind.True || property.Value.ValueKind == JsonValueKind.False,
                    _ => false
                };
                if (!valid) throw new ArgumentException("Invalid argument type: " + property.Name);
            }
        }
    }
}
