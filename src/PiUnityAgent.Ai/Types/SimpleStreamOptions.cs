using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Types;

public sealed record SimpleStreamOptions
{
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public int? MaxTokens { get; init; }
    public double? Temperature { get; init; }
    public IReadOnlyList<string>? StopSequences { get; init; }
    public string? SessionId { get; init; }
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    public JsonObject? SamplingParams { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
    public JsonNode? ToolChoice { get; init; }
    public int? TimeoutMs { get; init; }
    public int? MaxRetries { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
