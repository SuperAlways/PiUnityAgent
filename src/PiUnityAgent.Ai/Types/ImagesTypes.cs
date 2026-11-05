namespace PiUnityAgent.Ai.Types;

/// <summary>Marker implemented by content blocks accepted as image input.</summary>
public interface IImagesInputContent
{
}

/// <summary>Marker implemented by content blocks returned by image generation.</summary>
public interface IImagesOutputContent
{
}

/// <summary>Input for image generation.</summary>
public sealed record ImagesContext
{
    public IReadOnlyList<IImagesInputContent> Input { get; init; } = Array.Empty<IImagesInputContent>();
}

/// <summary>Why an image-generation response ended. Wire values match pi.</summary>
public enum ImagesStopReason
{
    Stop,
    Error,
    Aborted,
}

/// <summary>Final image-generation result.</summary>
public sealed record AssistantImages
{
    public string Api { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public IReadOnlyList<IImagesOutputContent> Output { get; init; } = Array.Empty<IImagesOutputContent>();

    public string? ResponseId { get; init; }

    public Usage? Usage { get; init; }

    public ImagesStopReason StopReason { get; init; }

    public string? ErrorMessage { get; init; }

    public long Timestamp { get; init; }
}
