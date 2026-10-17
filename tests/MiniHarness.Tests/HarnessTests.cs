using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Server;

namespace MiniHarness.Tests
{
    internal static class HarnessTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("tool result is correlated and fed to next model call", ToolLoop));
            tests.Add(("bad arguments and unknown tool produce corrective observations", CorrectableErrors));
            tests.Add(("policy denial never invokes a tool", Denied));
            tests.Add(("tool timeout is an observation, user cancellation is terminal", TimeoutAndCancellation));
            tests.Add(("model/tool/token/context budgets stop before new work", Budgets));
            tests.Add(("completion policy may reject and continue", CompletionGate));
            tests.Add(("checkpoint failure before execution prevents side effects", CheckpointBeforeEffect));
            tests.Add(("checkpoint failure after effect retains uncertain Started record", CheckpointAfterEffect));
            tests.Add(("file checkpoints round trip and reject path traversal", FileStore));
            tests.Add(("client functions are returned without local execution", ClientTools));
            tests.Add(("duplicate call IDs cannot execute a second time", DuplicateIdentity));
            tests.Add(("truncated calls stop before executing partial arguments", TruncatedCall));
            tests.Add(("required and named tool choices reject ordinary answers", MissingRequiredTool));
            tests.Add(("named tool choice rejects wrong and mixed batches before effects", WrongNamedTool));
            tests.Add(("valid tool choices retain terminal handling and local follow-up auto", ValidToolChoices));
            tests.Add(("omitted required/properties schemas run; bad schemas are rejected at registration", SchemaValidation));
        }

        internal static ModelRequest Request() => new ModelRequest { Model = "test", Messages = { new Message { Text = "test" } } };
        internal static ModelResponse Answer(string text = "done") => new ModelResponse
        {
            Message = new Message { Role = "assistant", Text = text }, Usage = new TokenUsage { InputTokens = 3, OutputTokens = 2 }
        };
        internal static ModelResponse Call(string name = "add", string arguments = "{\"a\":2,\"b\":3}", string id = "call_1")
        {
            var response = Answer(""); response.Finish = ModelFinish.ToolCalls;
            response.Message.ToolCalls.Add(new ToolCall { Id = id, Name = name, Arguments = arguments });
            return response;
        }

        private static AgentRunner Runner(IModelProvider model, MemoryStore? store = null, HarnessOptions? options = null,
            IEnumerable<ITool>? tools = null, IToolPolicy? policy = null, ICompletionPolicy? completion = null)
        {
            options ??= new HarnessOptions();
            return new AgentRunner(model, new ContextBuilder(options), new ToolRegistry(tools ?? new ITool[] { new AddTool() }),
                policy ?? new AllowListToolPolicy(new[] { "add", "slow", "count" }), new ToolExecutor(options),
                completion ?? new NaturalStopPolicy(), store ?? new MemoryStore(), options);
        }

        private static async Task ToolLoop()
        {
            var store = new MemoryStore();
            var model = new ScriptedModel(Call(), Answer("5"));
            var state = await Runner(model, store).RunAsync(Request(), CancellationToken.None);
            Check.Equal(RunStatus.Completed, state.Status);
            Check.Equal(2, state.ModelCalls); Check.Equal(1, state.ToolCalls); Check.Equal(10L, state.Usage.TotalTokens);
            var observation = model.Inputs[1].Messages.Single(m => m.Role == "tool");
            Check.Equal("call_1", observation.ToolCallId);
            Check.True(observation.Text.Contains("Succeeded") || observation.Text.Contains("\"Status\":1"));
            Check.Equal("5", state.Executions[0].Result!.Output);
            Check.True(store.Snapshots.Any(s => s.Executions.Count == 1 && s.Executions[0].Status == ToolCallStatus.Started));
        }

        private static async Task CorrectableErrors()
        {
            var model = new ScriptedModel(Call(arguments: "{\"a\":2}"), Call("missing", "{}", "call_2"), Call(id: "call_3"), Answer());
            var state = await Runner(model).RunAsync(Request(), CancellationToken.None);
            Check.Equal(RunStatus.Completed, state.Status);
            Check.Equal(ToolCallStatus.Failed, state.Executions[0].Status);
            Check.Equal(ToolCallStatus.Rejected, state.Executions[1].Status);
            Check.Equal(ToolCallStatus.Succeeded, state.Executions[2].Status);
        }

        private static async Task Denied()
        {
            var tool = new CountTool();
            var state = await Runner(new ScriptedModel(Call("count", "{}"), Answer()), tools: new[] { tool },
                policy: new AllowListToolPolicy(Array.Empty<string>())).RunAsync(Request(), CancellationToken.None);
            Check.Equal(0, tool.Count); Check.Equal(ToolCallStatus.Rejected, state.Executions[0].Status);
        }

        private static async Task TimeoutAndCancellation()
        {
            var slow = new CountTool("slow", true);
            var options = new HarnessOptions { ToolTimeoutSeconds = 1 };
            var timed = await Runner(new ScriptedModel(Call("slow", "{}"), Answer()), tools: new[] { slow }, options: options)
                .RunAsync(Request(), CancellationToken.None);
            Check.Equal(ToolCallStatus.TimedOut, timed.Executions[0].Status);
            using var cancel = new CancellationTokenSource();
            var store = new MemoryStore();
            var running = Runner(new ScriptedModel(Call("slow", "{}")), store, tools: new[] { new CountTool("slow", true) })
                .RunAsync(Request(), cancel.Token);
            cancel.CancelAfter(50);
            var cancelled = await running;
            Check.Equal(RunStatus.Cancelled, cancelled.Status);
            Check.Equal(RunStatus.Cancelled, store.Snapshots.Last().Status);
        }

        private static async Task Budgets()
        {
            var one = await Runner(new ScriptedModel(Call()), options: new HarnessOptions { MaxModelCalls = 1 })
                .RunAsync(Request(), CancellationToken.None);
            Check.Equal(RunStatus.BudgetExhausted, one.Status); Check.Equal(1, one.ModelCalls);
            var tool = new CountTool();
            var noTools = await Runner(new ScriptedModel(Call("count", "{}")), tools: new[] { tool },
                options: new HarnessOptions { MaxToolCalls = 0 }).RunAsync(Request(), CancellationToken.None);
            Check.Equal(RunStatus.BudgetExhausted, noTools.Status); Check.Equal(0, tool.Count);
            var tokens = await Runner(new ScriptedModel(Call("count", "{}")), tools: new[] { tool },
                options: new HarnessOptions { MaxTotalTokens = 5 }).RunAsync(Request(), CancellationToken.None);
            Check.Equal(RunStatus.BudgetExhausted, tokens.Status); Check.Equal(0, tool.Count);
            var unknown = Answer(); unknown.Usage.Known = false;
            var missing = await Runner(new ScriptedModel(unknown)).RunAsync(Request(), CancellationToken.None);
            Check.Equal("unknown_token_usage", missing.StopReason);
            var context = await Runner(new ScriptedModel(), options: new HarnessOptions { MaxContextCharacters = 1 })
                .RunAsync(Request(), CancellationToken.None);
            Check.Equal("context_capacity", context.StopReason); Check.Equal(0, context.ModelCalls);
        }

        private static async Task CompletionGate()
        {
            var model = new ScriptedModel(Answer("candidate"), Answer("verified"));
            var state = await Runner(model, completion: new Gate()).RunAsync(Request(), CancellationToken.None);
            Check.Equal(RunStatus.Completed, state.Status); Check.Equal(2, state.ModelCalls);
            Check.True(model.Inputs[1].Messages.Any(m => m.Text == "Missing evidence"));
        }

        private static async Task CheckpointBeforeEffect()
        {
            var store = new MemoryStore { FailWhen = s => s.Executions.Any(e => e.Status == ToolCallStatus.Started) };
            var tool = new CountTool();
            await Check.ThrowsAsync<IOException>(() => Runner(new ScriptedModel(Call("count", "{}")), store, tools: new[] { tool })
                .RunAsync(Request(), CancellationToken.None));
            Check.Equal(0, tool.Count);
        }

        private static async Task CheckpointAfterEffect()
        {
            var store = new MemoryStore { FailWhen = s => s.Executions.Any(e => e.Result != null) };
            var tool = new CountTool();
            await Check.ThrowsAsync<IOException>(() => Runner(new ScriptedModel(Call("count", "{}")), store, tools: new[] { tool })
                .RunAsync(Request(), CancellationToken.None));
            Check.Equal(1, tool.Count);
            Check.Equal(ToolCallStatus.Started, store.Snapshots.Last().Executions[0].Status);
        }

        private static async Task FileStore()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "miniharness-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FileRunStore(path);
                var state = new RunState { Request = Request(), Status = RunStatus.WaitingForUser };
                await store.SaveAsync(state, CancellationToken.None);
                var loaded = await store.LoadAsync(state.Id, CancellationToken.None);
                Check.Equal(state.Status, loaded!.Status); Check.Equal("test", loaded.Request.Messages[0].Text);
                await Check.ThrowsAsync<ArgumentException>(() => store.LoadAsync("../../outside", CancellationToken.None));
                Check.True(!System.IO.Directory.EnumerateFiles(path, "*.tmp").Any());
            }
            finally { System.IO.Directory.Delete(path, true); }
        }

        private static async Task ClientTools()
        {
            var request = Request();
            request.Tools.Add(new CountTool("external").Definition);
            var state = await Runner(new ScriptedModel(Call("external", "{}"))).RunAsync(request, CancellationToken.None);
            Check.Equal(RunStatus.WaitingForTools, state.Status); Check.Equal(0, state.Executions.Count);
        }

        private static async Task DuplicateIdentity()
        {
            var tool = new CountTool();
            await Check.ThrowsAsync<ModelException>(() => Runner(new ScriptedModel(Call("count", "{}"), Call("count", "{}")),
                tools: new[] { tool }).RunAsync(Request(), CancellationToken.None));
            Check.Equal(1, tool.Count);
        }

        private static async Task TruncatedCall()
        {
            var tool = new CountTool();
            var response = Call("count", "{"); response.Finish = ModelFinish.Length;
            var state = await Runner(new ScriptedModel(response), tools: new[] { tool }).RunAsync(Request(), CancellationToken.None);
            Check.Equal(RunStatus.WaitingForUser, state.Status); Check.Equal(0, tool.Count);
        }

        private static async Task MissingRequiredTool()
        {
            foreach (string choice in new[] { "required", "count" })
            {
                var request = Request(); request.Options.ToolChoice = choice;
                var store = new MemoryStore(); var tool = new CountTool();
                await Check.ThrowsAsync<ModelException>(() => Runner(new ScriptedModel(Answer()), store, tools: new[] { tool })
                    .RunAsync(request, CancellationToken.None));
                Check.Equal(0, tool.Count);
                Check.Equal(RunStatus.Failed, store.Snapshots.Last().Status);
                Check.Equal(0, store.Snapshots.Last().Executions.Count);
            }
        }

        private static async Task WrongNamedTool()
        {
            foreach (bool mixed in new[] { false, true })
            {
                var request = Request(); request.Options.ToolChoice = "weather";
                request.Tools.Add(new CountTool("weather").Definition);
                var response = Call("count", "{}");
                if (mixed) response.Message.ToolCalls.Insert(0, new ToolCall { Id = "weather_1", Name = "weather" });
                var store = new MemoryStore(); var tool = new CountTool();
                await Check.ThrowsAsync<ModelException>(() => Runner(new ScriptedModel(response, Answer()), store, tools: new[] { tool })
                    .RunAsync(request, CancellationToken.None));
                Check.Equal(0, tool.Count);
                Check.Equal(RunStatus.Failed, store.Snapshots.Last().Status);
                Check.Equal(0, store.Snapshots.Last().Executions.Count);
            }
        }

        private static async Task ValidToolChoices()
        {
            foreach (string choice in new[] { "auto", "required", "count" })
            {
                var request = Request(); request.Options.ToolChoice = choice;
                var model = new ScriptedModel(Call("count", "{}"), Answer()); var tool = new CountTool();
                var state = await Runner(model, tools: new[] { tool }).RunAsync(request, CancellationToken.None);
                Check.Equal(RunStatus.Completed, state.Status); Check.Equal(1, tool.Count);
                Check.Equal(choice, model.Inputs[0].Options.ToolChoice);
                Check.Equal("auto", model.Inputs[1].Options.ToolChoice);
            }
            foreach (string choice in new[] { "auto", "none" })
            {
                var request = Request(); request.Options.ToolChoice = choice;
                Check.Equal(RunStatus.Completed, (await Runner(new ScriptedModel(Answer())).RunAsync(request, CancellationToken.None)).Status);
            }
            var none = Request(); none.Options.ToolChoice = "none"; var deniedTool = new CountTool();
            await Check.ThrowsAsync<ModelException>(() => Runner(new ScriptedModel(Call("count", "{}")), tools: new[] { deniedTool })
                .RunAsync(none, CancellationToken.None));
            Check.Equal(0, deniedTool.Count);
            // Truncation/refusal remain explicit terminal states even when a tool was requested.
            foreach (var finish in new[] { ModelFinish.Length, ModelFinish.Refused, ModelFinish.ContentFiltered })
            {
                var request = Request(); request.Options.ToolChoice = "required";
                var response = Answer(); response.Finish = finish;
                Check.Equal(RunStatus.WaitingForUser, (await Runner(new ScriptedModel(response)).RunAsync(request, CancellationToken.None)).Status);
            }
        }

        private static async Task SchemaValidation()
        {
            // Omitted "required" and "properties" is a valid zero-argument schema and executes.
            var optional = new SchemaTool("optional", "{\"type\":\"object\",\"additionalProperties\":false}");
            var ran = await Runner(new ScriptedModel(Call("optional", "{}"), Answer()), tools: new[] { optional },
                policy: new AllowListToolPolicy(new[] { "optional" })).RunAsync(Request(), CancellationToken.None);
            Check.Equal(RunStatus.Completed, ran.Status);
            Check.Equal(1, optional.Count);

            // Unsupported/nested and malformed dialects are rejected at registration, not at first call.
            foreach (string bad in new[]
            {
                "{\"type\":\"array\"}",
                "{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\"}}}",
                "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}},\"required\":[\"missing\"]}",
                "{\"type\":\"object\",\"required\":\"a\"}"
            })
            {
                await Check.ThrowsAsync<ArgumentException>(() =>
                {
                    _ = new ToolRegistry(new[] { new SchemaTool("bad", bad) });
                    return Task.CompletedTask;
                });
            }

            // Pre-execution validation failure is a corrective observation with no effect,
            // never an uncertain side effect that would demand reconciliation.
            var store = new MemoryStore();
            var requireA = new SchemaTool("requirea",
                "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}},\"required\":[\"a\"]}");
            var state = await Runner(new ScriptedModel(Call("requirea", "{}"), Answer()), store,
                tools: new[] { requireA }, policy: new AllowListToolPolicy(new[] { "requirea" }))
                .RunAsync(Request(), CancellationToken.None);
            Check.Equal(0, requireA.Count);
            Check.Equal(ToolCallStatus.Failed, state.Executions[0].Status);
            Check.Equal(RunStatus.Completed, state.Status);
            Check.True(!state.StopReason?.Contains("Reconcil", StringComparison.Ordinal) ?? true, "must not enter reconciliation");
        }

        private sealed class Gate : ICompletionPolicy
        {
            public Task<CompletionVerdict> EvaluateAsync(RunState state, ModelResponse candidate, CancellationToken cancellationToken)
                => Task.FromResult(new CompletionVerdict { Accepted = state.ModelCalls == 2, CanContinue = true, Feedback = "Missing evidence" });
        }
        private sealed class SchemaTool : ITool
        {
            public int Count { get; private set; }
            public ToolDefinition Definition { get; }
            public SchemaTool(string name, string schema)
            {
                using var json = JsonDocument.Parse(schema);
                Definition = new ToolDefinition { Name = name, Parameters = json.RootElement.Clone() };
            }
            public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
            {
                Count++;
                return Task.FromResult("ok");
            }
        }
        private sealed class CountTool : ITool
        {
            public int Count { get; private set; }
            private readonly bool slow;
            public ToolDefinition Definition { get; }
            public CountTool(string name = "count", bool slow = false)
            {
                this.slow = slow;
                using var json = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
                Definition = new ToolDefinition { Name = name, Parameters = json.RootElement.Clone() };
            }
            public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
            {
                Count++;
                if (slow) await Task.Delay(Timeout.Infinite, cancellationToken);
                return "executed";
            }
        }
    }

    internal sealed class ScriptedModel : IModelProvider
    {
        private readonly Queue<ModelResponse> responses;
        public List<ModelRequest> Inputs { get; } = new List<ModelRequest>();
        public ScriptedModel(params ModelResponse[] responses) { this.responses = new Queue<ModelResponse>(responses); }
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inputs.Add(JsonSerializer.Deserialize<ModelRequest>(JsonSerializer.Serialize(request))!);
            return Task.FromResult(responses.Dequeue());
        }
    }
    internal sealed class MemoryStore : IRunStore
    {
        public int FailFromSave { get; set; } = int.MaxValue;
        public Func<RunState, bool>? FailWhen { get; set; }
        public List<RunState> Snapshots { get; } = new List<RunState>();
        private int saveCount;
        public Task SaveAsync(RunState state, CancellationToken cancellationToken)
        {
            if (++saveCount >= FailFromSave || FailWhen?.Invoke(state) == true) throw new IOException("Injected storage failure");
            Snapshots.Add(JsonSerializer.Deserialize<RunState>(JsonSerializer.Serialize(state))!);
            return Task.CompletedTask;
        }
        public Task<RunState?> LoadAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult(Snapshots.LastOrDefault(s => s.Id == id));
    }
}
