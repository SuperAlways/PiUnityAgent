using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiUnityAgent.Ai.Types;

public enum ModelInputModality
{
    Text,
    Image
}

public enum ModelOutputModality
{
    Text,
    Image
}

[JsonConverter(typeof(ModelTypeJsonConverter))]
public enum ModelType
{
    Chat,
    Image,
    Classifier
}

/// <summary>所有 catalog entry 的共同字段；Type 决定它能用于哪个 Models 操作。</summary>
public abstract record ProviderModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Api { get; init; }
    public required string Provider { get; init; }
    public required string BaseUrl { get; init; }

    public IReadOnlyList<ModelInputModality> Input { get; init; } =
        new[] { ModelInputModality.Text };

    public ModelInputLimits? InputLimits { get; init; }
    public ModelCost Cost { get; init; } = new();
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public abstract ModelType Type { get; }
}

/// <summary>Chat 模型：可用于 stream() 与相关入口。</summary>
public sealed record Model : ProviderModel
{
    public override ModelType Type => ModelType.Chat;

    public bool Reasoning { get; init; }
    public int ContextWindow { get; init; } = 128000;
    public int MaxTokens { get; init; } = 16384;
    public IReadOnlyDictionary<ModelThinkingLevel, string?>? ThinkingLevelMap { get; init; }
    public JsonObject? SamplingParams { get; init; }
    public IReadOnlyDictionary<ModelThinkingLevel, JsonObject>? SamplingParamsByThinkingLevel { get; init; }
    public JsonObject? Compat { get; init; }
    public ModelPromptCache? PromptCache { get; init; }
}

/// <summary>图片生成模型：只能用于 generateImages()。</summary>
public sealed record ImageModel : ProviderModel
{
    public override ModelType Type => ModelType.Image;

    public IReadOnlyList<ModelOutputModality> Output { get; init; } =
        new[] { ModelOutputModality.Image };
}

/// <summary>结构化分类模型：只能用于 classify()。</summary>
public sealed record ClassifierModel : ProviderModel
{
    public override ModelType Type => ModelType.Classifier;

    public int ContextWindow { get; init; }
    public JsonObject? Compat { get; init; }
}

public sealed record ModelCost(
    double Input = 0,
    double Output = 0,
    double CacheRead = 0,
    double CacheWrite = 0)
{
    public IReadOnlyList<ModelCostTier>? Tiers { get; init; }
}

public sealed record ModelCostTier(
    int InputTokensAbove,
    double Input,
    double Output,
    double CacheRead,
    double CacheWrite);

public sealed record ModelInputLimits
{
    public int? MaxRequestBytes { get; init; }
    public ModelImageInputLimits? Images { get; init; }
}

public sealed record ModelImageInputLimits
{
    public ModelImageResizeOptions? Resize { get; init; }
    public int? MaxPerMessage { get; init; }
    public int? MaxPerRequest { get; init; }
}

public sealed record ModelImageResizeOptions
{
    public int? MaxWidth { get; init; }
    public int? MaxHeight { get; init; }
    public int? MaxBytes { get; init; }
    public int? JpegQuality { get; init; }
}

public sealed record ModelPromptCache
{
    public double? Short { get; init; }
    public double? Long { get; init; }
}

public sealed class ModelTypeJsonConverter : JsonConverter<ModelType>
{
    public override ModelType Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "chat" => ModelType.Chat,
            "image" => ModelType.Image,
            "classifier" => ModelType.Classifier,
            var value => throw new JsonException($"Unknown model type: {value}")
        };

    public override void Write(
        Utf8JsonWriter writer,
        ModelType value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            ModelType.Chat => "chat",
            ModelType.Image => "image",
            ModelType.Classifier => "classifier",
            _ => throw new JsonException($"Unknown model type: {value}")
        });
}
