using System;
using MiniHarness.Coding.Processes;

namespace MiniHarness.Server
{
    public sealed class ProcessServerOptions
    {
        public bool Enabled { get; set; }
        public ProcessSessionOptions Limits { get; set; } = new ProcessSessionOptions { Directory = ".data/processes" };
        public ProcessProfile[] Profiles { get; set; } = Array.Empty<ProcessProfile>();
        public int PollMilliseconds { get; set; } = 500;
        public string[] Grants { get; set; } = new[] { "read", "execute" };
    }
}
