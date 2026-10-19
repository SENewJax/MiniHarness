using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHarness.Tests
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Self-hosted target process for the V1 controlled-process tests. The
            // test runner launches an independent copy of itself via
            // Environment.ProcessPath and exercises real exit codes/output/kill.
            if (args.Length >= 1 && args[0] == "--self")
                return SelfTarget(args);

            var tests = new List<(string Name, Func<Task> Run)>();
            HarnessTests.Register(tests);
            ProtocolTests.Register(tests);
            HttpTests.Register(tests);
            TaskStageTests.Register(tests);
            ConsistencyTests.Register(tests);
            V2ProcessTests.Register(tests);
            V2RuntimeTests.Register(tests);
            V2ConsistencyTests.Register(tests);
            CodingStageTests.Register(tests);
            VerificationStageTests.Register(tests);
            CodingCompletionPolicyStageTests.Register(tests);
            CodingEndToEndStageTests.Register(tests);
            string stage = args.Length == 2 && args[0] == "--stage" ? args[1] : "all";
            if (args.Length != 0 && (args.Length != 2 || args[0] != "--stage"))
            {
                Console.WriteLine("Usage: --stage all|legacy|1|2|3|4-5|6|v0-control|v0-replan|v0-resource|v0-child|v0-storage|v1-workspace|v1-mutation|v1-ledger|v1-tool|v1-process|v1-verify|v1-policy|v1-e2e|consistency|v2-session|v2-output|v2-resource|v2-wait|v2-steer|v2-recovery|v2-consistency"); return 2;
            }
            tests = tests.FindAll(t => stage == "all" || (stage == "legacy" ? !t.Name.StartsWith("[", StringComparison.Ordinal)
                : t.Name.StartsWith("[" + stage + "]", StringComparison.Ordinal)));
            if (tests.Count == 0) { Console.WriteLine("No tests registered for stage " + stage); return 2; }
            int failures = 0, skipped = 0;
            foreach (var test in tests)
            {
                try { await test.Run().ConfigureAwait(false); Console.WriteLine("PASS " + test.Name); }
                catch (TestSkippedException ex) { skipped++; Console.WriteLine("SKIP " + test.Name + ": " + ex.Message); }
                catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + "\n" + ex); }
            }
            Console.WriteLine($"{tests.Count - failures - skipped}/{tests.Count} tests passed; {skipped} skipped; {failures} failed.");
            return failures == 0 ? 0 : 1;
        }

        // Deterministic child program used by V1 process tests:
        //   --self exit <code>              exit with a real code
        //   --self chatter <lines>          emit that many stdout lines (pipe-drain test)
        //   --self tests <count>            print a "Tests run: <count>" marker
        //   --self sleep <seconds>          ignore nothing; killed on timeout
        //   --self echoerr <text>           write to stderr and exit 3
        private static int SelfTarget(string[] args)
        {
            string mode = args.Length > 1 ? args[1] : "exit";
            string value = args.Length > 2 ? args[2] : "0";
            switch (mode)
            {
                case "exit":
                    return int.TryParse(value, out int code) ? code : 0;
                case "echoerr":
                    Console.Error.WriteLine(value);
                    return 3;
                case "chatter":
                    int lines = int.TryParse(value, out lines) ? lines : 0;
                    for (int i = 0; i < lines; i++) Console.WriteLine("line " + i + " " + new string('x', 64));
                    return 0;
                case "chatter-forever":
                    // Emit continuously until killed. Used to prove the interrupted
                    // cleanup path has a hard wall-clock ceiling even while pipes are
                    // still carrying data at cancellation time. Optional argument is a
                    // pid file so callers can track/reap the real OS process.
                    string foreverPid = args.Length > 2 ? args[2] : "";
                    if (foreverPid.Length > 0) PublishPid(foreverPid, Environment.ProcessId);
                    while (true) { Console.WriteLine("forever " + new string('y', 64)); Thread.Sleep(5); }
                case "tests":
                    int count = int.TryParse(value, out count) ? count : 0;
                    Console.WriteLine("Tests run: " + count);
                    return 0;
                case "sleep":
                    int seconds = int.TryParse(value, out seconds) ? seconds : 30;
                    Thread.Sleep(TimeSpan.FromSeconds(seconds));
                    return 0;
                case "sleep-pid":
                    // Write this process id, then sleep. A cancellation test uses the
                    // pid file to assert the real process (not just the handle) died.
                    string pidFile = args.Length > 2 ? args[2] : "";
                    int pidSeconds = args.Length > 3 && int.TryParse(args[3], out int ps) ? ps : 30;
                    if (pidFile.Length > 0) PublishPid(pidFile, Environment.ProcessId);
                    Thread.Sleep(TimeSpan.FromSeconds(pidSeconds));
                    return 0;
                case "spawn-sleep":
                    // Spawn a detached grandchild that writes its own pid and sleeps,
                    // then sleep in the parent. Killing only the parent would orphan
                    // the grandchild; tree kill must reclaim both.
                    string childPid = args.Length > 2 ? args[2] : "";
                    int treeSeconds = args.Length > 3 && int.TryParse(args[3], out int ts) ? ts : 30;
                    var child = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath!,
                        Arguments = "--self sleep-pid \"" + childPid + "\" " + treeSeconds,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    System.Diagnostics.Process.Start(child);
                    if (childPid.Length > 0) PublishPid(childPid + ".parent", Environment.ProcessId);
                    Thread.Sleep(TimeSpan.FromSeconds(treeSeconds));
                    return 0;
                case "spawn-hold":
                    // Start a child that INHERITS our stdout/stderr handles (no
                    // redirect), then the launcher exits immediately. The inherited
                    // handles are the runner's redirected pipes, so the surviving
                    // child keeps EOF from arriving even though the parent is gone.
                    // Models a build launcher that returns while a watcher lingers.
                    // The launcher records the child pid itself before exiting, so
                    // the test can always identify the orphan even if the tree is
                    // reclaimed before the child gets to write its own file.
                    string holdPid = args.Length > 2 ? args[2] : "";
                    int holdSeconds = args.Length > 3 && int.TryParse(args[3], out int hs) ? hs : 30;
                    var holder = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath!,
                        Arguments = "--self hold-pid \"" + holdPid + "\" " + holdSeconds,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var holderProc = System.Diagnostics.Process.Start(holder)!;
                    if (holdPid.Length > 0) PublishPid(holdPid, holderProc.Id);
                    return 0;
                case "hold-pid":
                    // Record our pid (best effort; the launcher also records it),
                    // then sleep while keeping the inherited stdout handle (the
                    // runner's pipe) open without writing to it.
                    string selfPidFile = args.Length > 2 ? args[2] : "";
                    int selfSeconds = args.Length > 3 && int.TryParse(args[3], out int ss) ? ss : 30;
                    if (selfPidFile.Length > 0)
                    {
                        try { PublishPid(selfPidFile, Environment.ProcessId); }
                        catch { }
                    }
                    Thread.Sleep(TimeSpan.FromSeconds(selfSeconds));
                    return 0;
                case "touch":
                    System.IO.File.WriteAllText(value, "changed during verification\n");
                    return 0;
                case "project-tests":
                    return RunProjectTests();
                default:
                    return 0;
            }
        }

        private static void PublishPid(string path, int pid)
        {
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(pending, pid.ToString());
                try { File.Move(pending, path); }
                catch (IOException) when (File.Exists(path))
                { /* The launcher and its child may publish the same identity. */ }
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }

        // Real project-under-test for the V1 end-to-end loop. Working directory is
        // the workspace root. Every tests/*.tst file is one discovered test: its
        // trimmed content is the expected value and value.txt holds the actual
        // value. Exit code is zero only when every discovered test passes.
        private static int RunProjectTests()
        {
            string root = Directory.GetCurrentDirectory();
            string valuePath = System.IO.Path.Combine(root, "value.txt");
            string testsDir = System.IO.Path.Combine(root, "tests");
            string actual = System.IO.File.Exists(valuePath) ? System.IO.File.ReadAllText(valuePath).Trim() : "";
            var testFiles = Directory.Exists(testsDir)
                ? Directory.GetFiles(testsDir, "*.tst").OrderBy(p => p, StringComparer.Ordinal).ToList()
                : new List<string>();
            int failures = 0;
            foreach (var file in testFiles)
            {
                string expected = System.IO.File.ReadAllText(file).Trim();
                if (expected != actual)
                {
                    failures++;
                    Console.Error.WriteLine("Failed: " + System.IO.Path.GetFileName(file) + " expected '" + expected + "' got '" + actual + "'");
                }
                else Console.WriteLine("Passed: " + System.IO.Path.GetFileName(file));
            }
            Console.WriteLine("Tests run: " + testFiles.Count);
            return failures == 0 ? 0 : 1;
        }
    }
    internal sealed class TestSkippedException : Exception
    {
        public TestSkippedException(string message) : base(message) { }
    }

    internal static class Check
    {
        public static void True(bool condition, string message = "Assertion failed")
        {
            if (!condition) throw new Exception(message);
        }
        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"Expected {expected}, got {actual}.");
        }
        public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action().ConfigureAwait(false); }
            catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
    }
}
