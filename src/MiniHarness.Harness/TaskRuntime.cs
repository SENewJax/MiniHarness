using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    // Owns task lifetime. HTTP cancellation only stops the caller's wait.
    public sealed partial class TaskRuntime : ITaskRuntime, IResourceOutputAccess, IAsyncDisposable
    {
        private readonly ITaskStore store;
        private readonly IModelProvider model;
        private readonly HarnessOptions options;
        private readonly ModelBinding binding;
        private readonly IContextBuilder context;
        private readonly IToolRegistry tools;
        private readonly IContextualToolPolicy policy;
        private readonly ContextualToolExecutor executor;
        private readonly IContextPreparation preparation;
        private readonly ITaskCompletionPolicy completion;
        private readonly IWorkspaceRevisionProvider workspaceRevision;
        private readonly Dictionary<string, IResourceManager> resourceManagers;
        private readonly Action<string, Exception>? executionFailed;
        private readonly ConcurrentDictionary<string, Slot> slots = new ConcurrentDictionary<string, Slot>();
        private readonly ConcurrentDictionary<long, Task> active = new ConcurrentDictionary<long, Task>();
        private long invocation;
        private volatile bool disposed;
        private volatile bool stopping;

        public TaskRuntime(IModelProvider model, ITaskStore store, HarnessOptions options, ModelBinding binding,
            IContextBuilder? context = null, IToolRegistry? tools = null, IToolPolicy? toolPolicy = null,
            IContextualToolPolicy? policy = null, IToolExecutor? executor = null,
            IContextPreparation? preparation = null, ITaskCompletionPolicy? completion = null, IWorkspaceRevisionProvider? workspaceRevision = null,
            IEnumerable<IResourceManager>? resourceManagers = null, Action<string, Exception>? executionFailed = null)
        {
            options.Validate(); this.model = model; this.store = store;
            this.options = TaskJson.Clone(options); this.binding = TaskJson.Clone(binding);
            this.context = context ?? new ContextBuilder(this.options, model as IModelRequestSizer);
            this.tools = tools ?? new ToolRegistry(Array.Empty<ITool>());
            this.policy = policy ?? new LegacyToolPolicyAdapter(toolPolicy ?? new AllowListToolPolicy(Array.Empty<string>()));
            this.executor = new ContextualToolExecutor(executor ?? new ToolExecutor(this.options));
            this.preparation = preparation ?? new PassThroughPreparation();
            this.completion = completion ?? new EvidenceCompletionPolicy();
            this.workspaceRevision = workspaceRevision ?? new DirectoryWorkspaceRevision();
            this.resourceManagers = (resourceManagers ?? Array.Empty<IResourceManager>()).ToDictionary(m => m.Kind, StringComparer.Ordinal);
            this.executionFailed = executionFailed;
        }
        public Task<TaskCommandResult> ExecuteAsync(TaskCommand command, CancellationToken waitCancellation)
            => Schedule(command).Execution.WaitAsync(waitCancellation);
        public async Task<TaskSubmission> SubmitAsync(TaskCommand command, CancellationToken waitCancellation)
        {
            var scheduled = Schedule(command);
            return new TaskSubmission { TaskId = scheduled.Id, Receipt = await scheduled.Accepted.WaitAsync(waitCancellation).ConfigureAwait(false) };
        }
        private (string Id, Task<TaskCommandResult> Execution, Task<CommandReceipt> Accepted) Schedule(TaskCommand command)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TaskRuntime));
            if (stopping && command.Kind != TaskCommandKind.QueryResource && command.Kind != TaskCommandKind.CancelResource &&
                command.Kind != TaskCommandKind.ReconcileResources && command.Kind != TaskCommandKind.Cancel && command.Kind != TaskCommandKind.Pause)
                throw new TaskConflictException("Runtime is stopping; no new advancement accepted.");
            var copy = TaskJson.Clone(command);
            if (string.IsNullOrWhiteSpace(copy.Id) || copy.Id.Length > 128) throw new ArgumentException("Command ID is required (maximum 128 characters).");
            string id = copy.Kind == TaskCommandKind.Start ? "task_" + TaskJson.Hash(copy.Id).Substring(0, 32)
                : FileTaskStore.NormalizeId(copy.TaskId ?? throw new ArgumentException("Task ID is required."));
            var slot = slots.GetOrAdd(id, _ => new Slot());
            // Reserve identity before publishing an interrupt. This lock never
            // waits for the execution gate held by model/tool calls.
            string fingerprint = CommandFingerprint(copy);
            lock (slot.Sync)
            {
                if (slot.Pending.TryGetValue(copy.Id, out var previous))
                {
                    if (previous.Fingerprint != fingerprint) throw new TaskConflictException("Command ID was reused with another payload.");
                    return (id, previous.Execution, previous.Accepted);
                }
                var scheduled = ScheduleReserved(copy, id, slot, fingerprint);
                slot.Pending.Add(copy.Id, (fingerprint, scheduled.Execution, scheduled.Accepted));
                return scheduled;
            }
        }
        private (string Id, Task<TaskCommandResult> Execution, Task<CommandReceipt> Accepted) ScheduleReserved(TaskCommand copy, string id, Slot slot, string fingerprint)
        {
            long call = Interlocked.Increment(ref invocation);
            var accepted = new TaskCompletionSource<CommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Reserve FIFO execution order on the submitting thread. Admission
            // (including interrupts and duplicate receipts) remains independent.
            var gateTicket = slot.Gate.WaitAsync();
            _ = accepted.Task.ContinueWith(faulted => { _ = faulted.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            var execution = Task.Run(async () =>
            {
                bool transferred = false;
                try
                {
                await slot.Admission.WaitAsync().ConfigureAwait(false);
                try
                {
                    var snapshot = await store.LoadAsync(id, CancellationToken.None).ConfigureAwait(false);
                    var receipt = snapshot?.Receipts.FirstOrDefault(r => r.CommandId == copy.Id);
                    if (receipt != null)
                    {
                        if (receipt.Fingerprint != fingerprint) throw new TaskConflictException("Command ID was reused with another payload.");
                        if (copy.Kind == TaskCommandKind.StartChild && snapshot!.Status != AgentTaskStatus.Cancelled)
                            await DispatchChildrenAsync(snapshot).ConfigureAwait(false);
                        accepted.TrySetResult(TaskJson.Clone(receipt));
                        return new TaskCommandResult { State = snapshot!, Receipt = receipt };
                    }
                    if (copy.Kind == TaskCommandKind.Cancel || copy.Kind == TaskCommandKind.Pause)
                    {
                        if (snapshot == null) throw new ArgumentException("Task not found.");
                        if (snapshot.Status == AgentTaskStatus.Completed || snapshot.Status == AgentTaskStatus.Failed ||
                            (snapshot.Status == AgentTaskStatus.Cancelled && copy.Kind != TaskCommandKind.Cancel))
                            throw new TaskConflictException("Terminal task cannot be advanced.");
                        lock (slot.Sync)
                        {
                            slot.Controls[copy.Id] = copy.Kind;
                            UpdateInterrupt(slot);
                            slot.Cancellation?.Cancel();
                        }
                    }
                    if (copy.Kind == TaskCommandKind.Steer)
                    {
                        ValidateSteeringPayload(copy.Steering);
                        lock (slot.Sync) { slot.Steering.Add(copy.Id); }
                    }
                }
                finally { slot.Admission.Release(); }
                transferred = true;
                return await ExecuteCoreAsync(copy, id, slot, accepted, gateTicket).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // SubmitAsync may already have returned its durable receipt.
                    // A background failure must remain observable to the host.
                    if (accepted.Task.IsCompletedSuccessfully && executionFailed != null)
                        try { executionFailed(id, ex); } catch { /* Diagnostics must not replace the execution failure. */ }
                    throw;
                }
                finally
                {
                    lock (slot.Sync)
                    {
                        slot.Pending.Remove(copy.Id);
                        slot.Steering.Remove(copy.Id);
                        slot.Controls.Remove(copy.Id);
                        if (!disposed) UpdateInterrupt(slot);
                    }
                    if (!transferred) _ = gateTicket.ContinueWith(_ => slot.Gate.Release(), TaskScheduler.Default);
                }
            });
            active[call] = execution;
            _ = execution.ContinueWith(completed =>
            {
                active.TryRemove(call, out _);
                if (completed.Exception != null) accepted.TrySetException(completed.Exception.InnerExceptions);
                if (completed.IsCanceled) accepted.TrySetCanceled();
            }, TaskScheduler.Default);
            return (id, execution, accepted.Task);
        }
        public Task<AgentTaskState?> GetAsync(string taskId, CancellationToken cancellationToken) => store.LoadAsync(taskId, cancellationToken);
        public async Task<IReadOnlyList<TaskEvent>> ObserveAsync(string taskId, long afterSequence, CancellationToken cancellationToken)
        {
            if (afterSequence < 0) throw new ArgumentException("Event cursor must be non-negative.");
            var state = await GetAsync(taskId, cancellationToken).ConfigureAwait(false) ?? throw new ArgumentException("Task not found.");
            if (afterSequence > state.Events.Count) throw new ArgumentException("Event cursor is ahead of the task.");
            return state.Events.Where(e => e.Sequence > afterSequence).Select(TaskJson.Clone).ToList();
        }
        private async Task<TaskCommandResult> ExecuteCoreAsync(TaskCommand command, string id, Slot slot, TaskCompletionSource<CommandReceipt> accepted, Task gateTicket)
        {
            await gateTicket.ConfigureAwait(false);
            try
            {
                if (disposed) throw new ObjectDisposedException(nameof(TaskRuntime));
                if (stopping && command.Kind != TaskCommandKind.QueryResource && command.Kind != TaskCommandKind.CancelResource &&
                    command.Kind != TaskCommandKind.ReconcileResources && command.Kind != TaskCommandKind.Cancel && command.Kind != TaskCommandKind.Pause)
                    throw new TaskConflictException("Queued advancement rejected during shutdown.");
                await using var lease = await store.AcquireAsync(id, CancellationToken.None).ConfigureAwait(false);
                var task = await lease.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                string fingerprint = CommandFingerprint(command);
                var existing = task?.Receipts.FirstOrDefault(r => r.CommandId == command.Id);
                if (existing != null)
                {
                    if (existing.Fingerprint != fingerprint) throw new TaskConflictException("Command ID was reused with another payload.");
                    if (command.Kind == TaskCommandKind.StartChild && task!.Status != AgentTaskStatus.Cancelled)
                        await DispatchChildrenAsync(task).ConfigureAwait(false);
                    accepted.TrySetResult(TaskJson.Clone(existing));
                    return new TaskCommandResult { State = task!, Receipt = existing };
                }
                if (task == null && command.Kind != TaskCommandKind.Start) throw new ArgumentException("Task not found.");
                task ??= await NewTaskAsync(id, command).ConfigureAwait(false);
                bool terminal = task.Status == AgentTaskStatus.Completed || task.Status == AgentTaskStatus.Cancelled || task.Status == AgentTaskStatus.Failed;
                if (terminal && !(command.Kind == TaskCommandKind.Cancel && task.Status == AgentTaskStatus.Cancelled) && command.Kind != TaskCommandKind.Reconcile &&
                    command.Kind != TaskCommandKind.QueryResource && command.Kind != TaskCommandKind.CancelResource && command.Kind != TaskCommandKind.ReconcileResources && command.Kind != TaskCommandKind.CollectChild && command.Kind != TaskCommandKind.ResourceWake)
                    throw new TaskConflictException("Terminal task cannot be advanced.");
                bool controlOnly = command.Kind == TaskCommandKind.QueryResource || command.Kind == TaskCommandKind.CancelResource ||
                    command.Kind == TaskCommandKind.ReconcileResources || command.Kind == TaskCommandKind.WriteResourceInput ||
                    command.Kind == TaskCommandKind.Pause || command.Kind == TaskCommandKind.Cancel;
                if (!controlOnly && (task.Model.Model != binding.Model || (task.Model.Protocol != "Unspecified" && task.Model.Protocol != binding.Protocol)))
                    throw new TaskConflictException("Task model/protocol binding differs from this runtime.");
                if (!controlOnly) task.Model = TaskJson.Clone(binding);
                TaskTurn? turn = task.Turns.LastOrDefault();
                bool advance = false;
                switch (command.Kind)
                {
                    case TaskCommandKind.Start:
                        if (task.Receipts.Count > 0) throw new TaskConflictException("Task already exists.");
                        turn = AddTurn(task, command.Request ?? throw new ArgumentException("Request is required.")); advance = true;
                        break;
                    case TaskCommandKind.Input:
                        if (task.Status == AgentTaskStatus.Cancelled || task.Status == AgentTaskStatus.Completed || task.Status == AgentTaskStatus.Failed ||
                            (turn != null && turn.Status != TurnStatus.Completed))
                            throw new TaskConflictException("Task cannot accept a new turn in its current state.");
                        turn = AddTurn(task, command.Request ?? throw new ArgumentException("Request is required.")); advance = true;
                        break;
                    case TaskCommandKind.Resume:
                        if (task.Status == AgentTaskStatus.Cancelled || task.Status == AgentTaskStatus.Completed || task.Status == AgentTaskStatus.Failed)
                            throw new TaskConflictException("Terminal task cannot resume.");
                        advance = turn != null && turn.Status != TurnStatus.Completed;
                        if (!advance) { task.Status = AgentTaskStatus.Waiting; task.Wait = WaitReason.Input; }
                        break;
                    case TaskCommandKind.Approval:
                        if (turn == null) throw new TaskConflictException("No active turn.");
                        var operation = task.Operations.SingleOrDefault(o => o.Id == command.OperationId && o.TurnId == turn.Id);
                        if (operation == null || operation.Status != OperationStatus.WaitingApproval)
                            throw new TaskConflictException("Operation is not awaiting approval.");
                        if (operation.ApprovalFingerprint != OperationFingerprint(task, operation))
                            throw new TaskConflictException("Approval no longer matches the pending operation.");
                        operation.Approval = command.Approve ? ApprovalState.Approved : ApprovalState.Denied;
                        operation.Status = OperationStatus.Proposed; advance = true;
                        Emit(task, "ApprovalReceived", turn, operation.Approval.ToString(), operation);
                        break;
                    case TaskCommandKind.ExternalResult:
                        if (turn == null || task.Wait != WaitReason.ExternalResult) throw new TaskConflictException("Task is not awaiting external results.");
                        if (command.Results.Select(r => r.CallId).Distinct(StringComparer.Ordinal).Count() != command.Results.Count)
                            throw new ArgumentException("Duplicate external tool result.");
                        foreach (var result in command.Results)
                        {
                            var pending = task.Operations.SingleOrDefault(o => o.TurnId == turn.Id && o.ClientOwned &&
                                o.Status == OperationStatus.Proposed && o.Call.Id == result.CallId);
                            if (pending == null) throw new TaskConflictException("External result has no pending operation.");
                            FinishOperation(task, turn, pending, new ToolOutcome { Status = ToolCallStatus.Succeeded, Output = result.Output, Effect = EffectState.Confirmed });
                        }
                        if (task.Operations.Where(o => o.TurnId == turn.Id).All(o => o.Status == OperationStatus.Finished)) turn.Settings.Options.ToolChoice = "auto";
                        advance = true;
                        break;
                    case TaskCommandKind.Reconcile:
                        if (turn == null || command.ReconciledOutcome == null ||
                            (command.ReconciledOutcome.Effect != EffectState.Confirmed && command.ReconciledOutcome.Effect != EffectState.None))
                            throw new ArgumentException("Reconciliation requires a confirmed effect or confirmed absence of effect.");
                        var uncertainOperation = task.Operations.SingleOrDefault(o => o.Id == command.OperationId && o.TurnId == turn.Id && o.Status == OperationStatus.Unknown);
                        if (uncertainOperation == null) throw new TaskConflictException("No matching uncertain operation.");
                        if (command.ReconciledOutcome.Effect == EffectState.Confirmed) FinishOperation(task, turn, uncertainOperation, command.ReconciledOutcome);
                        else { uncertainOperation.Status = OperationStatus.Proposed; uncertainOperation.Effect = EffectState.None; uncertainOperation.Approval = ApprovalState.None; }
                        Emit(task, "OperationReconciled", turn, command.ReconciledOutcome.Effect.ToString(), uncertainOperation);
                        advance = !terminal;
                        break;
                    case TaskCommandKind.Evidence:
                        if (turn == null || turn.Wait != WaitReason.Evidence || task.Status != AgentTaskStatus.Waiting)
                            throw new TaskConflictException("Task is not awaiting evidence.");
                        foreach (var evidence in command.Evidence)
                        {
                            if ((evidence.TaskId.Length > 0 && evidence.TaskId != task.Id) || string.IsNullOrWhiteSpace(evidence.Requirement) ||
                                string.IsNullOrWhiteSpace(evidence.WorkspaceRevision)) throw new ArgumentException("Invalid evidence identity or revision.");
                            evidence.TaskId = task.Id;
                            foreach (var artifact in evidence.Artifacts) RegisterArtifact(task, artifact);
                            var oldEvidence = task.Evidence.SingleOrDefault(e => e.Id == evidence.Id);
                            if (oldEvidence != null && JsonSerializer.Serialize(oldEvidence, TaskJson.Options) != JsonSerializer.Serialize(evidence, TaskJson.Options))
                                throw new TaskConflictException("Evidence ID was reused with another payload.");
                            if (oldEvidence == null) task.Evidence.Add(TaskJson.Clone(evidence));
                        }
                        Emit(task, "EvidenceSubmitted", turn); advance = true;
                        break;
                    case TaskCommandKind.RefreshWorkspace:
                        await RefreshWorkspaceAsync(task, turn, CancellationToken.None).ConfigureAwait(false);
                        break;
                    case TaskCommandKind.Steer:
                        advance = ApplySteering(task, turn, command);
                        lock (slot.Sync) slot.Steering.Remove(command.Id);
                        break;
                    case TaskCommandKind.ResourceWake:
                        advance = await WakeAsync(task, turn, command, lease);
                        break;
                    case TaskCommandKind.WriteResourceInput:
                        await WriteResourceInputAsync(task, turn, lease, command);
                        break;
                    case TaskCommandKind.QueryResource:
                    case TaskCommandKind.CancelResource:
                    case TaskCommandKind.ReconcileResources:
                        await ManageResourcesAsync(task, lease, command).ConfigureAwait(false);
                        break;
                    case TaskCommandKind.StartChild:
                        ReserveChild(task, command);
                        break;
                    case TaskCommandKind.CollectChild:
                        await CollectChildAsync(task, command.ChildTaskId ?? throw new ArgumentException("Child task ID required.")).ConfigureAwait(false);
                        break;
                    case TaskCommandKind.Pause:
                    case TaskCommandKind.Cancel:
                        task.Status = command.Kind == TaskCommandKind.Cancel ? AgentTaskStatus.Cancelled : AgentTaskStatus.Paused;
                        if (command.Kind == TaskCommandKind.Cancel)
                        {
                            if (turn != null) InvalidateWaits(task, turn);
                            task.Wait = WaitReason.Reconciliation;
                            if (turn != null && turn.Status != TurnStatus.Completed) { turn.Status = TurnStatus.Interrupted; turn.Wait = task.Wait; }
                            Emit(task, "TaskCancellationRequested", turn);
                            await CommitAsync(lease, task);
                            await CancelResourcesAsync(task, lease).ConfigureAwait(false);
                            await CancelChildrenAsync(task, command.Id).ConfigureAwait(false);
                            bool unresolved = task.Operations.Any(o => o.Status == OperationStatus.Unknown || o.Status == OperationStatus.Started) ||
                                task.ResourceInputs.Any(i => i.Status != "finished" && i.Status != "absent") || task.Resources.Any(r => !ResourceFinished(r)) || task.Children.Any(c => !c.Collected);
                            task.Wait = unresolved ? WaitReason.Reconciliation : WaitReason.None;
                            if (turn != null) turn.Wait = task.Wait;
                        }
                        if (turn != null && turn.Status != TurnStatus.Completed) turn.Status = TurnStatus.Interrupted;
                        Emit(task, command.Kind.ToString(), turn);
                        lock (slot.Sync) { slot.Controls.Remove(command.Id); UpdateInterrupt(slot); }
                        break;
                    default: throw new ArgumentException("Unsupported task command kind.");
                }
                var receipt = new CommandReceipt
                {
                    CommandId = command.Id, Fingerprint = fingerprint, AcceptedRevision = task.Revision + 1, TurnId = turn?.Id
                };
                task.Receipts.Add(receipt);
                Emit(task, "CommandAccepted", turn, command.Kind.ToString());
                await CommitAsync(lease, task).ConfigureAwait(false);
                accepted.TrySetResult(TaskJson.Clone(receipt));
                if (command.Kind == TaskCommandKind.StartChild) await DispatchChildrenAsync(task).ConfigureAwait(false);
                if (advance && turn != null)
                {
                    using var cancellation = new CancellationTokenSource();
                    lock (slot.Sync)
                    {
                        slot.Cancellation = cancellation;
                        if (slot.Interrupt != null) cancellation.Cancel();
                    }
                    try { await AdvanceAsync(task, turn, lease, cancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        task.Status = slot.Interrupt == TaskCommandKind.Cancel ? AgentTaskStatus.Cancelled : AgentTaskStatus.Paused;
                        turn.Status = TurnStatus.Interrupted;
                        foreach (var step in task.Steps.Where(s => s.TurnId == turn.Id && (s.Status == StepStatus.Preparing || s.Status == StepStatus.Running)))
                        {
                            if (step.Status == StepStatus.Running) { task.Budget.Usage.Known = false; turn.Usage.Known = false; }
                            step.Status = StepStatus.Interrupted;
                        }
                        foreach (var pending in task.Operations.Where(o => o.TurnId == turn.Id && o.Status == OperationStatus.Started))
                        { pending.Status = OperationStatus.Unknown; pending.Effect = EffectState.Unknown; task.Wait = WaitReason.Reconciliation; turn.Wait = WaitReason.Reconciliation; }
                        Emit(task, "Interrupted", turn);
                        await CommitAsync(lease, task).ConfigureAwait(false);
                    }
                    catch (CheckpointWriteException) { throw; }
                    catch (ContextLimitException)
                    {
                        Wait(task, turn, WaitReason.Budget, "context_capacity"); await CommitAsync(lease, task).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        foreach (var running in task.Steps.Where(s => s.TurnId == turn.Id && s.Status == StepStatus.Running))
                        { running.Status = StepStatus.Interrupted; task.Budget.Usage.Known = false; turn.Usage.Known = false; }
                        var uncertain = task.Operations.Where(o => o.TurnId == turn.Id && o.Status == OperationStatus.Started).ToList();
                        foreach (var pending in uncertain) { pending.Status = OperationStatus.Unknown; pending.Effect = EffectState.Unknown; }
                        if (uncertain.Count > 0) Wait(task, turn, WaitReason.Reconciliation, ex.GetType().Name);
                        else { task.Status = AgentTaskStatus.Failed; turn.Status = TurnStatus.Failed; turn.StopReason = ex.GetType().Name; }
                        Emit(task, "Failed", turn, ex.GetType().Name);
                        await CommitAsync(lease, task).ConfigureAwait(false);
                        throw;
                    }
                    finally { lock (slot.Sync) { slot.Cancellation = null; } }
                }
                return new TaskCommandResult { State = TaskJson.Clone(task), Receipt = TaskJson.Clone(receipt) };
            }
            finally { slot.Gate.Release(); }
        }
        private AgentTaskState NewTask(string id, TaskCommand command) => new AgentTaskState
        {
            Id = id, ForkedFromResponseId = command.ForkedFromResponseId, Facts = TaskJson.Clone(command.Facts ?? new TaskFacts
            { Goal = command.Request?.Messages.FirstOrDefault(m => m.Role == "user")?.Text ?? "" }),
            Model = TaskJson.Clone(binding), Budget = new TaskBudget
            { MaxModelCalls = options.MaxModelCalls, MaxToolCalls = options.MaxToolCalls, MaxTokens = options.MaxTotalTokens }
        };
        private TaskTurn AddTurn(AgentTaskState task, ModelRequest request)
        {
            ValidateTurnRequest(task, request);
            var turn = new TaskTurn { Settings = TaskJson.Clone(request), Status = TurnStatus.Ready };
            turn.Settings.Messages.Clear(); task.Turns.Add(turn);
            foreach (var message in request.Messages) task.History.Add(new HistoryEntry { TurnId = turn.Id, Message = TaskJson.Clone(message) });
            task.Status = AgentTaskStatus.Ready; task.Wait = WaitReason.None;
            return turn;
        }
        private void ValidateTurnRequest(AgentTaskState task, ModelRequest request)
        {
            if (request.Options == null || request.Messages == null || request.Tools == null || request.Messages.Count == 0 ||
                request.Options.MaxOutputTokens < 1 || string.IsNullOrWhiteSpace(request.Options.ToolChoice) ||
                (request.Options.Temperature.HasValue && (request.Options.Temperature < 0 || request.Options.Temperature > 2)))
                throw new ArgumentException("Invalid model request or generation limits.");
            if (request.Tools.Any(t => string.IsNullOrWhiteSpace(t.Name) || t.Parameters.ValueKind != JsonValueKind.Object) ||
                request.Tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != request.Tools.Count)
                throw new ArgumentException("Invalid or duplicate client tools.");
            if (request.Model != task.Model.Model) throw new ArgumentException("Request model does not match task binding.");
            if (request.Tools.Any(t => tools.TryGet(t.Name, out _))) throw new ArgumentException("Client tool names must not collide with server tools.");
            string choice = request.Options.ToolChoice;
            if (choice == "required" && request.Tools.Count == 0 && tools.Definitions.Count == 0)
                throw new ArgumentException("tool_choice=required needs at least one available tool.");
            if (choice != "auto" && choice != "none" && choice != "required" && !request.Tools.Any(t => t.Name == choice) && !tools.TryGet(choice, out _))
                throw new ArgumentException("Named tool_choice is not available.");
            ConversationRules.Validate(task.History.Select(h => h.Message).Concat(request.Messages));
        }
        private async Task AdvanceAsync(AgentTaskState task, TaskTurn turn, ITaskLease lease, CancellationToken cancellationToken)
        {
            foreach (var pending in task.Operations.Where(o => o.TurnId == turn.Id && o.Status == OperationStatus.Started))
            { pending.Status = OperationStatus.Unknown; pending.Effect = EffectState.Unknown; }
            foreach (var interrupted in task.Steps.Where(s => s.TurnId == turn.Id && s.Status == StepStatus.Running))
            { interrupted.Status = StepStatus.Interrupted; task.Budget.Usage.Known = false; }
            while (true)
            {
            cancellationToken.ThrowIfCancellationRequested();
            if (UncertainResources(task))
            { Wait(task, turn, WaitReason.Reconciliation, "uncertain_resource_or_input"); await CommitAsync(lease, task); return; }
            if (await YieldForSteeringAsync(task, turn, lease)) return;
            if (task.ResourceWaits.Any(w => w.TurnId == turn.Id && w.Status == "active") &&
                task.Operations.Where(o => o.TurnId == turn.Id).All(o => o.Status == OperationStatus.Finished))
                if (!await ConsumeWaitAsync(task, turn, lease)) return;
            var candidateWait = task.ResourceWaits.LastOrDefault(w => w.TurnId == turn.Id && w.CompletionCandidate && w.Status != "active" && w.Status != "invalidated");
            if (candidateWait != null && turn.LastResponse?.Message.ToolCalls.Count == 0)
            {
                candidateWait.CompletionCandidate = false;
                // A timeout is feedback for a new decision, not permission to
                // register another wait for the same stale completion candidate.
                if (candidateWait.Status == "consumed" && await FinishCandidateAsync(task, turn, lease, cancellationToken)) return;
            }
            await RefreshWorkspaceAsync(task, turn, cancellationToken).ConfigureAwait(false);
            if (!await ProcessOperationsAsync(task, turn, lease, cancellationToken).ConfigureAwait(false)) return;
            FlushObservations(task, turn);
            if (turn.Wait == WaitReason.Evidence && turn.LastResponse != null && turn.LastResponse.Message.ToolCalls.Count == 0)
                if (await FinishCandidateAsync(task, turn, lease, cancellationToken).ConfigureAwait(false)) return;
            if (task.Budget.ModelCalls + task.Budget.ReservedModelCalls >= task.Budget.MaxModelCalls || task.Budget.Usage.TotalTokens + task.Budget.ReservedTokens >= task.Budget.MaxTokens ||
                (options.StopWhenUsageUnknown && !task.Budget.Usage.Known))
            {
                Wait(task, turn, WaitReason.Budget, "task_budget"); await CommitAsync(lease, task).ConfigureAwait(false); return;
            }
            task.Status = AgentTaskStatus.Running; task.Wait = WaitReason.None; turn.Status = TurnStatus.Running;
            var step = new TaskStep { TurnId = turn.Id, Status = StepStatus.Preparing, Model = TaskJson.Clone(binding), Capabilities = CaptureCapabilities(task), SteeringRevision = task.SteeringRevision };
            task.Steps.Add(step); Emit(task, "StepPreparing", turn);
            await CommitAsync(lease, task).ConfigureAwait(false);
            var prepared = await preparation.PrepareAsync(new ContextPreparation
            {
                Facts = TaskJson.Clone(task.Facts), Step = TaskJson.Clone(step), History = TaskJson.Clone(task.History)
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var view = TaskProjection.ToRun(task, turn);
            view.Request.Messages = TaskJson.Clone(prepared.Messages);
            view.Request.Instructions = "Task facts:\n" + JsonSerializer.Serialize(task.Facts, TaskJson.Options) + "\n" + view.Request.Instructions;
            view.Request.Options.MaxOutputTokens = (int)Math.Min(view.Request.Options.MaxOutputTokens,
                task.Budget.MaxTokens - task.Budget.Usage.TotalTokens - task.Budget.ReservedTokens);
            var request = context.Build(view, step.Capabilities.Tools);
            step.PreparedRequest = TaskJson.Clone(request); step.Status = StepStatus.Running;
            task.Budget.ModelCalls++; turn.ModelCalls++; Emit(task, "ModelStarted", turn);
            await CommitAsync(lease, task).ConfigureAwait(false);
            var response = await model.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
            ToolCallRules.ValidateIdentity(response.Message.ToolCalls, task.History.Select(h => h.Message));
            step.Status = StepStatus.Responded; turn.LastResponse = TaskJson.Clone(response);
            task.Budget.Usage.Add(response.Usage); turn.Usage.Add(response.Usage);
            task.History.Add(new HistoryEntry { TurnId = turn.Id, StepId = step.Id, Message = TaskJson.Clone(response.Message) });
            Emit(task, "ModelResponded", turn);
            if (response.Finish == ModelFinish.Length || response.Finish == ModelFinish.Refused || response.Finish == ModelFinish.ContentFiltered)
            {
                Wait(task, turn, WaitReason.ModelLimit, response.Finish.ToString()); await CommitAsync(lease, task).ConfigureAwait(false); return;
            }
            ToolCallRules.ValidateChoice(request.Options.ToolChoice, response.Message.ToolCalls);
            if (response.Message.ToolCalls.Count == 0 && await YieldForSteeringAsync(task, turn, lease)) return;
            if ((options.StopWhenUsageUnknown && !task.Budget.Usage.Known) || task.Budget.Usage.TotalTokens >= task.Budget.MaxTokens)
            {
                Wait(task, turn, WaitReason.Budget, task.Budget.Usage.Known ? "token_budget" : "unknown_token_usage"); await CommitAsync(lease, task).ConfigureAwait(false); return;
            }
            if (response.Message.ToolCalls.Count > 0)
            {
                foreach (var call in response.Message.ToolCalls)
                    task.Operations.Add(new ToolOperation { TurnId = turn.Id, StepId = step.Id, Call = TaskJson.Clone(call), ClientOwned = turn.Settings.Tools.Any(t => t.Name == call.Name) });
                await CommitAsync(lease, task).ConfigureAwait(false);
                continue;
            }
            else if (response.Finish != ModelFinish.Stop)
                Wait(task, turn, WaitReason.ModelLimit, response.Finish.ToString());
            else
            {
                if (await FinishCandidateAsync(task, turn, lease, cancellationToken).ConfigureAwait(false)) return;
                continue;
            }
            await CommitAsync(lease, task).ConfigureAwait(false);
            return;
            }
        }
        private static void Wait(AgentTaskState task, TaskTurn turn, WaitReason reason, string detail)
        {
            task.Status = AgentTaskStatus.Waiting; task.Wait = reason; turn.Status = TurnStatus.Waiting; turn.Wait = reason; turn.StopReason = detail;
            Emit(task, "Waiting", turn, detail);
        }
        private static void Emit(AgentTaskState task, string type, TaskTurn? turn = null, string detail = "", ToolOperation? operation = null)
            => task.Events.Add(new TaskEvent
            {
                TaskId = task.Id, TurnId = turn?.Id, OperationId = operation?.Id, Sequence = task.Events.Count + 1, Type = type, Detail = detail
            });
        private static Task CommitAsync(ITaskLease lease, AgentTaskState task) => lease.CommitAsync(task, task.Revision, CancellationToken.None);
        public async ValueTask DisposeAsync()
        {
            await StopAdvancementAsync();
            disposed = true;
        }
        public async Task StopAdvancementAsync()
        {
            stopping = true;
            foreach (var slot in slots.Values)
                lock (slot.Sync) { slot.Interrupt = TaskCommandKind.Pause; slot.Cancellation?.Cancel(); }
            try { await Task.WhenAll(active.Values.ToArray()).ConfigureAwait(false); }
            catch (Exception) { /* The originating caller observes errors; committed state remains authoritative. */ }
        }
        private sealed class Slot
        {
            public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);
            public SemaphoreSlim Admission { get; } = new SemaphoreSlim(1, 1);
            public Dictionary<string, (string Fingerprint, Task<TaskCommandResult> Execution, Task<CommandReceipt> Accepted)> Pending { get; }
                = new Dictionary<string, (string, Task<TaskCommandResult>, Task<CommandReceipt>)>();
            public object Sync { get; } = new object();
            public CancellationTokenSource? Cancellation { get; set; }
            public TaskCommandKind? Interrupt { get; set; }
            public Dictionary<string, TaskCommandKind> Controls { get; } = new Dictionary<string, TaskCommandKind>();
            public HashSet<string> Steering { get; } = new HashSet<string>();
        }
        private static void UpdateInterrupt(Slot slot)
        {
            slot.Interrupt = slot.Controls.Values.Contains(TaskCommandKind.Cancel) ? TaskCommandKind.Cancel
                : slot.Controls.Count > 0 ? TaskCommandKind.Pause : null;
        }
        private static string CommandFingerprint(TaskCommand command)
        {
            // Preserve fingerprints of pre-extension commands in schema v1.
            var original = new { command.Id, command.TaskId, command.Kind, command.Request, command.ForkedFromResponseId,
                command.Facts, command.OperationId, command.Approve, command.Results, command.Evidence, command.ReconciledOutcome };
            if (command.Kind == TaskCommandKind.Steer || command.Kind == TaskCommandKind.ResourceWake || command.Kind == TaskCommandKind.WriteResourceInput ||
                command.Steering != null || command.Wake != null || command.ResourceInput != null)
                return TaskJson.Hash(JsonSerializer.Serialize(new { Version = 2, Command = original, command.ResourceId,
                    command.Steering, command.Wake, command.ResourceInput }, TaskJson.Options));
            if (command.ResourceId == null && command.ChildTaskId == null && command.ParentTaskId == null && command.ChildBudget == null)
                return TaskJson.Hash(JsonSerializer.Serialize(original, TaskJson.Options));
            return TaskJson.Hash(JsonSerializer.Serialize(new { Command = original, command.ResourceId, command.ChildTaskId,
                command.ParentTaskId, command.ChildBudget }, TaskJson.Options));
        }
    }
}
