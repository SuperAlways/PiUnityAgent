using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiUnityAgent.Ai.Types;

/// <summary>Status of a nested tool call made by another tool (for example codemode).</summary>
[JsonConverter(typeof(NestedToolCallStatusJsonConverter))]
public enum NestedToolCallStatus
{
    Ok,
    Error,
    Unfinished,
}

/// <summary>Wire-name conversion for <see cref="NestedToolCallStatus"/>.</summary>
public sealed class NestedToolCallStatusJsonConverter : JsonConverter<NestedToolCallStatus>
{
    public override NestedToolCallStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "ok" => NestedToolCallStatus.Ok,
            "error" => NestedToolCallStatus.Error,
            "unfinished" => NestedToolCallStatus.Unfinished,
            var wire => throw new JsonException($"Unknown nested tool call status wire value: {wire}"),
        };

    public override void Write(Utf8JsonWriter writer, NestedToolCallStatus value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            NestedToolCallStatus.Ok => "ok",
            NestedToolCallStatus.Error => "error",
            NestedToolCallStatus.Unfinished => "unfinished",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown nested tool call status."),
        });
}

/// <summary>One nested tool call record. Results are not recorded.</summary>
public sealed record NestedToolCallRecord
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Omitted when over the size limits; ArgumentsBytes then gives their size.</summary>
    public JsonObject? Arguments { get; init; }

    /// <summary>UTF-8 size of the arguments as JSON, set when Arguments is omitted.</summary>
    public int? ArgumentsBytes { get; init; }

    public NestedToolCallStatus Status { get; init; }

    public int? DurationMs { get; init; }

    public string? Error { get; init; }
}

/// <summary>Bounded record of nested calls made by another tool.</summary>
public sealed record NestedToolCalls
{
    public IReadOnlyList<NestedToolCallRecord> Calls { get; init; } =
        Array.Empty<NestedToolCallRecord>();

    /// <summary>False when calls were dropped, arguments omitted, or calls had not finished.</summary>
    public bool Complete { get; init; }
}
