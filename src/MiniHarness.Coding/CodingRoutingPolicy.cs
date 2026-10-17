using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding
{
    /// <summary>
    /// Routes completion evaluation by task shape so enabling coding tools never
    /// changes the behaviour of ordinary (non-workspace) tasks. A task is treated
    /// as a coding task when the host declared verification requirements or granted
    /// workspace execution. Other tasks fall through to the supplied policy, which
    /// preserves the V0 turn-completion semantics.
    /// </summary>
    public sealed class CodingRoutingPolicy : ITaskCompletionPolicy
    {
        private readonly CodingCompletionPolicy coding = new CodingCompletionPolicy();
        private readonly ITaskCompletionPolicy fallback;

        // The fallback for non-coding tasks is supplied by the composition root so
        // that Coding never needs a dependency on the Harness assembly.
        public CodingRoutingPolicy(ITaskCompletionPolicy fallback)
        {
            this.fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        }

        public Task<TaskCompletionVerdict> EvaluateAsync(AgentTaskState state, ModelResponse candidate, CancellationToken cancellationToken)
        {
            bool codingTask =
                state.Facts.RequiredEvidence.Any(r => r.StartsWith("verification:", StringComparison.Ordinal)) ||
                (state.Facts.Workspace?.Grants?.Any(g => g == WorkspaceGrants.Execute) ?? false);
            var selected = codingTask ? coding : fallback;
            return selected.EvaluateAsync(state, candidate, cancellationToken);
        }
    }
}
