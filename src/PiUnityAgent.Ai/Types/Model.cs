namespace PiUnityAgent.Ai.Types;

public enum ModelInputModality { Text, Image }

public sealed record ModelCost(
    double Input = 0,
    double Output = 0,
    double CacheRead = 0,
    double CacheWrite = 0);

public sealed record Model
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Api { get; init; }
    public required string Provider { get; init; }
    public required string BaseUrl { get; init; }

    public IReadOnlyList<ModelInputModality> Input { get; init; } =
        new[] { ModelInputModality.Text };

    public ModelCost Cost { get; init; } = new();
    public bool Reasoning { get; init; }
    public int ContextWindow { get; init; } = 128000;
    public int MaxTokens { get; init; } = 16384;
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    public IReadOnlyDictionary<ModelThinkingLevel, string?>? ThinkingLevelMap { get; init; }
}
