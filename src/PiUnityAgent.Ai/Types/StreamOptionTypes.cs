using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace PiUnityAgent.Ai.Types;

public sealed record ThinkingBudgets
{
    public int? Minimal { get; init; }
    public int? Low { get; init; }
    public int? Medium { get; init; }
    public int? High { get; init; }
}

[JsonConverter(typeof(CacheRetentionJsonConverter))]
public enum CacheRetention
{
    None,
    Short,
    Long
}

[JsonConverter(typeof(ToolChoiceJsonConverter))]
public enum ToolChoice
{
    Auto,
    None
}

public abstract record DeferredRequest
{
    public sealed record Disabled : DeferredRequest
    {
    }

    public sealed record Enabled : DeferredRequest
    {
        /// <summary>延迟窗口；null / "15m" / "1h" / "24h"（pi TS 类型约束，C# 构造时校验）。</summary>
        public string? Window { get; init; }

        public Enabled(string? window = null)
        {
            if (window is not null && window is not ("15m" or "1h" or "24h"))
            {
                throw new ArgumentException(
                    $"Unknown deferred window: {window}. Expected 15m, 1h, or 24h.");
            }
            Window = window;
        }
    }
}

public sealed record DeferredFetchOptions : StreamOptions
{
    public int? Wait { get; init; }
}

public sealed record DeferredCancelOptions : StreamOptions
{
}

public sealed record ProviderResponse(
    int Status,
    IReadOnlyDictionary<string, string> Headers);

public sealed record TelemetrySpan
{
    public required string Name { get; init; }
    public TelemetrySpan? Parent { get; init; }
}

public sealed class CacheRetentionJsonConverter : JsonConverter<CacheRetention>
{
    public override CacheRetention Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "none" => CacheRetention.None,
            "short" => CacheRetention.Short,
            "long" => CacheRetention.Long,
            var value => throw new JsonException($"Unknown cache retention: {value}")
        };

    public override void Write(
        Utf8JsonWriter writer,
        CacheRetention value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            CacheRetention.None => "none",
            CacheRetention.Short => "short",
            CacheRetention.Long => "long",
            _ => throw new JsonException($"Unknown cache retention: {value}")
        });
}

public sealed class ToolChoiceJsonConverter : JsonConverter<ToolChoice>
{
    public override ToolChoice Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "auto" => ToolChoice.Auto,
            "none" => ToolChoice.None,
            var value => throw new JsonException($"Unknown tool choice: {value}")
        };

    public override void Write(
        Utf8JsonWriter writer,
        ToolChoice value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            ToolChoice.Auto => "auto",
            ToolChoice.None => "none",
            _ => throw new JsonException($"Unknown tool choice: {value}")
        });
}
