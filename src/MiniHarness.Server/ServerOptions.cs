using System;

namespace MiniHarness.Server
{
    public sealed class ServerOptions
    {
        public string ModelAlias { get; set; } = "mini-agent";
        public string ApiKey { get; set; } = "";
        public int RequestTimeoutSeconds { get; set; } = 300;
        public long MaxRequestBodyBytes { get; set; } = 1048576;
        public bool CancelOnDisconnect { get; set; }
        public string WorkspaceRoot { get; set; } = "";
        public string[] WorkspaceGrants { get; set; } = Array.Empty<string>();
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ModelAlias) || RequestTimeoutSeconds < 1 || MaxRequestBodyBytes < 1)
                throw new ArgumentException("Invalid Server configuration.");
        }
    }
    public sealed class StorageOptions
    {
        public string Directory { get; set; } = ".data/runs";
    }
    public sealed class ToolsOptions
    {
        public string[] AllowedNames { get; set; } = Array.Empty<string>();
    }
}
