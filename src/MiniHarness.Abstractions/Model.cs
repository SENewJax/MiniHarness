using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MiniHarness.Abstractions
{
    public enum ModelFinish { Stop, ToolCalls, Length, Refused, ContentFiltered }
    public enum RunStatus { Running, Completed, WaitingForTools, WaitingForUser, BudgetExhausted, Cancelled, Failed }
    public enum ToolCallStatus { Started, Succeeded, Rejected, Failed, TimedOut }

    public sealed class ToolCall
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "{}";
    }

    public sealed class Message
    {
        public string Role { get; set; } = "user";
        public string Text { get; set; } = "";
        public string? ToolCallId { get; set; }
        public List<ToolCall> ToolCalls { get; set; } = new List<ToolCall>();
        // Ordered, opaque provider continuation items. Complete snapshots are
        // authoritative for replay; Text/ToolCalls are the Harness-facing projection.
        // False preserves legacy checkpoints containing only supplemental reasoning.
        public bool ProviderItemsAreComplete { get; set; }
        public List<JsonElement> ProviderItems { get; set; } = new List<JsonElement>();
    }

    public sealed class ToolDefinition
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public JsonElement Parameters { get; set; }
        public bool? Strict { get; set; }
    }

    public sealed class GenerationOptions
    {
        public int MaxOutputTokens { get; set; } = 1024;
        public double? Temperature { get; set; }
        // auto / none / required / a function name
        public string ToolChoice { get; set; } = "auto";
    }

    public sealed class ModelRequest
    {
        public string Model { get; set; } = "";
        public string Instructions { get; set; } = "";
        public bool StoreResponse { get; set; } = true;
        public List<Message> Messages { get; set; } = new List<Message>();
        public List<ToolDefinition> Tools { get; set; } = new List<ToolDefinition>();
        public GenerationOptions Options { get; set; } = new GenerationOptions();
    }

    public sealed class TokenUsage
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public bool Known { get; set; } = true;
        public long TotalTokens => InputTokens + OutputTokens;
        public void Add(TokenUsage other)
        {
            InputTokens += other.InputTokens;
            OutputTokens += other.OutputTokens;
            Known = Known && other.Known;
        }
    }

    public sealed class ModelResponse
    {
        public Message Message { get; set; } = new Message { Role = "assistant" };
        public ModelFinish Finish { get; set; }
        public TokenUsage Usage { get; set; } = new TokenUsage { Known = false };
    }

    public sealed class ToolResult
    {
        public string CallId { get; set; } = "";
        public ToolCallStatus Status { get; set; }
        public string Output { get; set; } = "";
    }

    public sealed class ToolExecution
    {
        public ToolCall Call { get; set; } = new ToolCall();
        public ToolCallStatus Status { get; set; }
        public ToolResult? Result { get; set; }
    }

    public sealed class RunState
    {
        public string? TaskId { get; set; }
        public string Id { get; set; } = "resp_" + Guid.NewGuid().ToString("N");
        public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public RunStatus Status { get; set; } = RunStatus.Running;
        public string? StopReason { get; set; }
        public ModelRequest Request { get; set; } = new ModelRequest();
        public int ModelCalls { get; set; }
        public int ToolCalls { get; set; }
        public TokenUsage Usage { get; set; } = new TokenUsage();
        public List<ToolExecution> Executions { get; set; } = new List<ToolExecution>();
        public ModelResponse? LastResponse { get; set; }
    }

    public sealed class CompletionVerdict
    {
        public bool Accepted { get; set; }
        public bool CanContinue { get; set; }
        public string Feedback { get; set; } = "";
    }

    public sealed class ToolAdmission
    {
        public bool Allowed { get; set; }
        public bool NeedsUser { get; set; }
        public string Reason { get; set; } = "";
    }

    public sealed class ModelException : Exception
    {
        public ModelException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
