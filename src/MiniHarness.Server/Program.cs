using System;
using System.IO;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniHarness.Abstractions;
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

        public static WebApplication Build(string[] args)
        {
            // One composition root and one configuration file for all implementations.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });
            var server = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
            var model = builder.Configuration.GetSection("Model").Get<ModelOptions>() ?? new ModelOptions();
            CodingPlanConfiguration.ApplyWhenUnconfigured(model);
            var harness = builder.Configuration.GetSection("Harness").Get<HarnessOptions>() ?? new HarnessOptions();
            var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
            var toolOptions = builder.Configuration.GetSection("Tools").Get<ToolsOptions>() ?? new ToolsOptions();
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
            builder.Services.AddSingleton<IToolRegistry>(new ToolRegistry(new ITool[] { new EchoTool(), new AddTool(), new UtcTimeTool() }));
            builder.Services.AddSingleton<IToolPolicy>(new AllowListToolPolicy(toolOptions.AllowedNames));
            builder.Services.AddSingleton<IToolExecutor, ToolExecutor>();
            builder.Services.AddSingleton<IContextBuilder, ContextBuilder>();
            builder.Services.AddSingleton<ICompletionPolicy, NaturalStopPolicy>();
            builder.Services.AddHttpClient("model", client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            builder.Services.AddSingleton<IModelProvider>(services =>
            {
                var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("model");
                if (model.Protocol == "Responses") return new ResponsesModelProvider(client, model);
                return new ChatCompletionsModelProvider(client, model);
            });
            builder.Services.AddSingleton<TaskRuntime>(services => new TaskRuntime(services.GetRequiredService<IModelProvider>(),
                services.GetRequiredService<ITaskStore>(), harness, binding, tools: services.GetRequiredService<IToolRegistry>(),
                toolPolicy: services.GetRequiredService<IToolPolicy>(), executor: services.GetRequiredService<IToolExecutor>(),
                resourceManagers: services.GetServices<IResourceManager>()));
            builder.Services.AddSingleton<ITaskRuntime>(services => services.GetRequiredService<TaskRuntime>());
            builder.Services.AddSingleton<ProtocolAgentService>();
            builder.Services.AddSingleton<IAgentService>(services => services.GetRequiredService<ProtocolAgentService>());
            builder.Services.AddSingleton<IAgentRunner>(services => services.GetRequiredService<ProtocolAgentService>());
            builder.Services.AddSingleton<AgentApi>();
            builder.Services.AddSingleton<TaskApi>();
            var app = builder.Build();
            app.Services.GetRequiredService<AgentApi>().Map(app);
            app.Services.GetRequiredService<TaskApi>().Map(app);
            return app;
        }
    }
}
