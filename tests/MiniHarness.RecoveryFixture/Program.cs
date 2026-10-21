using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Protocols.OpenAI;

namespace MiniHarness.RecoveryFixture
{
    public sealed class FixtureMarker { }
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            string root = args[0], phase = args[1]; bool fake = args[2] == "fake";
            Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "workspace"));
            int signalled = 0;
            void Phase(string current)
            {
                if (current == "steering_admitted") Publish(Path.Combine(root, "steering.json"), "{\"accepted\":true}");
                if (current != phase || Interlocked.Exchange(ref signalled, 1) != 0) return;
                Publish(Path.Combine(root, "phase.json"), JsonSerializer.Serialize(new { phase = current, pid = Environment.ProcessId }));
                var limit = DateTimeOffset.UtcNow.AddMinutes(2);
                while (!File.Exists(Path.Combine(root, "release")) && DateTimeOffset.UtcNow < limit) Thread.Sleep(10);
            }
            var configuration = new List<string>
            {
                "--Urls", "http://127.0.0.1:0", "--Server:ApiKey", "", "--Server:WorkspaceRoot", Path.Combine(root, "workspace"),
                "--Server:WorkspaceGrants:0", "read", "--Server:WorkspaceGrants:1", "write", "--Server:WorkspaceGrants:2", "execute",
                "--Storage:Directory", Path.Combine(root, "tasks"), "--Context:Enabled", "true", "--Context:KeepRecentGroups", "2",
                "--Context:MaxSummaryCharacters", "1000", "--Context:SummaryMaxOutputTokens", "256", "--Harness:MaxContextCharacters", "6500",
                "--Harness:MaxModelCalls", "30", "--Harness:MaxTotalTokens", "50000", "--Recovery:Mode", "Manual",
                "--Recovery:AllowUsageReconciliation", "true", "--Recovery:AllowResourceReconciliation", "true", "--Logging:LogLevel:Default", "Error"
            };
            if (fake) configuration.AddRange(new[] { "--Model:BaseUrl", "https://fixture.invalid/v1", "--Model:Model", "test", "--Model:ApiKey", "fixture", "--Model:Protocol", "ChatCompletions" });
            configuration.AddRange(args.Skip(3));
            await using var app = MiniHarness.Server.Program.Build(configuration.ToArray(), configureServices: services =>
            {
                if (!fake) return;
                services.AddSingleton<IModelProvider>(new FixtureModel());
                var registry = (IToolRegistry)services.Last(s => s.ServiceType == typeof(IToolRegistry)).ImplementationInstance!;
                var tools = registry.Definitions.Select(d => { registry.TryGet(d.Name, out var tool); return tool!; }).ToList();
                tools.Add(new CounterTool(root, Phase));
                services.AddSingleton<IToolRegistry>(new ToolRegistry(tools));
                services.AddSingleton<IToolPolicy>(new AllowListToolPolicy(tools.Select(t => t.Definition.Name)));
            });
            app.Services.GetRequiredService<TaskRuntime>().FaultInjection = Phase;
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Publish(Path.Combine(root, "host.json"), JsonSerializer.Serialize(new { address, pid = Environment.ProcessId }));
            await app.WaitForShutdownAsync(); return 0;
        }
        public static void Publish(string path, string content)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content); stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        private sealed class CounterTool : IContextualTool, IVersionedTool
        {
            private readonly string root; private readonly Action<string> phase;
            public CounterTool(string root, Action<string> phase)
            {
                this.root = root; this.phase = phase;
                using var json = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[]}");
                Definition = new ToolDefinition { Name = "fixture_counter", Parameters = json.RootElement.Clone() };
            }
            public string Version => "counter-v1";
            public ToolDefinition Definition { get; }
            public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct) => throw new NotSupportedException();
            public Task<ToolOutcome> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
            {
                string path = Path.Combine(root, "effects.count"); int count = File.Exists(path) ? int.Parse(File.ReadAllText(path)) : 0;
                Publish(path, (++count).ToString()); phase("effect_uncommitted");
                return Task.FromResult(new ToolOutcome { Effect = EffectState.Confirmed, Status = ToolCallStatus.Succeeded, Output = count.ToString() });
            }
        }
        private sealed class FixtureModel : IModelProvider, IModelRequestSizer
        {
            public int MeasureCharacters(ModelRequest request) => new ChatCompletionsProtocol().WriteRequest(request).ToJsonString().Length;
            public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var response = new ModelResponse { Finish = ModelFinish.Stop, Usage = new TokenUsage { InputTokens = 3, OutputTokens = 2 } };
                if (request.InvocationPurpose == ModelInvocationPurpose.Summary)
                {
                    using var source = JsonDocument.Parse(request.Messages.Last().Text);
                    string id = source.RootElement.GetProperty("source")[0].GetProperty("id").GetString()!;
                    response.Message.Text = JsonSerializer.Serialize(new SummaryContent { Background = "Durable fixture observations.", References = { id } }, TaskJson.Options);
                }
                else if (request.Messages.Any(m => m.Text.Contains("resource_changed", StringComparison.Ordinal))) response.Message.Text = "Resource observed.";
                else if (request.Messages.Any(m => m.Text == "resource fixture"))
                {
                    var result = request.Messages.FirstOrDefault(m => m.Role == "tool");
                    string name, arguments;
                    if (result == null) { name = "start_process"; arguments = "{\"profile\":\"tree\"}"; }
                    else { var tool = JsonSerializer.Deserialize<ToolResult>(result.Text, TaskJson.Options)!;
                        var resource = JsonSerializer.Deserialize<ResourceReference>(tool.Output, TaskJson.Options)!;
                        name = "wait_process"; arguments = JsonSerializer.Serialize(new { resource_id = resource.Id, condition = "Exit", wait_ms = 30000 }); }
                    response.Finish = ModelFinish.ToolCalls; response.Message.ToolCalls.Add(new ToolCall { Id = "call_" + name, Name = name, Arguments = arguments });
                }
                else if (request.Messages.Any(m => m.Text == "counter fixture") && !request.Messages.Any(m => m.Role == "tool"))
                { response.Finish = ModelFinish.ToolCalls; response.Message.ToolCalls.Add(new ToolCall { Id = "call_counter", Name = "fixture_counter" }); }
                else response.Message.Text = "done";
                return Task.FromResult(response);
            }
        }
    }
}
