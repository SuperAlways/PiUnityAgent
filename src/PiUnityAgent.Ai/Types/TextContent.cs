namespace PiUnityAgent.Ai.Types;

/// <summary>A text block in a message.</summary>
public sealed record TextContent : ContentBlock, IImagesInputContent, IImagesOutputContent
{
    public override string Type => "text";

    public string Text { get; init; } = string.Empty;

    /// <summary>Provider-specific metadata for message replay, when present.</summary>
    public string? TextSignature { get; init; }
}
