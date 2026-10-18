using System;
using System.IO;

namespace MiniHarness.Testing
{
    // Shared by both test executables. Reports are outputs of the current run,
    // never inputs from a developer's historical runtime data directory.
    internal static class ValidationPaths
    {
        public const string EnvironmentVariable = "MINIHARNESS_VALIDATION_ROOT";

        public static string Root
        {
            get
            {
                string? configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
                return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                    ? Path.Combine(Path.GetTempPath(), "MiniHarness", "validation")
                    : configured);
            }
        }
    }
}
