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
using MiniHarness.Harness;
using MiniHarness.Model.OpenAI;

namespace MiniHarness.Server
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            Build(args).Run();
        }

        public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
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
            server.Validate(); model.Validate(); harness.Validate();
            builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = server.MaxRequestBodyBytes);
            builder.Services.AddSingleton(server);
            builder.Services.AddSingleton(model);
            builder.Services.AddSingleton(harness);
            builder.Services.AddSingleton<ITaskStore>(new FileTaskStore(Path.IsPathRooted(storage.Directory)
                ? storage.Directory : Path.Combine(builder.Environment.ContentRootPath, storage.Directory)));
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
            builder.Services.AddSingleton<IToolRegistry>(new ToolRegistry(registeredTools));
            builder.Services.AddSingleton<IToolPolicy>(new AllowListToolPolicy(allowedNames));
            builder.Services.AddSingleton<IToolExecutor, ToolExecutor>();
            builder.Services.AddSingleton<IContextBuilder>(services => new ContextBuilder(harness,
                services.GetRequiredService<IModelProvider>() as IModelRequestSizer));
            builder.Services.AddSingleton<ICompletionPolicy, NaturalStopPolicy>();
            // The workspace gate and verification tool must hash the workspace with
            // exactly the same rules, so they share one revision provider instance.
            IWorkspaceRevisionProvider revisionProvider = coding?.Revision ?? new DirectoryWorkspaceRevision();
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
                    services.GetRequiredService<ILogger<TaskRuntime>>().LogError(error, "Task {TaskId} execution failed after acceptance.", id)));
            builder.Services.AddSingleton<ITaskInitializer, HostTaskInitializer>();
            builder.Services.AddSingleton<TaskApplicationService>(services => new TaskApplicationService(
                services.GetRequiredService<TaskRuntime>(), services.GetRequiredService<ITaskInitializer>()));
            builder.Services.AddSingleton<ITaskApplicationService>(services => services.GetRequiredService<TaskApplicationService>());
            builder.Services.AddSingleton<ITaskRuntime>(services => services.GetRequiredService<ITaskApplicationService>());
            builder.Services.AddSingleton<ProtocolAgentService>();
            builder.Services.AddSingleton<IAgentService>(services => services.GetRequiredService<ProtocolAgentService>());
            builder.Services.AddSingleton<IAgentRunner>(services => services.GetRequiredService<ProtocolAgentService>());
            builder.Services.AddSingleton<AgentApi>();
            builder.Services.AddSingleton(coding != null ? new CodingTaskSetup(coding) : new CodingTaskSetup());
            builder.Services.AddSingleton<TaskApi>();
            var app = builder.Build();
            app.Services.GetRequiredService<AgentApi>().Map(app);
            app.Services.GetRequiredService<TaskApi>().Map(app);
            return app;
        }
    }
}
