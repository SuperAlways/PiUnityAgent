namespace PiUnityAgent.Ai.Types;

/// <summary>A model response. Content may contain text, thinking, and tool calls.</summary>
public sealed record AssistantMessage : Message
{
    public override string Role => "assistant";

    public IReadOnlyList<ContentBlock> Content { get; init; } = Array.Empty<ContentBlock>();

    public string Api { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    /// <summary>Concrete model reported by the provider, when different from Model.</summary>
    public string? ResponseModel { get; init; }

    public string? ResponseId { get; init; }

    /// <summary>Exact provider-native thinking effort used for this response.</summary>
    public string? ProviderThinkingLevel { get; init; }

    /// <summary>pi thinking level requested for this response.</summary>
    public string? ThinkingLevel { get; init; }

    public Usage Usage { get; init; } = new();

    /// <summary>Redacted provider/runtime diagnostics for failures and recoveries.</summary>
    public IReadOnlyList<AssistantMessageDiagnostic>? Diagnostics { get; init; }

    /// <summary>Handle for a deferred response that is fetched later.</summary>
    public DeferredHandle? Deferred { get; init; }

    public StopReason StopReason { get; init; } = StopReason.Pending;

    public string? ErrorMessage { get; init; }

    public string? RawStopReason { get; init; }

    /// <summary>Provider indication that the model explicitly ended its turn.</summary>
    public bool? EndTurn { get; init; }

    /// <summary>Milliseconds from response start to end, when measured.</summary>
    public int? DurationMs { get; init; }
}
