using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Model.OpenAI;

namespace MiniHarness.LiveTests
{
    public static partial class Program
    {
        private static async Task<int> ValidateRuntimeAsync(string[] args)
        {
            string key = "";
            try
            {
                key = Setting("CODING_PLAN_APIKEY");
                string reportPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(MiniHarness.Testing.ValidationPaths.Root, "v0-runtime-validation.json");
                string checkpoints = Path.Combine(Path.GetDirectoryName(reportPath)!, "runtime-checkpoints");
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                var results = new List<RuntimeProbe>();
                foreach (string protocol in new[] { "Responses", "ChatCompletions" })
                {
                    var result = new RuntimeProbe { Protocol = protocol }; var timer = Stopwatch.StartNew();
                    using var capture = new CaptureHandler(); using var client = new HttpClient(capture) { Timeout = Timeout.InfiniteTimeSpan };
                    try
                    {
                        var options = new ModelOptions { Protocol = protocol, MaxRetries = 0, TimeoutSeconds = 90 };
                        if (!CodingPlanConfiguration.ApplyWhenUnconfigured(options)) throw new ArgumentException("Set CODING_PLAN_URL, CODING_PLAN_APIKEY and CODING_PLAN_MODEL.");
                        options.Validate();
                        IModelProvider provider = protocol == "Responses" ? new ResponsesModelProvider(client, options) : new ChatCompletionsModelProvider(client, options);
                        var binding = new ModelBinding
                        {
                            Model = options.Model, Protocol = protocol,
                            ConfigurationVersion = TaskJson.Hash(options.BaseUrl.TrimEnd('/') + "|" + options.Model + "|" + protocol)
                        };
                        var limits = new HarnessOptions { MaxModelCalls = 3, MaxToolCalls = 1, MaxTotalTokens = 12000, MaxOutputTokens = 512 };
                        TaskRuntime Create() => new TaskRuntime(provider, new FileTaskStore(checkpoints), limits, binding,
                            tools: new ToolRegistry(new[] { new AddTool() }), toolPolicy: new AllowListToolPolicy(new[] { "probe_add" }));
                        var request = Request(options.Model, "Call probe_add with a=17 and b=25. After the actual tool result arrives, reply with only the result.");
                        request.Options.ToolChoice = "probe_add";
                        AgentTaskState first;
                        await using (var runtime = Create())
                        {
                            first = (await runtime.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Start, Request = request }, CancellationToken.None)).State;
                            result.TaskId = first.Id; result.FirstTurnText = first.Turns.Last().LastResponse?.Message.Text;
                            result.FirstTurnCalls = first.Budget.ModelCalls;
                            if (first.Turns.Last().Status != TurnStatus.Completed || result.FirstTurnText?.Trim() != "42" || first.Budget.ToolCalls != 1)
                                throw new InvalidOperationException("First turn did not complete the real tool loop: " + first.Wait);
                        }
                        // New runtime AND new store instance: all continuation state comes from disk.
                        await using (var restored = Create())
                        {
                            var checkpoint = (await restored.GetAsync(first.Id, CancellationToken.None))!;
                            result.RestoredTokens = checkpoint.Budget.Usage.TotalTokens;
                            if (checkpoint.Budget.ModelCalls != first.Budget.ModelCalls || checkpoint.Budget.Usage.TotalTokens != first.Budget.Usage.TotalTokens)
                                throw new InvalidOperationException("Checkpoint did not retain cumulative budget.");
                            var follow = Request(options.Model, "What numeric result did the previous tool return? Reply with only that number and do not call tools.");
                            follow.Options.ToolChoice = "none";
                            var second = (await restored.ExecuteAsync(new TaskCommand { Kind = TaskCommandKind.Input, TaskId = first.Id, Request = follow }, CancellationToken.None)).State;
                            result.SecondTurnText = second.Turns.Last().LastResponse?.Message.Text;
                            result.TotalModelCalls = second.Budget.ModelCalls; result.TotalToolCalls = second.Budget.ToolCalls;
                            result.Usage = second.Budget.Usage; result.Turns = second.Turns.Count;
                            result.Passed = second.Id == first.Id && second.Turns.Count == 2 && second.Turns.Last().Status == TurnStatus.Completed &&
                                result.SecondTurnText?.Trim() == "42" && second.Budget.ModelCalls == first.Budget.ModelCalls + 1 &&
                                second.Budget.ToolCalls == 1 && second.Budget.Usage.Known && second.Budget.Usage.TotalTokens > result.RestoredTokens;
                            if (!result.Passed) result.Error = "Second-turn continuity or accounting check failed: " + second.Wait;
                        }
                    }
                    catch (Exception ex) { result.Error = ex.GetType().Name + ": " + ex.Message; }
                    result.Requests.AddRange(capture.Requests); result.ElapsedMilliseconds = timer.ElapsedMilliseconds; results.Add(result);
                    Console.WriteLine(Sanitize(JsonSerializer.Serialize(result, TaskJson.Options), key));
                }
                string report = Sanitize(JsonSerializer.Serialize(new
                {
                    CheckedAtUtc = DateTimeOffset.UtcNow, Scenario = "real tool loop -> durable reload -> second input on the same task",
                    Model = Setting("CODING_PLAN_MODEL"), OutputCapPerCall = 512, Retries = 0, Results = results
                }, TaskJson.Options), key);
                await File.WriteAllTextAsync(reportPath, report);
                Console.WriteLine("Report: " + reportPath);
                Console.WriteLine($"{results.Count(r => r.Passed)}/{results.Count} runtime scenarios passed.");
                return results.All(r => r.Passed) ? 0 : 1;
            }
            catch (Exception ex) { Console.WriteLine(Sanitize(ex.GetType().Name + ": " + ex.Message, key)); return 1; }
        }
        private sealed class RuntimeProbe
        {
            public string Protocol { get; set; } = "";
            public string? TaskId { get; set; }
            public bool Passed { get; set; }
            public string? FirstTurnText { get; set; }
            public string? SecondTurnText { get; set; }
            public int FirstTurnCalls { get; set; }
            public int TotalModelCalls { get; set; }
            public int TotalToolCalls { get; set; }
            public int Turns { get; set; }
            public long RestoredTokens { get; set; }
            public TokenUsage? Usage { get; set; }
            public string? Error { get; set; }
            public long ElapsedMilliseconds { get; set; }
            public List<HttpObservation> Requests { get; } = new List<HttpObservation>();
        }
    }
}
