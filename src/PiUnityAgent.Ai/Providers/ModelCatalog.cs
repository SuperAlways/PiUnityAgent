using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>
/// 嵌入式模型目录：读取 <c>PiUnityAgent.Ai.Providers.ModelData.{providerId}.json</c>，
/// 只枚举 <c>chat:</c> 条目并映射为 <see cref="Model"/>。
/// 数据源自 pi.net ModelData；解析语义对齐 pi.net Models/ModelCatalog.cs（未知字段忽略、缺资源不抛）。
/// </summary>
public static class ModelCatalog
{
    private const string ResourcePrefix = "PiUnityAgent.Ai.Providers.ModelData.";
    private const string ChatKeyPrefix = "chat:";

    /// <summary>
    /// providerId 对应嵌入资源的全部 chat 模型；资源缺失（未注册的 provider）返回空列表，不抛。
    /// </summary>
    public static IReadOnlyList<Model> GetChatModels(string providerId)
    {
        if (string.IsNullOrEmpty(providerId)) return Array.Empty<Model>();

        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(
            ResourcePrefix + providerId + ".json");
        if (stream is null) return Array.Empty<Model>();

        var root = JsonNode.Parse(stream) as JsonObject;
        if (root is null) return Array.Empty<Model>();

        var result = new List<Model>();
        foreach (var (_, groupNode) in root)
        {
            // 顶层按 api 分组（openai-completions / openai-responses / ...）。
            if (groupNode is not JsonObject group) continue;
            foreach (var (key, entryNode) in group)
            {
                // pi 约定：组内键形如 "type:id"，只取 chat 条目。
                if (key is null || !key.StartsWith(ChatKeyPrefix, StringComparison.Ordinal)) continue;
                if (entryNode is not JsonObject spec) continue;

                var model = ParseChatModel(providerId, spec);
                if (model is not null) result.Add(model);
            }
        }
        return result;
    }

    private static Model? ParseChatModel(string catalogProviderId, JsonObject spec)
    {
        // TODO(后续扩展, D19): 目录 promptCache 字段暂不解析（pi Model.promptCache）。
        string? GetString(string key)
            => spec[key] is JsonValue { } value && value.TryGetValue<string>(out var text) ? text : null;

        bool GetBool(string key)
            => spec[key] is JsonValue { } value && value.TryGetValue<bool>(out var flag) && flag;

        int GetInt(string key)
            => spec[key] is JsonValue { } value && value.TryGetValue<int>(out var parsed) ? parsed : 0;

        // pi 约定 type 缺省即 chat；显式非 chat 的条目（image/classifier）不进 chat 目录。
        var type = GetString("type") ?? "chat";
        if (type != "chat") return null;

        var id = GetString("id");
        if (string.IsNullOrEmpty(id)) return null;

        // pi 约定：Provider 以 JSON 内的 provider 字段为准（catalog key 仅兜底）。
        var provider = GetString("provider") ?? catalogProviderId;

        var model = new Model
        {
            Id = id!,
            Name = GetString("name") ?? id,
            Api = GetString("api") ?? string.Empty,
            Provider = provider,
            BaseUrl = GetString("baseUrl") ?? string.Empty,
            Reasoning = GetBool("reasoning"),
            ContextWindow = GetInt("contextWindow"),
            MaxTokens = GetInt("maxTokens"),
            Compat = spec["compat"] as JsonObject,
            SamplingParams = spec["samplingParams"] as JsonObject,
        };

        if (spec["input"] is JsonArray inputArray)
        {
            var input = ParseInput(inputArray);
            if (input.Count > 0) model = model with { Input = input };
        }

        if (spec["cost"] is JsonObject costObject) model = model with { Cost = ParseCost(costObject) };

        if (spec["headers"] is JsonObject headersObject)
        {
            var headers = new Dictionary<string, string>();
            foreach (var (key, value) in headersObject)
            {
                if (key is not null && value is JsonValue { } headerValue
                    && headerValue.TryGetValue<string>(out var headerText))
                {
                    headers[key] = headerText;
                }
            }
            if (headers.Count > 0) model = model with { Headers = headers };
        }

        if (spec["thinkingLevelMap"] is JsonObject levelMapObject)
        {
            model = model with { ThinkingLevelMap = ParseThinkingLevelMap(levelMapObject) };
        }

        if (spec["samplingParamsByThinkingLevel"] is JsonObject byLevelObject)
        {
            var byLevel = new Dictionary<ModelThinkingLevel, JsonObject>();
            foreach (var (key, value) in byLevelObject)
            {
                if (key is not null && value is JsonObject levelParams
                    && TryParseThinkingLevel(key, out var level))
                {
                    byLevel[level] = levelParams;
                }
            }
            if (byLevel.Count > 0) model = model with { SamplingParamsByThinkingLevel = byLevel };
        }

        if (spec["inputLimits"] is JsonObject limitsObject)
        {
            model = model with { InputLimits = ParseInputLimits(limitsObject) };
        }

        return model;
    }

