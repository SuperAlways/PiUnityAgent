using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public sealed record AgentToolCallOutcome(ToolCall ToolCall, AgentToolResult Result, bool IsError)
{
    public long? DurationMs { get; init; }
}
