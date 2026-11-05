using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Types;

/// <summary>The result of executing one tool call.</summary>
public sealed record ToolResultMessage : Message
{
    public override string Role => "toolResult";

    /// <summary>Id of the ToolCall this result answers.</summary>
    public string ToolCallId { get; init; } = string.Empty;

    public string ToolName { get; init; } = string.Empty;

    /// <summary>Model-facing text and/or image content.</summary>
    public IReadOnlyList<ContentBlock> Content { get; init; } = Array.Empty<ContentBlock>();

    /// <summary>Non-model-facing structured details for UI/logs.</summary>
    public JsonNode? Details { get; init; }

    /// <summary>Calls this tool made to other tools. Kept for the session record; not sent to the model.</summary>
    public NestedToolCalls? NestedCalls { get; init; }

    /// <summary>Usage from the tool execution itself, when available.</summary>
    public Usage? Usage { get; init; }

    public bool IsError { get; init; }

    public int? DurationMs { get; init; }
}
