using System;
using System.Collections.Generic;

namespace MiniHarness.Coding.Processes
{
    public sealed class ProcessProfile
    {
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public string[] Arguments { get; set; } = Array.Empty<string>();
        public bool AllowStdin { get; set; }
        public int LifetimeSeconds { get; set; } = 300;
    }
    public sealed class ProcessSessionOptions
    {
        public string Directory { get; set; } = "";
        public int MaxSessions { get; set; } = 8;
        public int MaxSessionsPerTask { get; set; } = 2;
        public int MaxLifetimeSeconds { get; set; } = 3600;
        public int MaxOutputMemoryBytes { get; set; } = 65536;
        public int MaxOutputDiskBytes { get; set; } = 262144;
        public int MaxPageBytes { get; set; } = 8192;
        public int MaxInputBytes { get; set; } = 4096;
        public int MaxInputActions { get; set; } = 256;
        public int InputTimeoutMilliseconds { get; set; } = 2000;
        public int StartTimeoutMilliseconds { get; set; } = 3000;
        public int CancelTimeoutMilliseconds { get; set; } = 5000;
        public int MaxWaitMilliseconds { get; set; } = 60000;
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Directory) || MaxSessions < 1 || MaxSessionsPerTask < 1 || MaxOutputMemoryBytes < 4096 ||
                MaxOutputDiskBytes < 4096 || MaxPageBytes < 256 || MaxPageBytes > MaxOutputMemoryBytes || MaxInputBytes < 1 ||
                MaxInputActions < 1 || MaxInputActions > 4096 || InputTimeoutMilliseconds < 1 || StartTimeoutMilliseconds < 1 || CancelTimeoutMilliseconds < 1 || CancelTimeoutMilliseconds > 7000 ||
                MaxWaitMilliseconds < 1 || MaxWaitMilliseconds > 600000 || MaxLifetimeSeconds < 1 || MaxLifetimeSeconds > 86400)
                throw new ArgumentException("Invalid process session limits.");
        }
    }
}
