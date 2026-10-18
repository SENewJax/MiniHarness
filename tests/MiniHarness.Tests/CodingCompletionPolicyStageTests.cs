using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;
using MiniHarness.Coding;

namespace MiniHarness.Tests
{
    // Deterministic, no-process checks for the coding completion gate. These pin
    // the evidence-selection rule: completion is decided by a CURRENT-revision
    // passing record for every requirement, regardless of insertion order, and
    // an older passing record must never shadow a newer current-revision pass.
    internal static class CodingCompletionPolicyStageTests
    {
        public static void Register(List<(string, Func<Task>)> tests)
        {
            tests.Add(("[v1-policy] an old passing record does not shadow a newer current-revision pass", NewEvidenceCompletesScenario));
            tests.Add(("[v1-policy] only stale evidence feeds back a re-run; missing evidence is distinguished", MissingVersusStaleScenario));
            tests.Add(("[v1-policy] budget exhaustion waits instead of looping forever", BudgetWaitScenario));
        }

        private const string Requirement = "verification:unit_tests";
        private const string OldRevision = "rev_old";
        private const string NewRevision = "rev_new";

        private static AgentTaskState State(string revision, params EvidenceRecord[] evidence)
        {
            var state = new AgentTaskState
            {
                Id = "task_policy",
                Facts = new TaskFacts
                {
                    Workspace = new WorkspaceScope { RootPath = Path.Combine(Path.GetTempPath(), "unused"), Revision = revision },
                    RequiredEvidence = { Requirement }
                },
                Evidence = evidence.ToList()
            };
            return state;
        }

        private static EvidenceRecord Pass(string revision, bool exitZero = true) => new EvidenceRecord
        {
            TaskId = "task_policy", Requirement = Requirement, Passed = true,
            ExitCode = exitZero ? 0 : 1, WorkspaceRevision = revision
        };

        private static ModelResponse StopCandidate() => new ModelResponse
        {
            Message = new Message { Role = "assistant", Text = "done" },
            Finish = ModelFinish.Stop,
            Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1 }
        };

        private static async Task NewEvidenceCompletesScenario()
        {
            var policy = new CodingCompletionPolicy();

            // Insertion order deliberately puts the OLD pass first, exactly the
            // shape that previously made FirstOrDefault lock onto the stale record.
            var state = State(NewRevision, Pass(OldRevision), Pass(NewRevision));
            var verdict = await policy.EvaluateAsync(state, StopCandidate(), CancellationToken.None);

            Check.True(verdict.CompleteTask, "a current-revision passing record must complete despite an older pass preceding it");
            Check.True(verdict.CompleteTurn);
            Check.True(!verdict.CanContinue);
        }

        private static async Task MissingVersusStaleScenario()
        {
            var policy = new CodingCompletionPolicy();

            // Never passed at all: missing.
            var missing = State(NewRevision);
            var missingVerdict = await policy.EvaluateAsync(missing, StopCandidate(), CancellationToken.None);
            Check.True(!missingVerdict.CompleteTask && missingVerdict.CanContinue);
            Check.True(missingVerdict.Feedback!.Contains("missing passing evidence", StringComparison.OrdinalIgnoreCase));
            Check.True(!missingVerdict.Feedback.Contains("stale", StringComparison.OrdinalIgnoreCase));

            // Passed on an older revision only: stale, demands a re-run.
            var stale = State(NewRevision, Pass(OldRevision));
            var staleVerdict = await policy.EvaluateAsync(stale, StopCandidate(), CancellationToken.None);
            Check.True(!staleVerdict.CompleteTask && staleVerdict.CanContinue);
            Check.True(staleVerdict.Feedback!.Contains("stale evidence", StringComparison.OrdinalIgnoreCase));

            // Passed with a non-zero exit does not satisfy even on the current revision.
            var badExit = State(NewRevision, Pass(NewRevision, exitZero: false));
            var badVerdict = await policy.EvaluateAsync(badExit, StopCandidate(), CancellationToken.None);
            Check.True(!badVerdict.CompleteTask && badVerdict.CanContinue);
        }

        private static async Task BudgetWaitScenario()
        {
            var policy = new CodingCompletionPolicy();
            var state = State(NewRevision, Pass(OldRevision));
            state.Budget.ModelCalls = state.Budget.MaxModelCalls; // exhausted
            var verdict = await policy.EvaluateAsync(state, StopCandidate(), CancellationToken.None);
            Check.True(!verdict.CompleteTask && !verdict.CanContinue);
            Check.Equal(WaitReason.Budget, verdict.WaitingReason);
        }
    }
}
