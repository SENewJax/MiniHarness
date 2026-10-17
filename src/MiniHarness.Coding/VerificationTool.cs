using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Abstractions;

namespace MiniHarness.Coding
{
    // Host-owned verification command. The model selects a profile by name; it
    // never supplies an executable or a shell command line.
    public sealed class VerificationProfile
    {
        public string Name { get; init; } = "";
        public string FileName { get; init; } = "";
        public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
        public int TimeoutSeconds { get; init; } = 180;
        // Guard against "no tests found" reporting exit code 0. When TestCountRegex
        // is set, the captured count must reach MinimumTests for evidence to pass.
        public int MinimumTests { get; init; }
        public string? TestCountRegex { get; init; }
        public int MaxOutputBytes { get; init; } = 64_000;
        public int MaxLogBytes { get; init; } = 1_000_000;
    }

    public sealed class VerificationOptions
    {
        public string ArtifactRoot { get; init; } = "";
    }

    /// <summary>
    /// run_verification(profile): executes a host-allowed build/test profile as a
    /// one-shot process, fingerprints the workspace before and after, and returns
    /// a real EvidenceRecord bound to the resulting revision. A non-zero exit code
    /// is a model-visible verification failure, not a task-fatal exception.
    /// </summary>
    public sealed class VerificationTool : CodingToolBase, IToolTimeout, IVersionedTool
    {
        private readonly IReadOnlyDictionary<string, VerificationProfile> profiles;
        private readonly ProcessRunner runner;
        private readonly IWorkspaceRevisionProvider revisionProvider;
        private readonly string artifactRoot;

        public VerificationTool(IEnumerable<VerificationProfile> profiles, IWorkspaceRevisionProvider revisionProvider,
            string artifactRoot, WorkspaceOptions? workspaceOptions = null, ProcessRunner? runner = null)
            : base(workspaceOptions)
        {
            this.profiles = (profiles ?? Array.Empty<VerificationProfile>())
                .ToDictionary(p => p.Name, StringComparer.Ordinal);
            this.revisionProvider = revisionProvider;
            this.artifactRoot = string.IsNullOrWhiteSpace(artifactRoot)
                ? throw new ArgumentException("Verification artifact root is required.") : Path.GetFullPath(artifactRoot);
            this.runner = runner ?? new ProcessRunner();
        }

        public int TimeoutSeconds => !profiles.Any() ? 60 : profiles.Values.Max(p => p.TimeoutSeconds) + 15;

        // Changing any host command template moves this version, which the runtime
        // already includes in the capability snapshot: stale operations are invalidated.
        public string Version
        {
            get
            {
                var canonical = string.Join("\n", profiles.Values
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => p.Name + "|" + p.FileName + "|" + string.Join(" ", p.Arguments) +
                        "|" + p.TimeoutSeconds + "|" + p.MinimumTests + "|" + p.TestCountRegex));
                return WorkspaceAccess.HashBytes(Encoding.UTF8.GetBytes(canonical)).Substring(0, 16);
            }
        }

        public override ToolDefinition Definition { get; } = Def("run_verification",
            "Run a host-approved build or test profile (chosen by name) and record real evidence: exit code, output and workspace revision. A non-zero exit code is a test failure you must fix, not a fatal error.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"profile\":{\"type\":\"string\",\"description\":\"Allowed profile name, e.g. build, unit_tests, regression.\"}}," +
            "\"required\":[\"profile\"],\"additionalProperties\":false}");

