using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding
{
    /// <summary>
    /// Completion gate for code-repair tasks. The model decides when it is done;
    /// the program decides whether current-version evidence exists for every
    /// required verification. A missing or stale requirement is fed back to the
    /// model (CanContinue) while budget remains, so the loop can read the failure,
    /// edit and re-run verification. It never hard-codes a read/edit/test script.
    /// </summary>
    public sealed class CodingCompletionPolicy : ITaskCompletionPolicy
    {
        public Task<TaskCompletionVerdict> EvaluateAsync(AgentTaskState state, ModelResponse candidate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool stop = candidate.Finish == ModelFinish.Stop && !string.IsNullOrWhiteSpace(candidate.Message.Text);
            string revision = state.Facts.Workspace.Revision;
            var required = state.Facts.RequiredEvidence;

            var missing = new List<string>();
            var stale = new List<string>();
            foreach (var requirement in required)
            {
                // Select in ONE pass against task + requirement + passing + exit 0 +
                // CURRENT revision. Taking the first passing record and only then
                // checking the revision let an older passing run shadow a newer
                // current-revision pass and block completion forever.
                // Latest execution wins: among qualifying records the last appended
                // (most recent run) is authoritative, independent of insertion order.
                bool hasCurrentPass = state.Evidence.Any(e =>
                    e.TaskId == state.Id && e.Requirement == requirement && e.Passed &&
                    (!e.ExitCode.HasValue || e.ExitCode == 0) &&
                    e.WorkspaceRevision == revision);

                if (hasCurrentPass) continue;

                // No current-revision pass. Distinguish "never passed" from "passed
                // only on an older revision" so the feedback names the real gap.
                bool everPassed = state.Evidence.Any(e =>
                    e.TaskId == state.Id && e.Requirement == requirement && e.Passed &&
                    (!e.ExitCode.HasValue || e.ExitCode == 0));
                if (everPassed) stale.Add(requirement); else missing.Add(requirement);
            }

            bool satisfied = required.Count > 0 && missing.Count == 0 && stale.Count == 0;
            if (satisfied && stop)
            {
                return Task.FromResult(new TaskCompletionVerdict
                {
                    CompleteTask = true, CompleteTurn = true, CanContinue = false
                });
            }

            // Defensive: a coding task with zero configured requirements cannot be
            // evidence-gated. Accept a clean stop rather than trap the task forever;
            // the host is expected to declare required verification profiles.
            if (required.Count == 0 && stop)
            {
                return Task.FromResult(new TaskCompletionVerdict
                {
                    CompleteTask = true, CompleteTurn = true, CanContinue = false
                });
            }

            bool budgetExhausted =
                state.Budget.ModelCalls + state.Budget.ReservedModelCalls >= state.Budget.MaxModelCalls ||
                state.Budget.ToolCalls + state.Budget.ReservedToolCalls >= state.Budget.MaxToolCalls ||
                state.Budget.Usage.TotalTokens + state.Budget.ReservedTokens >= state.Budget.MaxTokens;

            if (budgetExhausted)
            {
                return Task.FromResult(new TaskCompletionVerdict
                {
                    CompleteTask = false, CompleteTurn = false, CanContinue = false,
                    WaitingReason = WaitReason.Budget,
                    Feedback = "Budget exhausted before required verification passed: " + Describe(missing, stale)
                });
            }

            // Evidence/fix gap is model-repairable: hand the gap back and keep going.
            return Task.FromResult(new TaskCompletionVerdict
            {
                CompleteTask = false, CompleteTurn = false, CanContinue = true,
                WaitingReason = WaitReason.Evidence,
                Feedback = "Task cannot be completed yet. " + Describe(missing, stale) +
                    " Inspect any failing output, edit the code, then re-run the required verification. " +
                    "Only propose completion after current-version passing evidence exists."
            });
        }

        private static string Describe(List<string> missing, List<string> stale)
        {
            var parts = new List<string>();
            if (missing.Count > 0)
                parts.Add("missing passing evidence for " + string.Join(", ", missing.Select(ShortName)));
            if (stale.Count > 0)
                parts.Add("stale evidence (code changed after the run) for " + string.Join(", ", stale.Select(ShortName)) + "; re-run on the current revision");
            return string.Join("; ", parts) + ".";
        }

        private static string ShortName(string requirement) =>
            requirement.StartsWith("verification:", StringComparison.Ordinal) ? requirement.Substring("verification:".Length) : requirement;
    }
}
