using System;
using System.IO;
using System.Linq;
using MiniHarness.Abstractions;

namespace MiniHarness.Server
{
    public sealed class HostTaskInitializer : ITaskInitializer
    {
        private readonly ServerOptions server;
        private readonly CodingTaskSetup codingSetup;
        public HostTaskInitializer(ServerOptions server, CodingTaskSetup codingSetup)
        { this.server = server; this.codingSetup = codingSetup; }
        public void Initialize(TaskCommand command)
        {
            command.Facts ??= new TaskFacts { Goal = command.Request?.Messages.FirstOrDefault(m => m.Role == "user")?.Text ?? "" };
            string requested = command.Facts.Workspace.RootPath;
            if (server.WorkspaceRoot.Length == 0)
            {
                if (requested.Length > 0) throw new ArgumentException("Configure Server:WorkspaceRoot before using a workspace.");
            }
            else
            {
                string allowed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.WorkspaceRoot));
                string resolved = requested.Length == 0 ? allowed : Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
                if (!resolved.Equals(allowed, StringComparison.OrdinalIgnoreCase) && !resolved.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Workspace is outside the server scope.");
                for (string? path = resolved; path != null; path = Directory.GetParent(path)?.FullName)
                {
                    if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new ArgumentException("Linked workspace paths are not permitted.");
                    if (path.Equals(allowed, StringComparison.OrdinalIgnoreCase)) break;
                }
                command.Facts.Workspace.RootPath = resolved;
            }
            command.Facts.Workspace.Grants = server.WorkspaceGrants.ToList();
            codingSetup.Apply(command.Facts);
        }
    }
}
