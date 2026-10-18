using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public sealed class ContextLimitException : Exception
    {
        public ContextLimitException() : base("Context character limit exceeded; no history was silently dropped.") { }
    }

    public sealed class ContextBuilder : IContextBuilder
    {
        private readonly HarnessOptions options;
        private readonly IModelRequestSizer? sizer;
        public ContextBuilder(HarnessOptions options, IModelRequestSizer? sizer = null) { this.options = options; this.sizer = sizer; }

        public ModelRequest Build(RunState state, IReadOnlyList<ToolDefinition> localTools)
        {
            var request = new ModelRequest
            {
                Model = state.Request.Model,
                Messages = new List<Message>(state.Request.Messages),
                Tools = state.Request.Tools.Concat(localTools).ToList(),
                Options = new GenerationOptions
                {
                    MaxOutputTokens = Math.Min(state.Request.Options.MaxOutputTokens, options.MaxOutputTokens),
                    Temperature = state.Request.Options.Temperature,
                    ToolChoice = state.Request.Options.ToolChoice
                }
            };
            if (!string.IsNullOrWhiteSpace(options.SystemInstructions))
                request.Messages.Insert(0, new Message { Role = "system", Text = options.SystemInstructions });
            if (!string.IsNullOrWhiteSpace(state.Request.Instructions))
                request.Messages.Insert(string.IsNullOrWhiteSpace(options.SystemInstructions) ? 0 : 1,
                    new Message { Role = "system", Text = state.Request.Instructions });
            // Deterministic capacity guard, not a tokenizer or semantic compactor.
            int characters = sizer?.MeasureCharacters(request) ?? JsonSerializer.Serialize(new
            {
                request.Model, request.Instructions, request.Tools, request.Options,
                Messages = request.Messages.Select(m => new
                {
                    m.Role, Text = m.ProviderItemsAreComplete ? null : m.Text,
                    ToolCallId = m.ProviderItemsAreComplete ? null : m.ToolCallId,
                    ToolCalls = m.ProviderItemsAreComplete ? null : m.ToolCalls,
                    m.ProviderItems
                })
            }).Length;
            if (characters > options.MaxContextCharacters)
                throw new ContextLimitException();
            return request;
        }
    }

    public sealed class NaturalStopPolicy : ICompletionPolicy
    {
        public System.Threading.Tasks.Task<CompletionVerdict> EvaluateAsync(RunState state, ModelResponse candidate,
            System.Threading.CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return System.Threading.Tasks.Task.FromResult(new CompletionVerdict
            {
                Accepted = candidate.Finish == ModelFinish.Stop && !string.IsNullOrWhiteSpace(candidate.Message.Text),
                Feedback = "A non-empty, naturally finished answer is required."
            });
        }
    }
}
