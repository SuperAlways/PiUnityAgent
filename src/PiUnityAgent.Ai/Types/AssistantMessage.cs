namespace PiUnityAgent.Ai.Types;

public sealed record AssistantMessage : Message
{
    public override string Role => "assistant";

    public required IReadOnlyList<ContentBlock> Content { get; init; }
    public required string Api { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required Usage Usage { get; init; }
    public required StopReason StopReason { get; init; }

    public string? ResponseModel { get; init; }
    public string? ResponseId { get; init; }
    public string? ProviderThinkingLevel { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
    public IReadOnlyList<AssistantMessageDiagnostic>? Diagnostics { get; init; }
    public DeferredHandle? Deferred { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawStopReason { get; init; }
    public bool? EndTurn { get; init; }
    public int? DurationMs { get; init; }
}
