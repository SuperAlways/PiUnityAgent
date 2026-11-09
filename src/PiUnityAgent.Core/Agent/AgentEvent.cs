using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public abstract record AgentEvent
{
    private AgentEvent() { }

    public sealed record AgentStart : AgentEvent;
    public sealed record AgentEnd(IReadOnlyList<Message> Messages) : AgentEvent;
    public sealed record TurnStart : AgentEvent;
    public sealed record TurnEnd(AssistantMessage Message, IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent;
    public sealed record MessageStart(Message Message) : AgentEvent;
    public sealed record MessageUpdate(AssistantMessageEvent AssistantMessageEvent, AssistantMessage Message) : AgentEvent;
    public sealed record MessageEnd(Message Message) : AgentEvent;
    public sealed record ToolExecutionStart(string ToolCallId, string ToolName, JsonObject Args) : AgentEvent;
    public sealed record ToolExecutionUpdate(string ToolCallId, string ToolName, JsonObject Args, AgentToolResult PartialResult) : AgentEvent;
    public sealed record ToolExecutionEnd(
        string ToolCallId, string ToolName, AgentToolResult Result, bool IsError, long? DurationMs) : AgentEvent;
}

public delegate Task AgentEventSink(AgentEvent @event);
