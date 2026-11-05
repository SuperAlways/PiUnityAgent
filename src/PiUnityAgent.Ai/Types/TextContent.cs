using System.Text.Json.Serialization;

namespace PiUnityAgent.Ai.Types;

/// <summary>A text block in a message.</summary>
public sealed record TextContent : ContentBlock, IImagesInputContent, IImagesOutputContent
{
    [JsonIgnore]
    public override string Type => "text";

    public string Text { get; init; } = string.Empty;

    /// <summary>Provider-specific metadata for message replay, when present.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextSignature { get; init; }
}
