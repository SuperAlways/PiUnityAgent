using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiUnityAgent.Ai.Types;

/// <summary>Why a model response ended. Wire values match pi.</summary>
[JsonConverter(typeof(StopReasonJsonConverter))]
public enum StopReason
{
    Pending,
    Stop,
    Length,
    ToolUse,
    Error,
    Aborted,
    Deferred,
}

/// <summary>
/// Single source of truth for the <see cref="StopReason"/> wire mapping.
/// Both the JSON converter and <see cref="StopReasonExtensions"/> delegate here.
/// </summary>
internal static class StopReasonWire
{
    public static string ToWire(StopReason reason) => reason switch
    {
        StopReason.Pending => "pending",
        StopReason.Stop => "stop",
        StopReason.Length => "length",
        StopReason.ToolUse => "toolUse",
        StopReason.Error => "error",
        StopReason.Aborted => "aborted",
        StopReason.Deferred => "deferred",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown stop reason."),
    };

    public static StopReason FromWire(string? wire) => wire switch
    {
        "pending" => StopReason.Pending,
        "stop" => StopReason.Stop,
        "length" => StopReason.Length,
        "toolUse" => StopReason.ToolUse,
        "error" => StopReason.Error,
        "aborted" => StopReason.Aborted,
        "deferred" => StopReason.Deferred,
        _ => throw new ArgumentOutOfRangeException(nameof(wire), wire, "Unknown stop reason wire value."),
    };
}

/// <summary>Wire-name conversion for <see cref="StopReason"/>.</summary>
public static class StopReasonExtensions
{
    public static string ToWire(this StopReason reason) => StopReasonWire.ToWire(reason);

    public static StopReason FromWire(string wire) => StopReasonWire.FromWire(wire);
}

public sealed class StopReasonJsonConverter : JsonConverter<StopReason>
{
    public override StopReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return StopReasonWire.FromWire(reader.GetString());
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new JsonException(e.Message, e);
        }
    }

    public override void Write(Utf8JsonWriter writer, StopReason value, JsonSerializerOptions options)
        => writer.WriteStringValue(StopReasonWire.ToWire(value));
}
