using System;
using System.Collections.Generic;
using System.Linq;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public static class ModelOperations
{
    private static readonly ModelThinkingLevel[] ExtendedThinkingLevels =
    {
        ModelThinkingLevel.Off,
        ModelThinkingLevel.Minimal,
        ModelThinkingLevel.Low,
        ModelThinkingLevel.Medium,
        ModelThinkingLevel.High,
        ModelThinkingLevel.XHigh,
        ModelThinkingLevel.Max,
    };

    public static ModelType GetModelType(ProviderModel model) => model.Type;

    public static bool IsModelType(ProviderModel model, ModelType type) => model.Type == type;

    public static bool HasApi(Model model, string api)
        => model.Type == ModelType.Chat && model.Api == api;

    public static bool ModelsAreEqual(ProviderModel? a, ProviderModel? b)
        => a is not null && b is not null
           && a.Type == b.Type && a.Id == b.Id && a.Provider == b.Provider;

    public static UsageCost CalculateCost(ProviderModel model, Usage usage)
    {
        var inputTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        var rates = model.Cost;
        var matchedThreshold = -1;
        foreach (var tier in model.Cost.Tiers ?? Array.Empty<ModelCostTier>())
        {
            if (inputTokens > tier.InputTokensAbove && tier.InputTokensAbove > matchedThreshold)
            {
                rates = new ModelCost(tier.Input, tier.Output, tier.CacheRead, tier.CacheWrite);
                matchedThreshold = tier.InputTokensAbove;
            }
        }

        var longWrite = usage.CacheWrite1h ?? 0;
        var shortWrite = usage.CacheWrite - longWrite;
        var input = rates.Input / 1_000_000 * usage.Input;
        var output = rates.Output / 1_000_000 * usage.Output;
        var cacheRead = rates.CacheRead / 1_000_000 * usage.CacheRead;
        var cacheWrite = (rates.CacheWrite * shortWrite + rates.Input * 2 * longWrite) / 1_000_000;
        return new UsageCost
        {
            Input = input,
            Output = output,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            Total = input + output + cacheRead + cacheWrite,
            TotalFromProvider = false,
        };
    }

    public static IReadOnlyList<ModelThinkingLevel> GetSupportedThinkingLevels(Model model)
    {
        if (!model.Reasoning) return new[] { ModelThinkingLevel.Off };

        return ExtendedThinkingLevels
            .Where(level =>
            {
                var hasKey = model.ThinkingLevelMap?.ContainsKey(level) == true;
                var mapped = hasKey ? model.ThinkingLevelMap![level] : null;
                if (hasKey && mapped is null) return false;
                if (level is ModelThinkingLevel.XHigh or ModelThinkingLevel.Max)
                {
                    return hasKey && mapped is not null;
                }
                return true;
            })
            .ToList();
    }

    public static ModelThinkingLevel ClampThinkingLevel(Model model, ModelThinkingLevel level)
    {
        var available = GetSupportedThinkingLevels(model);
        if (available.Contains(level)) return level;

        var requestedIndex = Array.IndexOf(ExtendedThinkingLevels, level);
        if (requestedIndex < 0) return available[0];

        for (var i = requestedIndex; i < ExtendedThinkingLevels.Length; i++)
        {
            if (available.Contains(ExtendedThinkingLevels[i])) return ExtendedThinkingLevels[i];
        }
        for (var i = requestedIndex - 1; i >= 0; i--)
        {
            if (available.Contains(ExtendedThinkingLevels[i])) return ExtendedThinkingLevels[i];
        }
        return available[0];
    }
}
