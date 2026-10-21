using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Model.OpenAI;

namespace MiniHarness.LiveTests
{
    // Explicit opt-in only: this executable makes real, potentially billable calls.
    // Credentials stay in memory; reports contain only sanitized diagnostics.
    public static partial class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.FirstOrDefault() == "--runtime") return await ValidateRuntimeAsync(args.Skip(1).ToArray());
            if (args.FirstOrDefault() == "--coding") return await ValidateCodingAsync(args.Skip(1).ToArray());
            if (args.FirstOrDefault() == "--coding-fixture") return await ValidateCodingAsync(new[] { "Offline" });
            if (args.FirstOrDefault() == "--long-running") return await ValidateLongRunningAsync(args.Skip(1).ToArray());
            if (args.FirstOrDefault() == "--context-recovery") return await ValidateContextRecoveryAsync(args.Skip(1).ToArray());
            if (args.FirstOrDefault() == "--resume-context") return await ResumeContextRecoveryAsync(args.Skip(1).ToArray());
            if (args.FirstOrDefault() == "--audit-context") return await AuditContextRecoveryAsync(args.Skip(1).ToArray());
            string key = "";
            try
            {
                key = Setting("CODING_PLAN_APIKEY");
                string modelName = Setting("CODING_PLAN_MODEL");
                var endpoint = new Uri(Setting("CODING_PLAN_URL"), UriKind.Absolute);
                if (endpoint.Scheme != "https" || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
                    throw new ArgumentException("CODING_PLAN_URL must be an HTTPS API base URL without credentials, query, or fragment.");
                string baseUrl = endpoint.AbsoluteUri.TrimEnd('/') + "/";
                var results = new List<ProbeResult>();
                foreach (string protocol in new[] { "Responses", "ChatCompletions" })
                {
                    using var handler = new CaptureHandler();
                    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                    var options = new ModelOptions
                    {
                        Protocol = protocol, BaseUrl = baseUrl, Model = modelName, ApiKey = key,
                        TimeoutSeconds = 90, MaxRetries = 0
                    };
                    options.Validate();
                    IModelProvider provider = protocol == "Responses"
                        ? new ResponsesModelProvider(client, options) : new ChatCompletionsModelProvider(client, options);
                    results.Add(await ProbeAsync("text", protocol, handler, async result =>
                    {
                        var response = await provider.GenerateAsync(Request(modelName, "Reply with exactly CODING_PLAN_OK and nothing else."), CancellationToken.None);
                        result.Finish = response.Finish.ToString(); result.Text = response.Message.Text;
                        result.Usage = response.Usage; result.ModelCalls = 1;
                        result.Passed = response.Finish == ModelFinish.Stop && response.Message.ToolCalls.Count == 0 && response.Message.Text.Trim() == "CODING_PLAN_OK";
                    }, key));

                    // Avoid further generations on an endpoint that failed basic validation.
                    if (!results[results.Count - 1].Passed) continue;
                    results.Add(await ProbeAsync("tool_loop", protocol, handler, async result =>
                    {
                        var limits = new HarnessOptions
                        {
                            MaxModelCalls = 3, MaxToolCalls = 2, MaxTotalTokens = 8192, MaxOutputTokens = 512,
                            SystemInstructions = "Follow the requested tool call. After receiving its result, answer with only the numeric result."
                        };
                        var runner = new AgentRunner(provider, new ContextBuilder(limits), new ToolRegistry(new[] { new AddTool() }),
                            new AllowListToolPolicy(new[] { "probe_add" }), new ToolExecutor(limits), new NaturalStopPolicy(), new VolatileStore(), limits);
                        var request = Request(modelName, "Use probe_add with a=17 and b=25. Once its result is available, reply with only that result.");
                        request.Options.ToolChoice = "probe_add";
                        var state = await runner.RunAsync(request, CancellationToken.None);
                        result.Finish = state.Status.ToString(); result.Text = state.LastResponse?.Message.Text;
                        result.Usage = state.Usage; result.ModelCalls = state.ModelCalls; result.ToolCalls = state.ToolCalls;
                        result.ToolResult = state.Executions.FirstOrDefault()?.Result?.Output;
                        result.Passed = state.Status == RunStatus.Completed && result.ToolResult == "42" && result.Text?.Trim() == "42";
                        if (!result.Passed) result.Error = state.StopReason;
                    }, key));
                }
                var report = new
                {
                    CheckedAtUtc = DateTimeOffset.UtcNow, Endpoint = baseUrl, Model = modelName,
                    DeclaredContext = "1049K tokens (user supplied)", DeclaredMaximumOutput = "256K tokens (user supplied)",
                    LimitsStressTested = false, ProbeMaximumOutputTokens = 512, Results = results
                };
                var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
                string output = Sanitize(JsonSerializer.Serialize(report, jsonOptions), key);
                string reportPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(MiniHarness.Testing.ValidationPaths.Root, "llm-validation.json");
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await File.WriteAllTextAsync(reportPath, output);
                Console.WriteLine("Report: " + reportPath);
                Console.WriteLine($"{results.Count(r => r.Passed)}/{results.Count} live probes passed.");
                return results.Count == 4 && results.All(r => r.Passed) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine(Sanitize(ex.GetType().Name + ": " + ex.Message, key));
                return 1;
            }
        }

        private static ModelRequest Request(string model, string prompt) => new ModelRequest
        {
            Model = model, Messages = { new Message { Text = prompt } }, Options = new GenerationOptions { MaxOutputTokens = 512 }
        };

        private static async Task<ProbeResult> ProbeAsync(string name, string protocol, CaptureHandler handler, Func<ProbeResult, Task> action, string key)
        {
            handler.Requests.Clear();
            var result = new ProbeResult { Probe = name, Protocol = protocol };
            var timer = Stopwatch.StartNew();
            try { await action(result); }
            catch (Exception ex) { result.Error = ex.GetType().Name + ": " + ex.Message; }
            result.ElapsedMilliseconds = timer.ElapsedMilliseconds;
            result.Requests.AddRange(handler.Requests);
            Console.WriteLine(Sanitize(JsonSerializer.Serialize(result), key));
            return result;
        }

        private static string Setting(string name)
        {
            string? value = CodingPlanConfiguration.Read(name);
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Missing environment variable: " + name);
            return value.Trim();
        }
        private static string Sanitize(string value, string key)
        {
            if (key.Length == 0) return value;
            string quoted = JsonSerializer.Serialize(key);
            return value.Replace(key, "[REDACTED]", StringComparison.Ordinal)
                .Replace(quoted.Substring(1, quoted.Length - 2), "[REDACTED]", StringComparison.Ordinal);
        }

        private sealed class CaptureHandler : DelegatingHandler
        {
            public List<HttpObservation> Requests { get; } = new List<HttpObservation>();
            public CaptureHandler() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var timer = Stopwatch.StartNew();
                var response = await base.SendAsync(request, cancellationToken);
                var observation = new HttpObservation
                {
                    Status = (int)response.StatusCode, ContentType = response.Content.Headers.ContentType?.MediaType,
                    Path = request.RequestUri!.AbsolutePath
                };
                Requests.Add(observation);
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                observation.ElapsedMilliseconds = timer.ElapsedMilliseconds;
                try
                {
                    using var json = JsonDocument.Parse(content);
                    var root = json.RootElement;
                    if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String) observation.Model = model.GetString();
                    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    {
                        if (error.TryGetProperty("code", out var code)) observation.ErrorCode = code.ToString();
                        if (error.TryGetProperty("message", out var message)) observation.ErrorMessage = message.ToString();
                    }
                }
                catch (JsonException) { observation.ErrorMessage = "Non-JSON response body (not logged)."; }
                return response;
            }
        }

        private sealed class AddTool : ITool
        {
            public ToolDefinition Definition { get; }
            public AddTool()
            {
                using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"number\"},\"b\":{\"type\":\"number\"}},\"required\":[\"a\",\"b\"],\"additionalProperties\":false}");
                Definition = new ToolDefinition { Name = "probe_add", Description = "Add two numbers and return the actual sum.", Parameters = schema.RootElement.Clone(), Strict = true };
            }
            public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult((arguments.GetProperty("a").GetDecimal() + arguments.GetProperty("b").GetDecimal()).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        private sealed class VolatileStore : IRunStore
        {
            public Task SaveAsync(RunState state, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<RunState?> LoadAsync(string id, CancellationToken cancellationToken) => Task.FromResult<RunState?>(null);
        }
        private sealed class ProbeResult
        {
            public string Probe { get; set; } = "";
            public string Protocol { get; set; } = "";
            public bool Passed { get; set; }
            public string? Finish { get; set; }
            public string? Text { get; set; }
            public string? ToolResult { get; set; }
            public int ModelCalls { get; set; }
            public int ToolCalls { get; set; }
            public TokenUsage? Usage { get; set; }
            public long ElapsedMilliseconds { get; set; }
            public string? Error { get; set; }
            public List<HttpObservation> Requests { get; } = new List<HttpObservation>();
        }
        private sealed class HttpObservation
        {
            public int Status { get; set; }
            public string? ContentType { get; set; }
            public string? Path { get; set; }
            public string? Model { get; set; }
            public string? ErrorCode { get; set; }
            public string? ErrorMessage { get; set; }
            public long ElapsedMilliseconds { get; set; }
        }
    }
}
