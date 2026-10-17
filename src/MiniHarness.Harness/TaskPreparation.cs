using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Harness
{
    public sealed class PassThroughPreparation : IContextPreparation
    {
        public Task<PreparedContext> PrepareAsync(ContextPreparation input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PreparedContext { Messages = input.History.Select(h => TaskJson.Clone(h.Message)).ToList() });
        }
    }
    public sealed class LegacyCompletionPolicyAdapter : ITaskCompletionPolicy
    {
        private readonly ICompletionPolicy policy;
        public LegacyCompletionPolicyAdapter(ICompletionPolicy policy) { this.policy = policy; }
        public async Task<TaskCompletionVerdict> EvaluateAsync(AgentTaskState state, ModelResponse candidate, CancellationToken ct)
        {
            var verdict = await policy.EvaluateAsync(TaskProjection.ToRun(state, state.Turns.Last()), candidate, ct).ConfigureAwait(false);
            return new TaskCompletionVerdict { CompleteTurn = verdict.Accepted, CanContinue = verdict.CanContinue, Feedback = verdict.Feedback };
        }
    }
    public sealed class EvidenceCompletionPolicy : ITaskCompletionPolicy
    {
        public Task<TaskCompletionVerdict> EvaluateAsync(AgentTaskState state, ModelResponse candidate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var missing = state.Facts.RequiredEvidence.Where(requirement => !state.Evidence.Any(e =>
                e.TaskId == state.Id && e.Requirement == requirement && e.Passed && (!e.ExitCode.HasValue || e.ExitCode == 0) &&
                e.WorkspaceRevision == state.Facts.Workspace.Revision)).ToList();
            return Task.FromResult(new TaskCompletionVerdict
            {
                CompleteTask = missing.Count == 0 && state.Facts.RequiredEvidence.Count > 0 && candidate.Finish == ModelFinish.Stop && !string.IsNullOrWhiteSpace(candidate.Message.Text),
                CompleteTurn = missing.Count == 0 && candidate.Finish == ModelFinish.Stop && !string.IsNullOrWhiteSpace(candidate.Message.Text),
                WaitingReason = WaitReason.Evidence, Feedback = "Missing current evidence: " + string.Join(", ", missing)
            });
        }
    }
    public sealed class DirectoryWorkspaceRevision : IWorkspaceRevisionProvider
    {
        // Build/test outputs and harness state must never move the source fingerprint,
        // otherwise a verification run would invalidate its own passing evidence.
        public static readonly IReadOnlySet<string> DefaultIgnoredDirectories =
            new HashSet<string>(new[] { ".git", ".data", "bin", "obj", "TestResults", ".vs", "node_modules" }, StringComparer.Ordinal);
        private readonly IReadOnlySet<string> ignoredDirectories;

        public DirectoryWorkspaceRevision() : this(null) { }
        public DirectoryWorkspaceRevision(IEnumerable<string>? additionalIgnoredDirectories)
        {
            var set = new HashSet<string>(DefaultIgnoredDirectories, StringComparer.Ordinal);
            if (additionalIgnoredDirectories != null)
                foreach (var name in additionalIgnoredDirectories)
                    if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
            ignoredDirectories = set;
        }

        public async Task<string> GetRevisionAsync(WorkspaceScope workspace, CancellationToken ct)
        {
            if (workspace.RootPath.Length == 0) return workspace.Revision;
            string root = Path.GetFullPath(workspace.RootPath);
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Workspace root must be an existing non-linked directory.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var enumeration = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
            async Task Visit(string directory)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", enumeration).OrderBy(p => p, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(entry))
                    {
                        string name = Path.GetFileName(entry);
                        if (!ignoredDirectories.Contains(name)) await Visit(entry).ConfigureAwait(false);
                    }
                    else
                    {
                        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, entry) + "\0"));
                        using var file = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
                        byte[] buffer = new byte[8192]; int count;
                        while ((count = await file.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0) hash.AppendData(buffer.AsSpan(0, count));
                        hash.AppendData(new byte[] { 0 });
                    }
                }
            }
            await Visit(root).ConfigureAwait(false);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }
}
