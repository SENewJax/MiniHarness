using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniHarness.Abstractions;
using MiniHarness.Coding;
using MiniHarness.Coding.Processes;
using MiniHarness.Harness;
using MiniHarness.Model.OpenAI;

namespace MiniHarness.Server
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length == 4 && args[0] == "--verify-delivery" && args[2] == "--expected-manifest-hash")
            { MiniHarness.Harness.Delivery.DeliveryManifestBuilder.Verify(args[1], args[3], System.Threading.CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine("Delivery integrity and acceptance records verified."); return; }
            Build(args).Run();
        }
        public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure) => Build(args, configure, null);

        public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null, Action<IServiceCollection>? configureServices = null)
        {
            // One composition root and one configuration file for all implementations.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });
            configure?.Invoke(builder);
            var server = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
            var model = builder.Configuration.GetSection("Model").Get<ModelOptions>() ?? new ModelOptions();
            CodingPlanConfiguration.ApplyWhenUnconfigured(model);
            var harness = builder.Configuration.GetSection("Harness").Get<HarnessOptions>() ?? new HarnessOptions();
            var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
            var toolOptions = builder.Configuration.GetSection("Tools").Get<ToolsOptions>() ?? new ToolsOptions();
            var codingOptions = builder.Configuration.GetSection("Coding").Get<CodingOptions>() ?? new CodingOptions();
            var processOptions = builder.Configuration.GetSection("Processes").Get<ProcessServerOptions>() ?? new ProcessServerOptions();
            var contextOptions = builder.Configuration.GetSection("Context").Get<ContextOptions>() ?? new ContextOptions(); contextOptions.Validate();
            var recoveryOptions = builder.Configuration.GetSection("Recovery").Get<RecoveryServerOptions>() ?? new RecoveryServerOptions(); recoveryOptions.Policy().Validate();
            var deliveryOptions = builder.Configuration.GetSection("Delivery").Get<DeliveryServerOptions>() ?? new DeliveryServerOptions();
            builder.Services.AddSingleton(deliveryOptions);
            builder.Services.AddSingleton(contextOptions); builder.Services.AddSingleton(recoveryOptions);
            builder.Services.AddSingleton(processOptions);
            server.Validate(); model.Validate(); harness.Validate();
            builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = server.MaxRequestBodyBytes);
            builder.Services.AddSingleton(server);
            builder.Services.AddSingleton(model);
            builder.Services.AddSingleton(harness);
            builder.Services.AddSingleton<ITaskStore>(new FileTaskStore(Path.IsPathRooted(storage.Directory)
                ? storage.Directory : Path.Combine(builder.Environment.ContentRootPath, storage.Directory), contextOptions.MaxCheckpointBytes));
            var binding = new ModelBinding
            {
                Model = model.Model, Protocol = model.Protocol,
                ConfigurationVersion = TaskJson.Hash(model.BaseUrl.TrimEnd('/') + "|" + model.Model + "|" + model.Protocol)
            };
            builder.Services.AddSingleton(binding);

            // V1 coding capability is opt-in. When enabled, the coding tools are
            // registered alongside the sample tools and share one revision
            // provider with the runtime, so verification fingerprints match the
            // workspace gate exactly.
            CodingComposition? coding = codingOptions.Enabled
                ? new CodingComposition(codingOptions, builder.Environment.ContentRootPath)
                : null;
            var registeredTools = new List<ITool> { new EchoTool(), new AddTool(), new UtcTimeTool() };
            var allowedNames = new List<string>(toolOptions.AllowedNames);
            if (coding != null)
            {
                registeredTools.AddRange(coding.Tools);
                allowedNames.AddRange(coding.Tools.Select(t => t.Definition.Name));
            }
            if (processOptions.Enabled)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Background process ownership is unavailable on this platform.");
                if (processOptions.Limits.MaxPageBytes > harness.MaxToolOutputCharacters) throw new ArgumentException("Process page budget must fit the tool output limit.");
                if (processOptions.PollMilliseconds < 50 || processOptions.PollMilliseconds > 5000) throw new ArgumentException("Invalid process polling interval.");
                processOptions.Limits.Directory = Path.GetFullPath(Path.IsPathRooted(processOptions.Limits.Directory) ? processOptions.Limits.Directory : Path.Combine(builder.Environment.ContentRootPath, processOptions.Limits.Directory));
                if (server.WorkspaceRoot.Length > 0)
                {
                    string workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.WorkspaceRoot));
                    if (processOptions.Limits.Directory.Equals(workspace, StringComparison.OrdinalIgnoreCase) || processOptions.Limits.Directory.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Process state must be outside the source workspace.");
                }
                var sessions = new ProcessSessionManager(processOptions.Limits, processOptions.Profiles);
                var processTools = ProcessTool.CreateAll(sessions);
                registeredTools.AddRange(processTools);
                allowedNames.AddRange(processTools.Select(t => t.Definition.Name));
                // Factory registration lets DI own disposal even when a caller
                // disposes an app without completing its explicit Stop path.
                builder.Services.AddSingleton<ProcessSessionManager>(_ => sessions);
                builder.Services.AddSingleton<IResourceManager>(services => services.GetRequiredService<ProcessSessionManager>());
                builder.Services.AddHostedService<ResourceWakeupService>();
            }
            builder.Services.AddSingleton<IToolRegistry>(new ToolRegistry(registeredTools));
            builder.Services.AddSingleton<IToolPolicy>(new AllowListToolPolicy(allowedNames));
            builder.Services.AddSingleton<IToolExecutor, ToolExecutor>();
            builder.Services.AddSingleton<IContextBuilder>(services => new ContextBuilder(harness,
                services.GetRequiredService<IModelProvider>() as IModelRequestSizer));
            builder.Services.AddSingleton<ICompletionPolicy, NaturalStopPolicy>();
            // The workspace gate and verification tool must hash the workspace with
            // exactly the same rules, so they share one revision provider instance.
            IWorkspaceRevisionProvider revisionProvider = coding?.Revision ?? new DirectoryWorkspaceRevision();
            MiniHarness.Harness.Delivery.DeliveryServices? delivery = null;
            if (deliveryOptions.Enabled)
            {
                deliveryOptions.Limits.Validate();
                foreach (var criterion in deliveryOptions.Template.Criteria.Where(c => c.Kind == CriterionKind.VerificationProfile))
                    criterion.VerifierFingerprint = coding?.Verification.Fingerprint(criterion.Profile) ?? throw new ArgumentException("V4 verification requires Coding profiles.");
                deliveryOptions.Template.PolicyVersion = TaskJson.Hash(System.Text.Json.JsonSerializer.Serialize(new { Contract = MiniHarness.Harness.Acceptance.AcceptanceBinding.SpecHash(deliveryOptions.Template), deliveryOptions.Limits,
                    deliveryOptions.Actor, deliveryOptions.AllowReview, deliveryOptions.AllowAmendment, deliveryOptions.AllowContinue,
                    deliveryOptions.AllowExternalEvidence, deliveryOptions.AllowArtifactImport }, TaskJson.Options));
                MiniHarness.Harness.Acceptance.AcceptanceBinding.Validate(deliveryOptions.Template);
                string deliveryRoot = Path.GetFullPath(Path.IsPathRooted(deliveryOptions.Directory) ? deliveryOptions.Directory : Path.Combine(builder.Environment.ContentRootPath, deliveryOptions.Directory));
                if (server.WorkspaceRoot.Length > 0 && (deliveryRoot == Path.GetFullPath(server.WorkspaceRoot) || deliveryRoot.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.WorkspaceRoot)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Delivery store must be outside the workspace.");
                delivery = new MiniHarness.Harness.Delivery.DeliveryServices { Options = deliveryOptions.Limits,
                    Authorizer = new HostAcceptancePolicy(deliveryOptions), Changes = coding == null ? null : new DeliveryChangeSetProvider(coding.Ledger,
                        new WorkspaceOptions { IgnoredDirectories = new WorkspaceOptions().IgnoredDirectories.Concat(codingOptions.IgnoredDirectories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }),
                    Artifacts = new MiniHarness.Harness.Delivery.FileArtifactStore(deliveryRoot, new[] { codingOptions.ResolveArtifactRoot(builder.Environment.ContentRootPath), server.WorkspaceRoot }.Where(p => p.Length > 0), deliveryOptions.Limits) };
                builder.Services.AddSingleton(delivery);
            }
            ITaskCompletionPolicy taskCompletion = coding != null
                ? new CodingRoutingPolicy(new EvidenceCompletionPolicy())
                : new EvidenceCompletionPolicy();
            builder.Services.AddHttpClient("model", client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            builder.Services.AddSingleton<IModelProvider>(services =>
            {
                var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("model");
                if (model.Protocol == "Responses") return new ResponsesModelProvider(client, model);
                return new ChatCompletionsModelProvider(client, model);
            });
            builder.Services.AddSingleton<TaskRuntime>(services => new TaskRuntime(services.GetRequiredService<IModelProvider>(),
                services.GetRequiredService<ITaskStore>(), harness, binding, context: services.GetRequiredService<IContextBuilder>(), tools: services.GetRequiredService<IToolRegistry>(),
                toolPolicy: services.GetRequiredService<IToolPolicy>(), executor: services.GetRequiredService<IToolExecutor>(),
                completion: taskCompletion, workspaceRevision: revisionProvider,
                resourceManagers: services.GetServices<IResourceManager>(), executionFailed: (id, error) =>
                    services.GetRequiredService<ILogger<TaskRuntime>>().LogError(error, "Task {TaskId} execution failed after acceptance.", id),
                contextOptions: contextOptions, recoveryOptions: recoveryOptions.Policy(), recoveryAuthorizer: services.GetRequiredService<IRecoveryAuthorizer>(), delivery: delivery));
            builder.Services.AddSingleton<ITaskInitializer, HostTaskInitializer>();
            builder.Services.AddSingleton<IRecoveryAuthorizer, HostRecoveryAuthorizer>();
            builder.Services.AddSingleton<TaskApplicationService>(services => new TaskApplicationService(
                services.GetRequiredService<TaskRuntime>(), services.GetRequiredService<ITaskInitializer>()));
            builder.Services.AddSingleton<ITaskApplicationService>(services => services.GetRequiredService<TaskApplicationService>());
            builder.Services.AddSingleton<ITaskRuntime>(services => services.GetRequiredService<ITaskApplicationService>());
            builder.Services.AddSingleton<IResourceOutputAccess>(services => services.GetRequiredService<TaskApplicationService>());
            builder.Services.AddSingleton<TaskRecoveryService>();
            builder.Services.AddHostedService(services => services.GetRequiredService<TaskRecoveryService>());
            builder.Services.AddSingleton<ProtocolAgentService>();
            builder.Services.AddSingleton<IAgentService>(services => services.GetRequiredService<ProtocolAgentService>());
            builder.Services.AddSingleton<IAgentRunner>(services => services.GetRequiredService<ProtocolAgentService>());
            builder.Services.AddSingleton<AgentApi>();
            builder.Services.AddSingleton(coding != null ? new CodingTaskSetup(coding) : new CodingTaskSetup());
            builder.Services.AddSingleton<TaskApi>();
            configureServices?.Invoke(builder.Services);
            var app = builder.Build();
            app.Services.GetRequiredService<AgentApi>().Map(app);
            app.Services.GetRequiredService<TaskApi>().Map(app);
            return app;
        }
    }
}
