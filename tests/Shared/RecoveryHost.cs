using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniHarness.Model.OpenAI;

namespace MiniHarness.Testing
{
    public sealed class RecoveryHost : IDisposable
    {
        private readonly Process process;
        private readonly string root;
        private readonly StringBuilder output = new StringBuilder();
        private readonly string key;
        public HttpClient Client { get; }
        public int Pid => process.Id;
        private RecoveryHost(Process process, string root, string key)
        {
            this.process = process; this.root = root; this.key = key;
            Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            process.OutputDataReceived += (_, e) => Capture(e.Data); process.ErrorDataReceived += (_, e) => Capture(e.Data);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        private void Capture(string? line)
        { if (line == null) return; lock (output) { if (output.Length < 16384) output.AppendLine(key.Length == 0 ? line : line.Replace(key, "[REDACTED]", StringComparison.Ordinal)); } }
        public static async Task<RecoveryHost> StartAsync(string root, string phase, bool fake, IEnumerable<string> configuration,
            CancellationToken ct, IDictionary<string, string>? environment = null)
        {
            Directory.CreateDirectory(root);
            foreach (string name in new[] { "host.json", "phase.json", "release" }) File.Delete(Path.Combine(root, name));
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(MiniHarness.RecoveryFixture.FixtureMarker).Assembly.Location);
            start.ArgumentList.Add(root); start.ArgumentList.Add(phase); start.ArgumentList.Add(fake ? "fake" : "real");
            foreach (var argument in configuration) start.ArgumentList.Add(argument);
            if (environment != null) foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
            var process = Process.Start(start)!;
            var host = new RecoveryHost(process, root, environment != null && environment.TryGetValue("CODING_PLAN_APIKEY", out var supplied) ? supplied : CodingPlanConfiguration.Read("CODING_PLAN_APIKEY") ?? "");
            try
            {
                using var ready = await ReadMarkerAsync(Path.Combine(root, "host.json"), ct, () => process.HasExited);
                if (ready.RootElement.GetProperty("pid").GetInt32() != process.Id) throw new InvalidDataException("Host readiness belongs to another process.");
                host.Client.BaseAddress = new Uri(ready.RootElement.GetProperty("address").GetString()!); return host;
            }
            catch { host.Dispose(); throw; }
        }
        public static async Task<JsonDocument> ReadMarkerAsync(string path, CancellationToken ct, Func<bool>? exited = null)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (exited?.Invoke() == true) throw new IOException("Fixture host exited before publishing " + Path.GetFileName(path));
                try
                {
                    // Rename visibility can precede the publisher's handle close.
                    // Sharing DELETE permits that atomic-publication window.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                }
                catch (FileNotFoundException) { }
                catch (IOException error) when (((uint)error.HResult & 0xffff0000u) == 0x80070000u && (error.HResult & 0xffff) is 5 or 32 or 33) { }
                await Task.Delay(10, ct);
            }
        }
        public async Task KillAsync(CancellationToken ct)
        {
            if (!process.HasExited) process.Kill(); // Preserve Job-on-host-death coverage.
            await process.WaitForExitAsync(ct); SaveOutput();
        }
        private void SaveOutput()
        { lock (output) File.WriteAllText(Path.Combine(root, "host-" + process.Id + ".log"), output.ToString()); }
        public void Dispose()
        {
            Client.Dispose();
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); }
            SaveOutput(); process.Dispose();
        }
    }
}
