using System;
using System.Text.Json;
using System.Text.Json.Serialization;

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
[JsonConverter(typeof(ImagesStopReasonJsonConverter))]
public enum ImagesStopReason
{
    Stop,
    Error,
    Aborted,
}

/// <summary>Wire-name conversion for <see cref="ImagesStopReason"/>.</summary>
public sealed class ImagesStopReasonJsonConverter : JsonConverter<ImagesStopReason>
{
    public override ImagesStopReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "stop" => ImagesStopReason.Stop,
            "error" => ImagesStopReason.Error,
            "aborted" => ImagesStopReason.Aborted,
            var wire => throw new JsonException($"Unknown images stop reason wire value: {wire}"),
        };

    public override void Write(Utf8JsonWriter writer, ImagesStopReason value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            ImagesStopReason.Stop => "stop",
            ImagesStopReason.Error => "error",
            ImagesStopReason.Aborted => "aborted",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown images stop reason."),
        });
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
