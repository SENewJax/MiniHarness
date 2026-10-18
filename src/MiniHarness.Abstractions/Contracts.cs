using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHarness.Abstractions
{
    public interface IModelProvider
    {
        Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken);
    }
    public interface IModelRequestSizer
    {
        int MeasureCharacters(ModelRequest request);
    }

    public interface IAgentRunner
    {
        Task<RunState> RunAsync(ModelRequest request, CancellationToken cancellationToken);
    }

    public interface IContextBuilder
    {
        ModelRequest Build(RunState state, IReadOnlyList<ToolDefinition> localTools);
    }

    public interface ITool
    {
        ToolDefinition Definition { get; }
        Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
    }

    // Tools that legitimately run longer than the default tool timeout
    // (for example build/test execution) declare their own ceiling. The
    // executor still owns the hard cancellation; this only widens the bound.
    public interface IToolTimeout
    {
        int TimeoutSeconds { get; }
    }

    public interface IToolRegistry
    {
        IReadOnlyList<ToolDefinition> Definitions { get; }
        bool TryGet(string name, out ITool? tool);
    }

    public interface IToolPolicy
    {
        Task<ToolAdmission> CheckAsync(ToolCall call, CancellationToken cancellationToken);
    }

    public interface IToolExecutor
    {
        Task<ToolResult> ExecuteAsync(ITool tool, ToolCall call, CancellationToken cancellationToken);
    }

    public interface IRunStore
    {
        Task SaveAsync(RunState state, CancellationToken cancellationToken);
        Task<RunState?> LoadAsync(string id, CancellationToken cancellationToken);
    }

    public interface ICompletionPolicy
    {
        Task<CompletionVerdict> EvaluateAsync(RunState state, ModelResponse candidate, CancellationToken cancellationToken);
    }
}
