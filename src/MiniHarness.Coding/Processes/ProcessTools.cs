using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding.Processes
{
    public sealed class ProcessTool : CodingToolBase, IVersionedTool, IToolTimeout
    {
        private readonly ProcessSessionManager manager;
        private readonly string name;
        public string Version => manager.Version;
        public int TimeoutSeconds => 15;
        public override ToolDefinition Definition { get; }
        public static IReadOnlyList<ITool> CreateAll(ProcessSessionManager manager) =>
            new[] { "start_process", "read_process_output", "write_process_input", "wait_process", "cancel_process" }.Select(n => (ITool)new ProcessTool(manager, n)).ToList();
        private ProcessTool(ProcessSessionManager manager, string name)
        {
            this.manager = manager; this.name = name;
            string properties = name == "start_process" ? "\"profile\":{\"type\":\"string\"}" :
                "\"resource_id\":{\"type\":\"string\"}";
            string required = name == "start_process" ? "\"profile\"" : "\"resource_id\"";
            if (name == "read_process_output" || name == "wait_process") properties += ",\"after\":{\"type\":\"integer\"}";
            if (name == "read_process_output") properties += ",\"max_bytes\":{\"type\":\"integer\"}";
            if (name == "write_process_input")
            {
                properties += ",\"generation\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"},\"close_input\":{\"type\":\"boolean\"}";
                required += ",\"generation\"";
            }
            if (name == "wait_process") properties += ",\"condition\":{\"type\":\"string\"},\"wait_ms\":{\"type\":\"integer\"}";
            Definition = Def(name, name == "start_process" ? "Start a host-owned profile and return a running resource. Profiles: " + string.Join(", ", manager.ProfileNames) :
                name == "wait_process" ? "Persist an Exit or Output wait and yield this turn without polling the model. A timeout does not kill the process." :
                name == "write_process_input" ? "Write bounded stdin or close it. Use the generation returned by start_process; uncertain writes must not be resent." :
                name == "read_process_output" ? "Read bounded incremental output with an after cursor and max_bytes >= 256; gaps mean retained output was evicted." : "Cancel and confirm the owned process tree.",
                "{\"type\":\"object\",\"properties\":{" + properties + "},\"required\":[" + required + "],\"additionalProperties\":false}");
        }
        protected override async Task<ToolOutcome> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
        {
            try { return await ExecuteProcessAsync(args, context, ct); }
            catch (ArgumentException error) { return Fail(error.Message); }
        }
        private async Task<ToolOutcome> ExecuteProcessAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
        {
            new WorkspaceAccess(context.Workspace).RequireGrant(WorkspaceGrants.Execute);
            if (name == "start_process")
            {
                var resource = await manager.StartAsync(String(args, "profile"), context, ct);
                return new ToolOutcome { Status = resource.State == "running" || resource.State == "finished" ? ToolCallStatus.Succeeded : ToolCallStatus.Failed,
                    Effect = resource.State == "unknown" ? EffectState.Unknown : resource.Process!.StopReason == "startup_failed" && resource.Process.TerminationConfirmed ? EffectState.None : EffectState.Confirmed,
                    Output = ToJson(resource), Resources = { resource } };
            }
            var current = manager.Find(String(args, "resource_id"), context.TaskId);
            if (name == "read_process_output")
            {
                var page = await manager.ReadOutputAsync(current, Int(args, "after", 0), Int(args, "max_bytes", 8192), ct);
                return new ToolOutcome { Status = ToolCallStatus.Succeeded, Output = ToJson(page), ResourceUpdates = new List<ResourceReference> { current } };
            }
            if (name == "write_process_input")
            {
                try
                {
                    var updated = await manager.WriteInputAsync(current, new ResourceInput { CommandId = context.OperationId,
                        ExpectedGeneration = String(args, "generation"), Text = String(args, "text"),
                        CloseInput = args.TryGetProperty("close_input", out var close) && close.GetBoolean() }, ct);
                    return new ToolOutcome { Status = ToolCallStatus.Succeeded, Effect = EffectState.Confirmed,
                        Output = ToJson(updated), ResourceUpdates = new List<ResourceReference> { updated } };
                }
                catch (IOException error) { return Uncertain(error.Message); }
            }
            if (name == "cancel_process")
            {
                var updated = await manager.ExecuteAsync(current, ResourceAction.Cancel, ct);
                return new ToolOutcome { Status = ResourceStates.IsTerminal(updated.State) ? ToolCallStatus.Succeeded : ToolCallStatus.Failed,
                    Effect = ResourceStates.IsTerminal(updated.State) ? EffectState.Confirmed : EffectState.Unknown,
                    Output = ToJson(updated), ResourceUpdates = new List<ResourceReference> { updated } };
            }
            string condition = String(args, "condition", "exit");
            if (!Enum.TryParse<ResourceWaitCondition>(condition, true, out var kind) || !Enum.IsDefined(kind)) return Fail("condition must be Exit or Output.");
            int after = Int(args, "after", 0);
            if (after < 0 || after > current.Process!.LastSequence) return Fail("Invalid output cursor.");
            return new ToolOutcome { Status = ToolCallStatus.Succeeded, Output = "Resource wait registered; the runtime will deliver the next observation.",
                ResourceUpdates = new List<ResourceReference> { current }, WaitRequest = new ResourceWaitRequest { ResourceId = current.Id,
                    Condition = kind, AfterSequence = after, WaitMilliseconds = Math.Clamp(Int(args, "wait_ms", 30000), 1, manager.MaxWaitMilliseconds) } };
        }
    }
}
