using System.Collections.Generic;
using MiniHarness.Abstractions;

namespace MiniHarness.Server
{
    /// <summary>
    /// Applies host-declared coding setup at task start: grants come from the host
    /// (never from model parameters), and the required verification profiles are
    /// written into the task facts so the completion gate cannot be bypassed. The
    /// model may run extra verification but cannot remove a required one.
    /// </summary>
    public sealed class CodingTaskSetup
    {
        private readonly bool enabled;
        private readonly IReadOnlyList<string> grants;
        private readonly IReadOnlyList<string> requiredEvidence;

        public CodingTaskSetup()
        {
            enabled = false;
            grants = new List<string>();
            requiredEvidence = new List<string>();
        }

        public CodingTaskSetup(CodingComposition composition)
        {
            enabled = true;
            grants = composition.Grants;
            requiredEvidence = composition.RequiredEvidence;
        }

        public void Apply(TaskFacts facts)
        {
            if (!enabled) return;

            // Host grants win; merge deterministically without duplicates.
            var seen = new HashSet<string>(facts.Workspace.Grants);
            foreach (var grant in grants)
                if (seen.Add(grant)) facts.Workspace.Grants.Add(grant);

            foreach (var requirement in requiredEvidence)
                if (!facts.RequiredEvidence.Contains(requirement))
                    facts.RequiredEvidence.Add(requirement);
        }
    }
}
