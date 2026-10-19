using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Linq;
using MiniHarness.Abstractions;
using MiniHarness.Coding.Processes;

namespace MiniHarness.ProcessFixture
{
    public static class FixtureMarker { }
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
            string mode = args.Length == 0 ? "echo" : args[0];
            if (mode == "owner")
            {
                string directory = args[1];
                await using var manager = new ProcessSessionManager(new ProcessSessionOptions { Directory = Path.Combine(directory, "state") },
                    new[] { new ProcessProfile { Name = "tree", FileName = "dotnet", Arguments = new[] { typeof(FixtureMarker).Assembly.Location, "tree" }, LifetimeSeconds = 30 } });
                var resource = await manager.StartAsync("tree", new ToolExecutionContext { TaskId = "task_owner", OperationId = "op_owner",
                    Workspace = new WorkspaceScope { RootPath = Path.Combine(directory, "workspace"), Grants = { "execute" } } }, default);
                string output;
                do { output = string.Concat((await manager.ReadOutputAsync(resource, 0, 8192, default)).Chunks.Select(c => c.Text));
                    if (System.Text.RegularExpressions.Regex.IsMatch(output, @"CHILD:\d+\r?\n")) break; await Task.Delay(10); } while (true);
                string ready = Path.Combine(directory, "owner-ready.json");
                await File.WriteAllTextAsync(ready + ".tmp", JsonSerializer.Serialize(new { Resource = resource, Output = output }));
                File.Move(ready + ".tmp", ready); // Publish only a closed, complete readiness record.
                await Task.Delay(-1); return 0;
            }
            if (mode == "flood")
            {
                for (int i = 0; i < 600; i++) { Console.WriteLine("中文🙂stdout-" + i); Console.Error.WriteLine("中文🙂stderr-" + i); }
                return 7;
            }
            if (mode == "tree" || mode == "orphan")
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                start.ArgumentList.Add(typeof(FixtureMarker).Assembly.Location); start.ArgumentList.Add("hold");
                using var child = Process.Start(start)!;
                Console.WriteLine("CHILD:" + child.Id);
                if (mode == "orphan") return 0;
            }
            Console.WriteLine("READY:" + Environment.ProcessId);
            if (mode == "heartbeat")
            { while (true) { Console.WriteLine("tick"); await Task.Delay(100); } }
            if (mode == "hold" || mode == "tree") { await Task.Delay(-1); return 0; }
            while (true)
            {
                string? line = await Console.In.ReadLineAsync();
                if (line == null) { Console.WriteLine("EOF"); return 0; }
                Console.WriteLine("echo:" + line);
                if (line == "exit") return 0;
            }
        }
    }
}
