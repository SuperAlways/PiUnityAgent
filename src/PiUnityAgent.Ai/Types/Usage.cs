namespace PiUnityAgent.Ai.Types;

/// <summary>Token and cost accounting for one model response.</summary>
public sealed record Usage
{
    public int Input { get; init; }

    public int Output { get; init; }

    public int CacheRead { get; init; }

    public int CacheWrite { get; init; }

    /// <summary>Subset of CacheWrite with 1h retention. Only Anthropic reports this split.</summary>
    public int? CacheWrite1h { get; init; }

    /// <summary>Reasoning tokens, a subset of Output, when the provider reports them.</summary>
    public int? Reasoning { get; init; }

    public int TotalTokens { get; init; }

    public UsageCost Cost { get; init; } = new();
}

/// <summary>Cost breakdown. Values are provider-billed or catalog-estimated depending on TotalFromProvider.</summary>
public sealed record UsageCost
{
    public double Input { get; init; }

    public double Output { get; init; }

    public double CacheRead { get; init; }

    public double CacheWrite { get; init; }

    public double Total { get; init; }

    /// <summary>True when Total is provider-billed; false/absent means catalog estimate.</summary>
    public bool? TotalFromProvider { get; init; }
}
