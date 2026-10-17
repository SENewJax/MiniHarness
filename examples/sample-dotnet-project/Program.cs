using System;
using System.IO;

namespace SampleProject
{
    // Dependency-free self-test harness.
    //
    //   dotnet run --project <root> -- self-test
    //
    // Enumerates tests/*.case (format per line: price percent expected), applies
    // Calculator.ApplyDiscount, and exits non-zero on the first mismatch. The
    // output line "Tests run: N" is the marker the host's TestCountRegex matches,
    // so a workspace with zero *.case files cannot be mistaken for a passing run.
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "self-test")
                return RunSelfTests();

            Console.WriteLine("sample-dotnet-project: pass 'self-test' to run the regression cases.");
            return 0;
        }

        private static int RunSelfTests()
        {
            string root = Directory.GetCurrentDirectory();
            string testsDir = Path.Combine(root, "tests");
            if (!Directory.Exists(testsDir))
            {
                Console.Error.WriteLine("No tests directory found; refusing to report success with zero tests.");
                Console.WriteLine("Tests run: 0");
                return 2;
            }

            var files = Directory.GetFiles(testsDir, "*.case", SearchOption.AllDirectories);
            int ran = 0;
            foreach (string file in files)
            {
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3)
                    {
                        Console.Error.WriteLine("Malformed case in " + Path.GetFileName(file) + ": " + raw);
                        return 2;
                    }

                    int price = int.Parse(parts[0]);
                    int percent = int.Parse(parts[1]);
                    int expected = int.Parse(parts[2]);
                    ran++;

                    int actual = Calculator.ApplyDiscount(price, percent);
                    if (actual != expected)
                    {
                        Console.WriteLine("Tests run: " + ran);
                        Console.Error.WriteLine($"Failed: {Path.GetFileName(file)} ApplyDiscount({price}, {percent}) expected {expected} got {actual}");
                        return 1;
                    }
                }
            }

            if (ran == 0)
            {
                Console.Error.WriteLine("No executable test cases found; refusing to report success with zero tests.");
                Console.WriteLine("Tests run: 0");
                return 2;
            }

            Console.WriteLine("Tests run: " + ran);
            Console.WriteLine("All tests passed.");
            return 0;
        }
    }
}
