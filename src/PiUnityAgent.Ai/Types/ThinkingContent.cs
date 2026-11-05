namespace PiUnityAgent.Ai.Types;

/// <summary>A reasoning/thinking block. Not all providers emit these.</summary>
public sealed record ThinkingContent : ContentBlock
{
    public override string Type => "thinking";

    public string Thinking { get; init; } = string.Empty;

    /// <summary>Provider-specific opaque or serialized reasoning replay data.</summary>
    public string? ThinkingSignature { get; init; }

    /// <summary>True when the content was redacted by a safety filter; the payload stays in ThinkingSignature.</summary>
    public bool? Redacted { get; init; }
}
