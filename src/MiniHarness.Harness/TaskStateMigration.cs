using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public static class TaskStateMigration
    {
        public static AgentTaskState Read(JsonElement json)
        {
            if (!json.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                (version.GetInt32() != 1 && version.GetInt32() != AgentTaskState.CurrentSchemaVersion))
                throw new InvalidDataException("Unsupported task checkpoint schema version.");
            var task = json.Deserialize<AgentTaskState>(TaskJson.Options) ?? throw new InvalidDataException("Null task checkpoint.");
            if (version.GetInt32() == 1)
            {
                task.SchemaVersion = AgentTaskState.CurrentSchemaVersion;
                if (task.Budget == null || task.History == null || task.Children == null || task.Turns == null) throw new InvalidDataException("Invalid legacy task collections.");
                task.ContextMemory = new ContextMemory(); task.Compactions = new System.Collections.Generic.List<CompactionRecord>(); task.ModelInvocations = new System.Collections.Generic.List<ModelInvocationRecord>();
                task.Recovery = new RecoveryRecord(); task.ModelLedgerInitialized = false;
                InitializeLedger(task);
            }
            Validate(task); PopulateResponseBounds(task); return task;
        }
        private static void PopulateResponseBounds(AgentTaskState task)
        {
            foreach (var turn in task.Turns.Where(t => t.Status == TurnStatus.Completed && !t.ResponseHistoryCount.HasValue))
            {
                int ordinal = task.Turns.IndexOf(turn);
                int end = ordinal + 1 < task.Turns.Count ? task.History.FindIndex(h => h.TurnId == task.Turns[ordinal + 1].Id) : task.History.Count;
                if (end < 0) end = task.History.Count;
                int lateChild = task.Children.Count == 0 ? -1 : task.History.FindIndex(0, end, h => h.TurnId == turn.Id &&
                    h.Message.Role == "system" && h.Message.Text.StartsWith("Child task result: ", StringComparison.Ordinal));
                turn.ResponseHistoryCount = lateChild >= 0 ? lateChild : end;
            }
        }
        public static void InitializeLedger(AgentTaskState task)
        {
            if (task.ModelLedgerInitialized) return;
            task.LegacyUsage = TaskJson.Clone(task.Budget.Usage);
            var collected = task.Children.Where(c => c.Collected && c.Usage != null).ToList();
            task.LegacyUsage.InputTokens -= collected.Sum(c => c.Usage!.Usage.InputTokens);
            task.LegacyUsage.OutputTokens -= collected.Sum(c => c.Usage!.Usage.OutputTokens);
            task.LegacyModelCalls = task.Budget.ModelCalls - collected.Sum(c => c.Usage!.ModelCalls);
            if (task.LegacyUsage.InputTokens < 0 || task.LegacyUsage.OutputTokens < 0 || task.LegacyModelCalls < 0)
                throw new InvalidDataException("Legacy aggregate is smaller than its collected child costs.");
            task.LegacyUnknown = !task.Budget.Usage.Known && (!collected.Any(c => !c.Usage!.Usage.Known) || task.Turns.Any(t => !t.Usage.Known));
            task.LegacyUsage.Known = !task.LegacyUnknown;
            task.LegacyTurns = task.Turns.Select(t => new LegacyTurnUsage { TurnId = t.Id, Usage = TaskJson.Clone(t.Usage) }).ToList();
            task.ModelLedgerInitialized = true;
        }
        public static void Validate(AgentTaskState task)
        {
            if (task.History == null || task.Turns == null || task.Steps == null || task.Operations == null || task.Budget == null ||
                task.Compactions == null || task.ModelInvocations == null || task.ContextMemory == null || task.Recovery == null ||
                task.Facts == null || task.Model == null || task.Receipts == null || task.Resources == null || task.Evidence == null ||
                task.History.Any(h => h == null || h.Message == null || string.IsNullOrWhiteSpace(h.Id)) ||
                task.History.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count() != task.History.Count ||
                task.ModelInvocations.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != task.ModelInvocations.Count ||
                task.Compactions.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != task.Compactions.Count ||
                task.Budget.ModelCalls < 0 || task.Budget.ToolCalls < 0 || task.Budget.Usage.InputTokens < 0 || task.Budget.Usage.OutputTokens < 0 ||
                !Enum.IsDefined(task.Status) || !Enum.IsDefined(task.Wait) || task.Turns.Any(t => !Enum.IsDefined(t.Status) || !Enum.IsDefined(t.Wait)) ||
                task.Turns.Any(t => t.ResponseHistoryCount.HasValue && (t.ResponseHistoryCount < 0 || t.ResponseHistoryCount > task.History.Count)) ||
                task.Steps.Any(s => !Enum.IsDefined(s.Status)) || task.Operations.Any(o => !Enum.IsDefined(o.Status) || !Enum.IsDefined(o.Effect) || !Enum.IsDefined(o.Approval)) ||
                task.ModelInvocations.Any(i => !Enum.IsDefined(i.Status) || !Enum.IsDefined(i.Purpose)) || task.Compactions.Any(c => !Enum.IsDefined(c.Status)))
                throw new InvalidDataException("Invalid task state collections, history identities or accounting.");
            if (task.ModelLedgerInitialized && task.Budget.ModelCalls != task.LegacyModelCalls + task.ModelInvocations.Count +
                task.Children.Where(c => c.Collected && c.Usage != null).Sum(c => c.Usage!.ModelCalls))
                throw new InvalidDataException("Model call aggregate does not match legacy, local invocation and collected child records.");
        }
    }
}
