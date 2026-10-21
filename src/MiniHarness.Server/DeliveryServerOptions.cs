using System;
using System.Linq;
using MiniHarness.Abstractions;
using MiniHarness.Harness;
using MiniHarness.Harness.Acceptance;

namespace MiniHarness.Server
{
    public sealed class DeliveryServerOptions
    {
        public bool Enabled { get; set; }
        public string Directory { get; set; } = ".data/delivery";
        public DeliveryOptions Limits { get; set; } = new DeliveryOptions();
        public AcceptanceSpec Template { get; set; } = new AcceptanceSpec();
        public bool AllowReview { get; set; }
        public bool AllowAmendment { get; set; }
        public bool AllowExternalEvidence { get; set; }
        public bool AllowContinue { get; set; }
        public bool AllowArtifactImport { get; set; }
        public string Actor { get; set; } = "local-host";
    }
    public sealed class HostAcceptancePolicy : IAcceptanceAuthorizer
    {
        private readonly DeliveryServerOptions options;
        public HostAcceptancePolicy(DeliveryServerOptions options) { this.options = options; }
        public string Actor => options.Actor;
        public string PolicyVersion => options.Template.PolicyVersion;
        public bool Allows(AgentTaskState task, TaskCommandKind action) => options.Enabled && (action == TaskCommandKind.ReviewDelivery ? options.AllowReview :
            action == TaskCommandKind.AmendAcceptanceSpec ? options.AllowAmendment : action == TaskCommandKind.ContinueAfterNoProgress ? options.AllowContinue :
            action == TaskCommandKind.RegisterDeliveryArtifact ? options.AllowArtifactImport : options.AllowExternalEvidence);
        public bool ValidSpec(AgentTaskState task, AcceptanceSpec spec) => options.Enabled && spec.PolicyVersion == PolicyVersion &&
            options.Template.Criteria.Where(c => c.Required).All(required => spec.Criteria.Any(c => c.Id == required.Id && AcceptanceBinding.Hash(c) == AcceptanceBinding.Hash(required))) &&
            options.Template.ProtectedPaths.All(spec.ProtectedPaths.Contains) && spec.AllowNewFilesUnder.All(options.Template.AllowNewFilesUnder.Contains);
    }
}
