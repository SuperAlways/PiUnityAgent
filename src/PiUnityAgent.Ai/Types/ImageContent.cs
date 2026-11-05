namespace PiUnityAgent.Ai.Types;

/// <summary>A base64-encoded image block.</summary>
public sealed record ImageContent : ContentBlock, IImagesInputContent, IImagesOutputContent
{
    public override string Type => "image";

    public string Data { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;
}
