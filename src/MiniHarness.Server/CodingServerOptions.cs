using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MiniHarness.Abstractions;
using MiniHarness.Coding;
using MiniHarness.Harness;

namespace MiniHarness.Server
{
    // Host configuration for the V1 coding loop. Commands are host-owned profile
    // templates; the model only selects a profile name, never an executable.
    public sealed class CodingOptions
    {
        public bool Enabled { get; set; }
        public string ArtifactDirectory { get; set; } = ".data/artifacts";
        public string[] Grants { get; set; } = new[] { "read", "write", "execute" };
        public string[] RequiredVerification { get; set; } = Array.Empty<string>();
        public string[] IgnoredDirectories { get; set; } = Array.Empty<string>();
        public VerificationProfileOptions[] Profiles { get; set; } = Array.Empty<VerificationProfileOptions>();

        public string ResolveArtifactRoot(string contentRoot) =>
            Path.IsPathRooted(ArtifactDirectory) ? ArtifactDirectory : Path.Combine(contentRoot, ArtifactDirectory);
    }

    public sealed class VerificationProfileOptions
    {
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public string[] Arguments { get; set; } = Array.Empty<string>();
        public int TimeoutSeconds { get; set; } = 180;
        public int MinimumTests { get; set; }
        public string? TestCountRegex { get; set; }
        public int MaxOutputBytes { get; set; } = 64_000;
        public int MaxLogBytes { get; set; } = 1_000_000;

        public VerificationProfile ToProfile() => new VerificationProfile
        {
            Name = Name, FileName = FileName, Arguments = Arguments, TimeoutSeconds = TimeoutSeconds,
            MinimumTests = MinimumTests, TestCountRegex = TestCountRegex,
            MaxOutputBytes = MaxOutputBytes, MaxLogBytes = MaxLogBytes
        };
    }

    // Wires coding tools, change ledger, revision provider and completion policy
    // together from one composition point.
    public sealed class CodingComposition
    {
        public ChangeLedger Ledger { get; }
        public IReadOnlyList<ITool> Tools { get; }
        public CodingCompletionPolicy Completion { get; }
        public IReadOnlyList<string> Grants { get; }
        public IReadOnlyList<string> RequiredEvidence { get; }
        public DirectoryWorkspaceRevision Revision { get; }
        public VerificationTool Verification { get; }

        public CodingComposition(CodingOptions options, string contentRoot)
        {
            string artifactRoot = options.ResolveArtifactRoot(contentRoot);
            Directory.CreateDirectory(artifactRoot);
            Ledger = new ChangeLedger(Path.Combine(artifactRoot, "changes"));
            Grants = options.Grants.ToList();
            Revision = new DirectoryWorkspaceRevision(options.IgnoredDirectories);

            var workspaceOptions = new WorkspaceOptions();
            var profiles = options.Profiles.Select(p => p.ToProfile()).ToList();
            Verification = new VerificationTool(profiles, Revision, artifactRoot, workspaceOptions);

            Tools = new List<ITool>
            {
                new ListFilesTool(workspaceOptions),
                new SearchTextTool(workspaceOptions),
                new ReadFileTool(workspaceOptions),
                new CreateFileTool(Ledger, workspaceOptions),
                new ApplyPatchTool(Ledger, workspaceOptions),
                new GetChangesTool(Ledger, workspaceOptions),
                Verification
            };
            RequiredEvidence = options.RequiredVerification
                .Select(VerificationTool.VerificationRequirement).ToList();
            Completion = new CodingCompletionPolicy();
        }
    }
}
