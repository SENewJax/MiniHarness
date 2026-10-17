using System;

namespace MiniHarness.Harness
{
    public sealed class HarnessOptions
    {
        public int MaxModelCalls { get; set; } = 12;
        public int MaxToolCalls { get; set; } = 24;
        public long MaxTotalTokens { get; set; } = 50000;
        public int MaxContextCharacters { get; set; } = 100000;
        public int MaxOutputTokens { get; set; } = 2048;
        public int ToolTimeoutSeconds { get; set; } = 10;
        public int MaxToolOutputCharacters { get; set; } = 16000;
        public bool StopWhenUsageUnknown { get; set; } = true;
        public string SystemInstructions { get; set; } = "Use tools when needed. Base your answer on actual observations.";

        public void Validate()
        {
            if (MaxModelCalls < 1 || MaxToolCalls < 0 || MaxTotalTokens < 1 ||
                MaxContextCharacters < 1 || MaxOutputTokens < 1 || ToolTimeoutSeconds < 1 || MaxToolOutputCharacters < 100)
                throw new ArgumentException("Harness limits must be positive (MaxToolCalls may be zero).");
        }
    }
}
