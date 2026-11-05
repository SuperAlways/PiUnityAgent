using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiUnityAgent.Ai.Types;

[JsonConverter(typeof(ThinkingLevelJsonConverter))]
public enum ThinkingLevel { Minimal, Low, Medium, High, XHigh, Max }

[JsonConverter(typeof(ModelThinkingLevelJsonConverter))]
public enum ModelThinkingLevel { Off, Minimal, Low, Medium, High, XHigh, Max }

public sealed class ThinkingLevelJsonConverter : JsonConverter<ThinkingLevel>
{
    public override ThinkingLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "minimal" => ThinkingLevel.Minimal,
            "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            "xhigh" => ThinkingLevel.XHigh,
            "max" => ThinkingLevel.Max,
            var value => throw new JsonException($"Unknown thinking level: {value}")
        };

    public override void Write(Utf8JsonWriter writer, ThinkingLevel value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            ThinkingLevel.Minimal => "minimal",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.XHigh => "xhigh",
            ThinkingLevel.Max => "max",
            _ => throw new JsonException($"Unknown thinking level: {value}")
        });
}

public sealed class ModelThinkingLevelJsonConverter : JsonConverter<ModelThinkingLevel>
{
    public override ModelThinkingLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "off" => ModelThinkingLevel.Off,
            "minimal" => ModelThinkingLevel.Minimal,
            "low" => ModelThinkingLevel.Low,
            "medium" => ModelThinkingLevel.Medium,
            "high" => ModelThinkingLevel.High,
            "xhigh" => ModelThinkingLevel.XHigh,
            "max" => ModelThinkingLevel.Max,
            var value => throw new JsonException($"Unknown model thinking level: {value}")
        };

    public override void Write(Utf8JsonWriter writer, ModelThinkingLevel value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            ModelThinkingLevel.Off => "off",
            ModelThinkingLevel.Minimal => "minimal",
            ModelThinkingLevel.Low => "low",
            ModelThinkingLevel.Medium => "medium",
            ModelThinkingLevel.High => "high",
            ModelThinkingLevel.XHigh => "xhigh",
            ModelThinkingLevel.Max => "max",
            _ => throw new JsonException($"Unknown model thinking level: {value}")
        });
}
