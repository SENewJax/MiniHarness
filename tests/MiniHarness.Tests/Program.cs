using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MiniHarness.Tests
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var tests = new List<(string Name, Func<Task> Run)>();
            HarnessTests.Register(tests);
            ProtocolTests.Register(tests);
            HttpTests.Register(tests);
            TaskStageTests.Register(tests);
            string stage = args.Length == 2 && args[0] == "--stage" ? args[1] : "all";
            if (args.Length != 0 && (args.Length != 2 || args[0] != "--stage"))
            {
                Console.WriteLine("Usage: --stage all|legacy|1|2|3|4-5|6|v0-control|v0-replan|v0-resource|v0-child"); return 2;
            }
            tests = tests.FindAll(t => stage == "all" || (stage == "legacy" ? !t.Name.StartsWith("[", StringComparison.Ordinal)
                : t.Name.StartsWith("[" + stage + "]", StringComparison.Ordinal)));
            if (tests.Count == 0) { Console.WriteLine("No tests registered for stage " + stage); return 2; }
            int failures = 0;
            foreach (var test in tests)
            {
                try { await test.Run().ConfigureAwait(false); Console.WriteLine("PASS " + test.Name); }
                catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + "\n" + ex); }
            }
            Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed.");
            return failures == 0 ? 0 : 1;
        }
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