    private static IReadOnlyList<ModelInputModality> ParseInput(JsonArray inputArray)
    {
        var input = new List<ModelInputModality>();
        foreach (var node in inputArray)
        {
            if (node is not JsonValue { } value || !value.TryGetValue<string>(out var text)) continue;
            switch (text)
            {
                case "text":
                    input.Add(ModelInputModality.Text);
                    break;
                case "image":
                    input.Add(ModelInputModality.Image);
                    break;
                default:
                    // 未知模态忽略（前向兼容）。
                    break;
            }
        }
        return input;
    }

    private static ModelCost ParseCost(JsonObject cost)
    {
        static double Number(JsonObject owner, string key)
            => owner[key] is JsonValue { } value && value.TryGetValue<double>(out var parsed) ? parsed : 0;

        var parsedCost = new ModelCost(
            Number(cost, "input"),
            Number(cost, "output"),
            Number(cost, "cacheRead"),
            Number(cost, "cacheWrite"));

        var tiers = new List<ModelCostTier>();
        if (cost["tiers"] is JsonArray tierArray)
        {
            foreach (var node in tierArray)
            {
                if (node is not JsonObject tier) continue;
                tiers.Add(new ModelCostTier(
                    GetInt(tier, "inputTokensAbove"),
                    Number(tier, "input"),
                    Number(tier, "output"),
                    Number(tier, "cacheRead"),
                    Number(tier, "cacheWrite")));
            }
        }
        if (tiers.Count > 0) parsedCost = parsedCost with { Tiers = tiers };
        return parsedCost;
    }

    private static IReadOnlyDictionary<ModelThinkingLevel, string?> ParseThinkingLevelMap(JsonObject map)
    {
        // pi 语义：null（或非字符串值）= 该档位禁用；未知档位键忽略。
        var result = new Dictionary<ModelThinkingLevel, string?>();
        foreach (var (key, value) in map)
        {
            if (key is null || !TryParseThinkingLevel(key, out var level)) continue;
            result[level] = value is JsonValue { } primitive
                && primitive.TryGetValue<string>(out var text) ? text : null;
        }
        return result;
    }

    private static bool TryParseThinkingLevel(string key, out ModelThinkingLevel level)
    {
        switch (key)
        {
            case "off": level = ModelThinkingLevel.Off; return true;
            case "minimal": level = ModelThinkingLevel.Minimal; return true;
            case "low": level = ModelThinkingLevel.Low; return true;
            case "medium": level = ModelThinkingLevel.Medium; return true;
            case "high": level = ModelThinkingLevel.High; return true;
            case "xhigh": level = ModelThinkingLevel.XHigh; return true;
            case "max": level = ModelThinkingLevel.Max; return true;
            default: level = default; return false;
        }
    }

    private static ModelInputLimits ParseInputLimits(JsonObject limits)
    {
        ModelImageInputLimits? images = null;
        if (limits["images"] is JsonObject imagesObject)
        {
            ModelImageResizeOptions? resize = null;
            if (imagesObject["resize"] is JsonObject resizeObject)
            {
                resize = new ModelImageResizeOptions
                {
                    MaxWidth = GetOptionalInt(resizeObject, "maxWidth"),
                    MaxHeight = GetOptionalInt(resizeObject, "maxHeight"),
                    MaxBytes = GetOptionalInt(resizeObject, "maxBytes"),
                    JpegQuality = GetOptionalInt(resizeObject, "jpegQuality"),
                };
            }

            images = new ModelImageInputLimits
            {
                Resize = resize,
                MaxPerMessage = GetOptionalInt(imagesObject, "maxPerMessage"),
                MaxPerRequest = GetOptionalInt(imagesObject, "maxPerRequest"),
            };
        }

        return new ModelInputLimits
        {
            MaxRequestBytes = GetOptionalInt(limits, "maxRequestBytes"),
            Images = images,
        };
    }

    private static int GetInt(JsonObject owner, string key)
        => owner[key] is JsonValue { } value && value.TryGetValue<int>(out var parsed) ? parsed : 0;

    private static int? GetOptionalInt(JsonObject owner, string key)
        => owner[key] is JsonValue { } value && value.TryGetValue<int>(out var parsed) ? parsed : null;
}
