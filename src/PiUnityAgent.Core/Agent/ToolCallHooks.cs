using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public sealed record BeforeToolCallContext(
    AssistantMessage AssistantMessage, ToolCall ToolCall, JsonNode? Args, AgentContext Context);

public sealed record BeforeToolCallResult(bool? Block = null, string? Reason = null, bool? Terminate = null);

public sealed record AfterToolCallContext(
    AssistantMessage AssistantMessage, ToolCall ToolCall, JsonNode? Args,
    AgentToolResult Result, bool IsError, AgentContext Context);

public sealed record AfterToolCallResult(
    IReadOnlyList<ContentBlock>? Content = null, JsonNode? Details = null, JsonNode? StructuredContent = null,
    bool? IsError = null, Usage? Usage = null, bool? Terminate = null);

public delegate Task<BeforeToolCallResult?> BeforeToolCall(BeforeToolCallContext context, CancellationToken signal);
public delegate Task<AfterToolCallResult?> AfterToolCall(AfterToolCallContext context, CancellationToken signal);
