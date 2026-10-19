using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding.Processes
{
    public sealed class ProcessSessionManager : IInteractiveResourceManager, IResourceInputJournal, IResourceWaitPolicy, IAsyncDisposable
    {
        public string Kind => "process";
        public string Generation { get; } = Guid.NewGuid().ToString("N");
        public event Action<ResourceReference>? Changed;
        // Fault windows are explicit and deterministic in acceptance tests.
        // Production leaves this null; callbacks never receive credentials.
        public Action<string>? FaultInjection { get; set; }
        private readonly ProcessSessionOptions options;
        private readonly Dictionary<string, ProcessProfile> profiles;
        private readonly ConcurrentDictionary<string, Session> sessions = new ConcurrentDictionary<string, Session>();
        private readonly SemaphoreSlim admission = new SemaphoreSlim(1, 1);
        private volatile bool stopping;
        private int disposalStarted;
        private readonly FileStream? ownership;
        private readonly string? ownershipFailure;
        public ProcessSessionManager(ProcessSessionOptions options, IEnumerable<ProcessProfile> profiles)
        {
            options.Validate(); this.options = ProcessJson.Clone(options);
            this.profiles = profiles.Select(ProcessJson.Clone).ToDictionary(p => p.Name, StringComparer.Ordinal);
            if (this.profiles.Values.Any(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.FileName) || p.LifetimeSeconds < 1 || p.LifetimeSeconds > options.MaxLifetimeSeconds))
                throw new ArgumentException("Invalid process profile.");
            Directory.CreateDirectory(options.Directory);
            try { ownership = new FileStream(Path.Combine(options.Directory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { ownershipFailure = error.GetType().Name + " HRESULT=0x" + error.HResult.ToString("X8"); /* Reconciliation only. */ }
        }
        public string Version => WorkspaceAccess.HashBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { options, profiles }, ProcessJson.Options)));
        public int MaxWaitMilliseconds => options.MaxWaitMilliseconds;
        public IReadOnlyList<string> ProfileNames => profiles.Keys.ToList();
        private string PathFor(string id) => Path.Combine(options.Directory, id + ".json");
        private string OutputPath(string id) => Path.Combine(options.Directory, id + ".output.json");
        public async Task<ResourceReference> StartAsync(string profile, ToolExecutionContext context, CancellationToken ct)
        {
            new WorkspaceAccess(context.Workspace).RequireGrant(WorkspaceGrants.Execute);
            if (!profiles.TryGetValue(profile, out var template)) throw new ResourceRequestRejectedException("Unknown process profile.");
            string root = new WorkspaceAccess(context.Workspace).Root;
            string id = "process_" + WorkspaceAccess.HashBytes(Encoding.UTF8.GetBytes(context.TaskId + ":" + context.OperationId)).Substring(0, 32);
            await admission.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (stopping) throw new ResourceRequestRejectedException("Process manager is stopping.");
                if (sessions.TryGetValue(id, out var existing))
                { var prior = Snapshot(existing); if (prior.Process!.Profile != profile) throw new ResourceRequestRejectedException("Startup operation profile conflict."); return prior; }
                if (File.Exists(PathFor(id)))
                { var prior = Load(id, context.TaskId); if (prior.Process!.Profile != profile) throw new ResourceRequestRejectedException("Startup operation profile conflict."); return prior; } // No ambiguous startup is ever replayed.
                if (ownership == null) throw new ResourceRequestRejectedException("Process directory ownership unavailable; reconciliation only. " + ownershipFailure);
                var historical = Directory.EnumerateFiles(options.Directory, "process_*.json")
                    .Where(p => !p.EndsWith(".output.json", StringComparison.Ordinal) && !sessions.ContainsKey(Path.GetFileNameWithoutExtension(p)))
                    .Select(p => ProcessJson.Read<SessionRecord>(p).Resource).Where(r => !ResourceStates.IsTerminal(r.State)).ToList();
                if (historical.Count + sessions.Values.Count(s => !ResourceStates.IsTerminal(Snapshot(s).State)) >= options.MaxSessions ||
                    historical.Count(r => r.OwnerTaskId == context.TaskId) + sessions.Values.Count(s => s.Record.Resource.OwnerTaskId == context.TaskId && !ResourceStates.IsTerminal(Snapshot(s).State)) >= options.MaxSessionsPerTask)
                    throw new ResourceRequestRejectedException("Process session quota exceeded.");
                ct.ThrowIfCancellationRequested();
                var resource = new ResourceReference { Id = id, Kind = Kind, OwnerTaskId = context.TaskId, OwnerOperationId = context.OperationId,
                    State = "starting", Process = new ProcessSnapshot { Generation = Generation, Profile = profile, ProfileVersion = Version,
                        DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(template.LifetimeSeconds) } };
                var record = new SessionRecord { Resource = resource, Profile = ProcessJson.Clone(template) };
                ProcessJson.Write(PathFor(id), record); // durable intent before OS creation
                var session = new Session(record, new ProcessOutputStore(OutputPath(id), options));
                sessions[id] = session;
                try
                {
                    FaultInjection?.Invoke("after_start_intent");
                    var startup = Task.Factory.StartNew(() => OwnedProcess.Create(template.FileName, template.Arguments, root, () => FaultInjection?.Invoke("before_job_bind")),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    try { session.Owned = await startup.WaitAsync(TimeSpan.FromMilliseconds(options.StartTimeoutMilliseconds), ct); }
                    catch
                    {
                        session.StartupAttempt = startup; // Includes completion racing the timeout.
                        throw;
                    }
                    ct.ThrowIfCancellationRequested();
                    resource.Process.Pid = session.Owned.Process.Id;
                    resource.Process.StartedAtUtc = session.Owned.Process.StartTime.ToUniversalTime();
                    resource.Process.DeadlineUtc = resource.Process.StartedAtUtc.Value.AddSeconds(template.LifetimeSeconds);
                    resource.State = "running"; resource.Process.Version++;
                    if (!template.AllowStdin) { session.Owned.Input.Close(); resource.Process.InputClosed = true; }
                    Save(session);
                    // Anonymous pipe handles are synchronous on Windows. A read may block
                    // before returning its Task; never perform it on the startup path
                    // while the child is still suspended.
                    session.Drains = new[] { Task.Run(() => DrainAsync(session, session.Owned.Output, "stdout")),
                        Task.Run(() => DrainAsync(session, session.Owned.Error, "stderr")) };
                    if (DateTimeOffset.UtcNow >= resource.Process.DeadlineUtc) throw new TimeoutException("Process lifetime expired before execution.");
                    session.Owned.Resume();
                    session.Monitor = MonitorAsync(session);
                    session.Cleanup = CleanupAsync(session);
                }
                catch (Exception error)
                {
                    if (error is LaunchException failedLaunch) resource.Process.Pid = failedLaunch.Pid;
                    bool empty = session.Owned == null ? error is PlatformNotSupportedException || error is LaunchException launch && launch.TerminationConfirmed
                        : await session.Owned.Job.TerminateAndWaitEmptyAsync(TimeSpan.FromMilliseconds(options.CancelTimeoutMilliseconds));
                    resource.State = empty ? "failed" : "unknown"; resource.Result = "startup: " + error.GetType().Name + ": " + error.Message;
                    resource.Process.TerminationConfirmed = empty; resource.Process.StopReason = "startup_failed";
                    try { Save(session); } catch { resource.State = "unknown"; }
                    if (empty) { session.Owned?.Dispose(); sessions.TryRemove(id, out _); }
                }
                if (session.StartupAttempt != null) session.LateStartup = ReclaimLateStartupAsync(session, session.StartupAttempt);
                Notify(session); return Snapshot(session);
            }
            finally { admission.Release(); }
        }
        private async Task ReclaimLateStartupAsync(Session session, Task<OwnedProcess> startup)
        {
            try
            {
                using var owned = await startup;
                bool confirmed = await owned.Job.TerminateAndWaitEmptyAsync(TimeSpan.FromMilliseconds(options.CancelTimeoutMilliseconds));
                lock (session.Sync)
                {
                    session.Record.Resource.State = confirmed ? "failed" : "unknown";
                    session.Record.Resource.Process!.Pid = owned.Process.Id;
                    session.Record.Resource.Process!.TerminationConfirmed = confirmed;
                    session.Record.Resource.Process.StopReason = "startup_deadline"; Save(session);
                }
            }
            catch (Exception ex) { lock (session.Sync) { session.Record.Resource.Result = "Late startup: " + ex.Message; try { Save(session); } catch { } } }
            Notify(session);
        }
        private async Task CleanupAsync(Session session)
        {
            await session.Monitor;
            if (!ResourceStates.IsTerminal(Snapshot(session).State)) return;
            await session.InputGate.WaitAsync();
            try { session.Owned?.Dispose(); sessions.TryRemove(session.Record.Resource.Id, out _); }
            finally { session.InputGate.Release(); }
        }
        private async Task DrainAsync(Session session, StreamReader reader, string stream)
        {
            char[] buffer = new char[1024]; string pending = "";
            try
            {
                while (true)
                {
                    int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                    if (read == 0) break;
                    string text = pending + new string(buffer, 0, read); pending = "";
                    if (char.IsHighSurrogate(text[text.Length - 1])) { pending = text.Substring(text.Length - 1); text = text.Substring(0, text.Length - 1); }
                    session.Output.Append(stream, text);
                    lock (session.Sync) session.Record.Resource.Process!.Version++;
                    if (session.Output.Degraded) session.OutputFailure.TrySetResult(true);
                    Notify(session);
                }
                if (pending.Length > 0) session.Output.Append(stream, "\uFFFD");
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            { session.OutputFailure.TrySetResult(true); }
        }
        private async Task MonitorAsync(Session session)
        {
            try
            {
                var untilExit = WaitTreeAsync(session);
                _ = untilExit.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                var remaining = session.Record.Resource.Process!.DeadlineUtc - DateTimeOffset.UtcNow;
                var lifetime = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                var winner = await Task.WhenAny(untilExit, lifetime, session.OutputFailure.Task).ConfigureAwait(false);
                if (winner != untilExit) await StopAsync(session, winner == lifetime ? "lifetime" : "output_store_unavailable").ConfigureAwait(false);
                else { await untilExit.ConfigureAwait(false); await FinishAsync(session).ConfigureAwait(false); }
            }
            catch (Exception ex)
            {
                lock (session.Sync)
                { session.Record.Resource.State = "unknown"; session.Record.Resource.Result = ex.GetType().Name + ": " + ex.Message; try { Save(session); } catch { } }
                Notify(session);
            }
        }
        private async Task WaitTreeAsync(Session session)
        {
            await session.Owned!.Process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(session.Drains).ConfigureAwait(false);
            while (true)
            {
                int members = session.Owned.Job.CountProcesses();
                if (members == 0) break;
                if (members < 0) throw new IOException("Cannot confirm process job membership.");
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
        private async Task FinishAsync(Session session)
        {
            await session.End.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (session.Sync)
                {
                    if (ResourceStates.IsTerminal(session.Record.Resource.State)) return;
                    session.Record.Resource.State = "finished";
                    session.Record.Resource.Process!.ExitCode = session.Owned!.Process.ExitCode;
                    session.Record.Resource.Process.TerminationConfirmed = true;
                    session.Record.Resource.Process.OutputArtifact = session.Output.Seal(session.Record.Resource.Id, session.Record.Resource.OwnerTaskId);
                    session.Record.Resource.Process.Version++; Save(session);
                }
            }
            finally { session.End.Release(); }
            Notify(session);
        }
        private async Task StopAsync(Session session, string reason)
        {
            await session.End.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (session.Sync)
                {
                    if (ResourceStates.IsTerminal(session.Record.Resource.State)) return;
                    session.Record.Resource.State = "cancellation_requested";
                    session.Record.Resource.Process!.StopReason = reason; Save(session);
                }
                bool confirmed = session.Owned != null && await session.Owned.Job.TerminateAndWaitEmptyAsync(TimeSpan.FromMilliseconds(options.CancelTimeoutMilliseconds));
                if (confirmed)
                    try { await Task.WhenAll(session.Drains).WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (Exception) { confirmed = false; }
                lock (session.Sync)
                {
                    var resource = session.Record.Resource;
                    resource.State = !confirmed ? "unknown" : reason == "cancel" || reason == "shutdown" ? "cancelled" : "failed";
                    resource.Process!.TerminationConfirmed = confirmed;
                    if (confirmed) resource.Process.OutputArtifact = session.Output.Seal(resource.Id, resource.OwnerTaskId);
                    if (session.Owned?.Process.HasExited == true) resource.Process.ExitCode = session.Owned.Process.ExitCode;
                    resource.Process.Version++; Save(session);
                }
            }
            finally { session.End.Release(); }
            Notify(session);
        }
        public async Task<ResourceReference> ExecuteAsync(ResourceReference resource, ResourceAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var current = Load(resource.Id, resource.OwnerTaskId);
            CheckIdentity(resource, current);
            if (action == ResourceAction.Cancel && sessions.TryGetValue(resource.Id, out var session)) await StopAsync(session, "cancel").WaitAsync(ct);
            return Load(resource.Id, resource.OwnerTaskId);
        }
        public Task<ProcessOutputPage> ReadOutputAsync(ResourceReference resource, long after, int maxBytes, CancellationToken ct)
        {
            if (maxBytes < 256) throw new ResourceRequestRejectedException("Output pages require at least 256 bytes.");
            ct.ThrowIfCancellationRequested(); var current = Load(resource.Id, resource.OwnerTaskId); CheckIdentity(resource, current);
            var output = sessions.TryGetValue(resource.Id, out var session) ? session.Output : new ProcessOutputStore(OutputPath(resource.Id), options);
            long known = current.Process!.LastSequence;
            if (current.Process.OutputDegraded && after > output.Last && after <= known)
                return Task.FromResult(new ProcessOutputPage { FirstAvailableSequence = known + 1, NextCursor = known, Truncated = true, Gap = after < known });
            var page = output.Read(after, maxBytes);
            if (current.Process.OutputDegraded)
            {
                page.Truncated = true;
                if (!page.HasMore && output.Last < known) { page.Gap = true; page.NextCursor = known; }
            }
            return Task.FromResult(page);
        }
        public async Task<ResourceReference> WriteInputAsync(ResourceReference resource, ResourceInput input, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(input.CommandId) || input.CommandId.Length > 128) throw new ResourceRequestRejectedException("stdin command ID must contain 1 to 128 characters.");
            var current = Load(resource.Id, resource.OwnerTaskId); CheckIdentity(resource, current);
            if (!sessions.TryGetValue(resource.Id, out var session))
            {
                var old = ProcessJson.Read<SessionRecord>(PathFor(resource.Id));
                string fingerprint = WorkspaceAccess.HashBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input, ProcessJson.Options)));
                if (old.Inputs.TryGetValue(input.CommandId, out var receipt) && receipt.Fingerprint == fingerprint && receipt.Status == "finished") return current;
                throw new ResourceRequestRejectedException("Closed or old host session cannot accept stdin.");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(options.InputTimeoutMilliseconds);
            try { await session.InputGate.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new ResourceRequestRejectedException("stdin queue deadline exceeded; no input written."); }
            try
            {
                string fingerprint = WorkspaceAccess.HashBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input, ProcessJson.Options)));
                if (string.IsNullOrWhiteSpace(input.CommandId)) throw new ResourceRequestRejectedException("stdin action ID required.");
                lock (session.Sync)
                {
                    if (session.Record.Inputs.TryGetValue(input.CommandId, out var previous))
                    {
                        if (previous.Fingerprint != fingerprint) throw new ResourceRequestRejectedException("stdin command ID conflict.");
                        if (previous.Status != "finished") throw new ResourceRequestRejectedException("stdin action is uncertain; do not resend.");
                        return Snapshot(session);
                    }
                    if (input.ExpectedGeneration != Generation || !session.Record.Profile.AllowStdin || session.Record.Resource.State != "running" ||
                        session.Record.Resource.Process!.InputClosed || session.Record.Inputs.Count >= options.MaxInputActions ||
                        Encoding.UTF8.GetByteCount(input.Text) > options.MaxInputBytes || (input.CloseInput && input.Text.Length > 0))
                        throw new ResourceRequestRejectedException("stdin profile, generation, size or state rejected.");
                    session.Record.Inputs[input.CommandId] = new InputReceipt { Fingerprint = fingerprint };
                    Save(session); // intent before any byte reaches stdin
                }
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (input.CloseInput) session.Owned!.Input.Close();
                    else
                    {
                        await session.Owned!.Input.WriteAsync(input.Text.AsMemory(), deadline.Token).WaitAsync(deadline.Token);
                        await session.Owned.Input.FlushAsync(deadline.Token).WaitAsync(deadline.Token);
                    }
                    FaultInjection?.Invoke("after_stdin_write");
                    lock (session.Sync)
                    {
                        session.Record.Inputs[input.CommandId].Status = "finished";
                        session.Record.Resource.Process!.InputClosed |= input.CloseInput;
                        session.Record.Resource.Process.Version++; Save(session);
                    }
                }
                catch (Exception)
                {
                    lock (session.Sync) { session.Record.Inputs[input.CommandId].Status = "unknown"; try { Save(session); } catch { } }
                    await StopAsync(session, "stdin_uncertain"); throw new IOException("stdin effect is unknown; reconcile rather than resend.");
                }
                Notify(session); return Snapshot(session);
            }
            finally { session.InputGate.Release(); }
        }
        public ResourceReference Find(string id, string taskId) => Load(id, taskId);
        public Task<string> ReadInputStatusAsync(ResourceReference resource, string commandId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); CheckIdentity(resource, Load(resource.Id, resource.OwnerTaskId));
            if (sessions.TryGetValue(resource.Id, out var session))
                lock (session.Sync) return Task.FromResult(session.Record.Inputs.TryGetValue(commandId, out var input) ? input.Status : "absent");
            var record = ProcessJson.Read<SessionRecord>(PathFor(resource.Id));
            return Task.FromResult(record.Inputs.TryGetValue(commandId, out var receipt) ? receipt.Status : "absent");
        }
        private ResourceReference Load(string id, string taskId)
        {
            if (id.Length != 40 || !id.StartsWith("process_", StringComparison.Ordinal) || !id.Substring(8).All(Uri.IsHexDigit)) throw new ResourceRequestRejectedException("Invalid process identity.");
            if (sessions.TryGetValue(id, out var active))
            {
                var snapshot = Snapshot(active); if (snapshot.OwnerTaskId != taskId) throw new ResourceRequestRejectedException("Process belongs to another task."); return snapshot;
            }
            if (!File.Exists(PathFor(id))) throw new ResourceRequestRejectedException("Unknown process resource.");
            var record = ProcessJson.Read<SessionRecord>(PathFor(id));
            if (record.Resource.Process?.SchemaVersion != 1) throw new InvalidDataException("Unsupported process journal metadata version.");
            if (record.Resource.Id != id || record.Resource.OwnerTaskId != taskId) throw new ResourceRequestRejectedException("Process ownership mismatch.");
            if (!ResourceStates.IsTerminal(record.Resource.State))
            { record.Resource.State = "unknown"; record.Resource.Result = "Host changed; PID is diagnostic only. No attach, kill or restart attempted."; }
            return record.Resource;
        }
        private static void CheckIdentity(ResourceReference expected, ResourceReference current)
        {
            if (expected.Kind != current.Kind || expected.OwnerTaskId != current.OwnerTaskId || expected.OwnerOperationId != current.OwnerOperationId)
                throw new ResourceRequestRejectedException("Resource identity cannot change.");
            if (expected.Process != null && current.Process != null && expected.Process.Generation != current.Process.Generation)
                throw new ResourceRequestRejectedException("Resource generation changed.");
        }
        private ResourceReference Snapshot(Session session)
        {
            lock (session.Sync)
            {
                var resource = ProcessJson.Clone(session.Record.Resource);
                resource.Process!.LastSequence = session.Output.Last; resource.Process.FirstAvailableSequence = session.Output.First;
                resource.Process.OutputDegraded = session.Output.Degraded;
                return resource;
            }
        }
        private void Save(Session session)
        {
            lock (session.Sync)
            {
                session.Record.Resource.Process!.LastSequence = session.Output.Last;
                session.Record.Resource.Process.FirstAvailableSequence = session.Output.First;
                session.Record.Resource.Process.OutputDegraded = session.Output.Degraded;
                ProcessJson.Write(PathFor(session.Record.Resource.Id), session.Record);
            }
        }
        private void Notify(Session session) { try { FaultInjection?.Invoke("before_notify"); Changed?.Invoke(Snapshot(session)); } catch { /* Notifications are hints; polling reconciles. */ } }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposalStarted, 1) != 0) return;
            stopping = true;
            await admission.WaitAsync(); admission.Release();
            foreach (var session in sessions.Values)
            {
                try { await StopAsync(session, "shutdown").WaitAsync(TimeSpan.FromMilliseconds(options.CancelTimeoutMilliseconds + 3000)); } catch { }
                try { await session.Monitor.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                try { await session.Cleanup.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                session.Owned?.Dispose();
            }
            var late = Task.WhenAll(sessions.Values.Select(s => s.LateStartup));
            if (late.IsCompleted) ownership?.Dispose();
            else _ = late.ContinueWith(_ => ownership?.Dispose(), TaskScheduler.Default);
        }
        private sealed class Session
        {
            public object Sync { get; } = new object();
            public SemaphoreSlim InputGate { get; } = new SemaphoreSlim(1, 1);
            public SemaphoreSlim End { get; } = new SemaphoreSlim(1, 1);
            public SessionRecord Record { get; }
            public ProcessOutputStore Output { get; }
            public OwnedProcess? Owned { get; set; }
            public Task[] Drains { get; set; } = Array.Empty<Task>();
            public Task Monitor { get; set; } = Task.CompletedTask;
            public Task Cleanup { get; set; } = Task.CompletedTask;
            public Task LateStartup { get; set; } = Task.CompletedTask;
            public Task<OwnedProcess>? StartupAttempt { get; set; }
            public TaskCompletionSource<bool> OutputFailure { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Session(SessionRecord record, ProcessOutputStore output) { Record = record; Output = output; }
        }
        private sealed class SessionRecord
        {
            public ResourceReference Resource { get; set; } = new ResourceReference();
            public ProcessProfile Profile { get; set; } = new ProcessProfile();
            public Dictionary<string, InputReceipt> Inputs { get; set; } = new Dictionary<string, InputReceipt>();
        }
        private sealed class InputReceipt
        {
            public string Fingerprint { get; set; } = "";
            public string Status { get; set; } = "started";
        }
    }
}