        protected override async Task<ToolOutcome> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
        {
            var access = new WorkspaceAccess(context.Workspace, Options);
            access.RequireGrant(WorkspaceGrants.Execute);

            string name = String(args, "profile", "");
            if (!profiles.TryGetValue(name, out var profile))
                return Fail("Unknown verification profile '" + name + "'. Allowed: " + string.Join(", ", profiles.Keys.OrderBy(k => k, StringComparer.Ordinal)));

            string root = access.Root;
            string before = await SafeRevisionAsync(context.Workspace, ct).ConfigureAwait(false);

            var spec = new ProcessSpecification
            {
                FileName = profile.FileName, Arguments = profile.Arguments, WorkingDirectory = root,
                TimeoutSeconds = profile.TimeoutSeconds, MaxOutputBytes = profile.MaxOutputBytes, MaxLogBytes = profile.MaxLogBytes
            };
            var result = await runner.RunAsync(spec, ct).ConfigureAwait(false);
            string after = await SafeRevisionAsync(context.Workspace, ct).ConfigureAwait(false);
            bool changedDuringRun = before != after;

            var summary = new
            {
                profile = name,
                state = result.State.ToString(),
                exitCode = result.ExitCode,
                elapsedMs = result.ElapsedMilliseconds,
                beforeRevision = before,
                afterRevision = after,
                changedDuringRun,
                outputTruncated = result.OutputTruncated,
                stdoutTail = Tail(result.StandardOutput, 4000),
                stderrTail = Tail(result.StandardError, 4000)
            };

            // A process tree that could not be confirmed terminated has unknown
            // side effects: force the runtime's reconciliation flow.
            if (result.State == ProcessOutcomeState.KillUnconfirmed)
                return new ToolOutcome { Status = ToolCallStatus.Failed, Effect = EffectState.Unknown, Output = ToJson(summary) };

            int? testCount = null; bool testCountKnown = false;
            if (profile.TestCountRegex != null)
            {
                var match = Regex.Match(result.StandardOutput, profile.TestCountRegex);
                if (match.Success && match.Groups.Count > 1 && int.TryParse(match.Groups[1].Value, out int parsed))
                { testCount = parsed; testCountKnown = true; }
            }

            bool passed = result.State == ProcessOutcomeState.Exited && result.ExitCode == 0 && !changedDuringRun;
            string failureReason = "";
            if (result.State != ProcessOutcomeState.Exited) failureReason = "Verification did not complete: " + result.State;
            else if (result.ExitCode != 0) failureReason = "Verification failed with exit code " + result.ExitCode + ".";
            else if (changedDuringRun) failureReason = "Workspace changed while verification was running; refusing passing evidence for this revision.";
            else if (testCountKnown && testCount < profile.MinimumTests)
            { passed = false; failureReason = "Only " + testCount + " test(s) executed; the profile requires at least " + profile.MinimumTests + ". A zero-test pass is not accepted."; }
            else if (profile.TestCountRegex != null && !testCountKnown)
            { passed = false; failureReason = "Could not confirm that tests actually executed (test count marker missing from output)."; }

            var artifact = WriteLog(context, profile, spec, before, after, result, testCount, testCountKnown, passed);
            var evidence = new EvidenceRecord
            {
                Requirement = VerificationRequirement(name),
                TaskId = "", OperationId = "",
                WorkspaceRevision = after,
                Passed = passed,
                ExitCode = result.ExitCode,
                Output = (passed ? "PASS " : "FAIL ") + name + " (" + result.State + (failureReason.Length > 0 ? ": " + failureReason : "") + ")",
                Artifacts = { artifact }
            };

            return new ToolOutcome
            {
                Status = ToolCallStatus.Succeeded,
                Effect = EffectState.None,
                Output = ToJson(summary),
                Evidence = { evidence }
            };
        }

        private async Task<string> SafeRevisionAsync(WorkspaceScope scope, CancellationToken ct)
        {
            try { return await revisionProvider.GetRevisionAsync(scope, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { throw new WorkspaceAccessException("Could not fingerprint workspace: " + ex.Message); }
        }

        private ArtifactReference WriteLog(ToolExecutionContext context, VerificationProfile profile, ProcessSpecification spec,
            string before, string after, ProcessRunResult result, int? testCount, bool testCountKnown, bool passed)
        {
            string taskDir = Path.Combine(artifactRoot, Sanitize(context.TaskId), "verification");
            Directory.CreateDirectory(taskDir);
            string file = Path.Combine(taskDir, context.OperationId + "-" + profile.Name + ".log");
            var builder = new StringBuilder();
            builder.AppendLine("profile: " + profile.Name);
            builder.AppendLine("command: " + spec.FileName + " " + string.Join(" ", spec.Arguments));
            builder.AppendLine("workingDirectory: " + spec.WorkingDirectory);
            builder.AppendLine("state: " + result.State);
            builder.AppendLine("exitCode: " + (result.ExitCode?.ToString() ?? "<none>"));
            builder.AppendLine("elapsedMs: " + result.ElapsedMilliseconds);
            builder.AppendLine("passed: " + passed);
            builder.AppendLine("testCount: " + (testCountKnown ? testCount?.ToString() : "<unknown>"));
            builder.AppendLine("outputTruncated: " + result.OutputTruncated);
            builder.AppendLine("revisionBefore: " + before);
            builder.AppendLine("revisionAfter: " + after);
            builder.AppendLine("--- stdout ---");
            builder.AppendLine(result.StandardOutput);
            builder.AppendLine("--- stderr ---");
            builder.AppendLine(result.StandardError);
            byte[] bytes = Encoding.UTF8.GetBytes(Truncate(builder.ToString(), spec.MaxLogBytes));
            File.WriteAllBytes(file, bytes);
            return new ArtifactReference
            {
                Id = "verification-log-" + context.OperationId,
                Location = file,
                ContentHash = WorkspaceAccess.HashBytes(bytes)
            };
        }

        public static string VerificationRequirement(string profileName) => "verification:" + profileName;

        private static string Sanitize(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value;
        }

        private static string Tail(string value, int max) =>
            value.Length <= max ? value : value.Substring(value.Length - max);

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value.Substring(0, max) + "\n[log truncated]";
    }
}
