namespace PiUnityAgent.Ai.Types;

public sealed record ToolResultMessage : Message
{
    public override string Role => "toolResult";

    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required IReadOnlyList<ContentBlock> Content { get; init; }
    public required bool IsError { get; init; }

    public object? Details { get; init; }
    public Usage? Usage { get; init; }
    public NestedToolCalls? NestedCalls { get; init; }
    public int? DurationMs { get; init; }
}
