using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Server
{
    // Tools are host capabilities, not model-provider behavior.
    public sealed class EchoTool : ITool
    {
        public ToolDefinition Definition { get; } = Create("echo", "Return the supplied text.",
            "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}");
        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(arguments.GetProperty("text").GetString()!);
        }
        internal static ToolDefinition Create(string name, string description, string schema)
        {
            using var json = JsonDocument.Parse(schema);
            return new ToolDefinition { Name = name, Description = description, Parameters = json.RootElement.Clone(), Strict = true };
        }
    }
    public sealed class AddTool : ITool
    {
        public ToolDefinition Definition { get; } = EchoTool.Create("add", "Add two decimal numbers.",
            "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"number\"},\"b\":{\"type\":\"number\"}},\"required\":[\"a\",\"b\"],\"additionalProperties\":false}");
        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!arguments.GetProperty("a").TryGetDecimal(out decimal a) || !arguments.GetProperty("b").TryGetDecimal(out decimal b))
                throw new ArgumentException("Numbers exceed decimal range.");
            try { return Task.FromResult(checked(a + b).ToString(CultureInfo.InvariantCulture)); }
            catch (OverflowException) { throw new ArgumentException("Sum exceeds decimal range."); }
        }
    }
    public sealed class UtcTimeTool : ITool
    {
        public ToolDefinition Definition { get; } = EchoTool.Create("utc_time", "Return the current UTC time.",
            "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
        public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
    }
}
