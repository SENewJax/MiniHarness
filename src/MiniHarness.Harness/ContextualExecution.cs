using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public sealed class LegacyToolPolicyAdapter : IContextualToolPolicy
    {
        private readonly IToolPolicy policy;
        private readonly string version;
        public string Version => TaskJson.Hash(policy.GetType().FullName + "|" + policy.GetType().Module.ModuleVersionId + "|" +
            (policy is IVersionedToolPolicy versioned ? versioned.Version : version));
        public LegacyToolPolicyAdapter(IToolPolicy policy, string version = "1") { this.policy = policy; this.version = version; }
        public Task<ToolAdmission> CheckAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
            => policy.CheckAsync(call, cancellationToken);
    }
    public sealed class ContextualToolExecutor
    {
        private readonly IToolExecutor executor;
        public ContextualToolExecutor(IToolExecutor executor) { this.executor = executor; }
        public async Task<ToolOutcome> ExecuteAsync(ITool tool, ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var bound = new BoundTool(tool, context);
            var result = await executor.ExecuteAsync(bound, call, cancellationToken).ConfigureAwait(false);
            if (result.Status == ToolCallStatus.Succeeded && bound.Outcome != null)
            { bound.Outcome.Output = result.Output; return bound.Outcome; }
            return new ToolOutcome
            {
                Status = result.Status, Output = result.Output,
                Effect = bound.Invoked ? (result.Status == ToolCallStatus.Succeeded ? EffectState.Confirmed : EffectState.Unknown) : EffectState.None
            };
        }
        private sealed class BoundTool : ITool
        {
            private readonly ITool tool;
            private readonly ToolExecutionContext context;
            public bool Invoked { get; private set; }
            public ToolOutcome? Outcome { get; private set; }
            public ToolDefinition Definition => context.Capabilities.Tools.SingleOrDefault(t => t.Name == tool.Definition.Name) ?? tool.Definition;
            public BoundTool(ITool tool, ToolExecutionContext context) { this.tool = tool; this.context = context; }
            public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
            {
                Invoked = true;
                if (tool is IContextualTool contextual)
                {
                    Outcome = await contextual.ExecuteAsync(arguments, context, cancellationToken).ConfigureAwait(false);
                    return Outcome.Output;
                }
                return await tool.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
