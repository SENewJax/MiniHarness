using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniHarness.Abstractions;
using MiniHarness.Harness;

namespace MiniHarness.Server
{
    public sealed class TaskRecoveryService : BackgroundService
    {
        private readonly ITaskStore store;
        private readonly ITaskApplicationService application;
        private readonly TaskRuntime runtime;
        private readonly RecoveryServerOptions options;
        private readonly ILogger<TaskRecoveryService> logger;
        private readonly ConcurrentDictionary<string, string> results = new ConcurrentDictionary<string, string>();
        public IReadOnlyDictionary<string, string> Results => results;
        public TaskRecoveryService(ITaskStore store, ITaskApplicationService application, TaskRuntime runtime, RecoveryServerOptions options, ILogger<TaskRecoveryService> logger)
        { this.store = store; this.application = application; this.runtime = runtime; this.options = options; this.logger = logger; }
        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            if (store is not ITaskCatalog catalog) return;
            using var slots = new SemaphoreSlim(options.MaxConcurrency);
            var jobs = new List<Task>();
            try
            {
                foreach (var id in catalog.EnumerateTaskIds())
                {
                    jobs.RemoveAll(job => job.IsCompleted);
                    await slots.WaitAsync(stop); jobs.Add(RunAsync(id)); await Task.Delay(options.StartIntervalMilliseconds, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { await Task.WhenAll(jobs); }
            async Task RunAsync(string id)
            {
                try
                {
                    for (int attempt = 0; attempt < options.MaxAttempts; attempt++)
                    {
                        try
                        {
                            var current = await store.LoadAsync(id, stop);
                            if (current == null || current.Recovery.HostGeneration == runtime.HostGeneration) return;
                            var recovered = (await application.ExecuteAsync(new TaskCommand { Id = TaskJson.Hash(runtime.HostGeneration + ":recover:" + id),
                                Kind = TaskCommandKind.Recover, TaskId = id }, stop)).State;
                            if (results.Count < 1024) results[id] = recovered.Recovery.Classification.ToString();
                            if (options.Mode == RecoveryMode.Safe && recovered.Recovery.CanResume && recovered.Recovery.AutomaticAllowed)
                                await application.ExecuteAsync(new TaskCommand { Id = TaskJson.Hash(runtime.HostGeneration + ":resume:" + id + ":" + recovered.Recovery.ContinuationToken),
                                    Kind = TaskCommandKind.Resume, TaskId = id, Automatic = true, RecoveryToken = recovered.Recovery.ContinuationToken }, stop);
                            return;
                        }
                        catch (TaskConflictException)
                        {
                            if (results.Count < 1024) results[id] = "busy";
                            if (attempt + 1 < options.MaxAttempts) await Task.Delay(options.StartIntervalMilliseconds, stop);
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error)
                { if (results.Count < 1024) results[id] = "blocked:" + error.GetType().Name; logger.LogWarning(error, "Recovery scan failed for {TaskId}.", id); }
                finally { slots.Release(); }
            }
        }
    }
}
