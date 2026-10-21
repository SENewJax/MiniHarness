using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Harness;

namespace MiniHarness.Server
{
    public sealed class RecoveryServerOptions
    {
        public RecoveryMode Mode { get; set; } = RecoveryMode.Manual;
        public int MaxConcurrency { get; set; } = 2;
        public int MaxAttempts { get; set; } = 1;
        public int ManagementTimeoutSeconds { get; set; } = 10;
        public int StartIntervalMilliseconds { get; set; } = 100;
        public bool AllowUsageReconciliation { get; set; }
        public bool AllowResourceReconciliation { get; set; }
        public RecoveryOptions Policy() => new RecoveryOptions { Mode = Mode, MaxConcurrency = MaxConcurrency,
            MaxAttempts = MaxAttempts, ManagementTimeoutSeconds = ManagementTimeoutSeconds, StartIntervalMilliseconds = StartIntervalMilliseconds };
    }
    public sealed class HostRecoveryAuthorizer : IResourceRecoveryAuthorizer
    {
        private readonly ITaskInitializer initializer;
        private readonly RecoveryServerOptions options;
        public HostRecoveryAuthorizer(ITaskInitializer initializer, RecoveryServerOptions options) { this.initializer = initializer; this.options = options; }
        public bool AllowsUsageReconciliation => options.AllowUsageReconciliation;
        public bool AllowsResourceReconciliation => options.AllowResourceReconciliation;
        public Task<RecoveryAuthorization> CheckAsync(AgentTaskState task, ModelBinding current, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var probe = new TaskCommand { Kind = TaskCommandKind.Start, Facts = TaskJson.Clone(task.Facts) };
                initializer.Initialize(probe);
                if (probe.Facts!.Workspace.RootPath != task.Facts.Workspace.RootPath)
                    return Task.FromResult(new RecoveryAuthorization { Reason = "workspace_binding_changed" });
                var restricted = TaskJson.Clone(task.Facts);
                restricted.Workspace.Grants = restricted.Workspace.Grants.Intersect(probe.Facts.Workspace.Grants, StringComparer.Ordinal).ToList();
                restricted.RequiredEvidence = restricted.RequiredEvidence.Union(probe.Facts.RequiredEvidence, StringComparer.Ordinal).ToList();
                return Task.FromResult(new RecoveryAuthorization { Allowed = true, RestrictedFacts = restricted });
            }
            catch (ArgumentException error) { return Task.FromResult(new RecoveryAuthorization { Reason = error.Message }); }
        }
    }
}
