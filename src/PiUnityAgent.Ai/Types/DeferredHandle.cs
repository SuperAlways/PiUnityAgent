using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Types;

/// <summary>Handle for a provider response that is fetched later (deferred).</summary>
public sealed record DeferredHandle
{
    public string Provider { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string Api { get; init; } = string.Empty;

    /// <summary>Provider token, such as a response id or batch id plus row id.</summary>
    public string Id { get; init; } = string.Empty;

    public long? ExpiresAt { get; init; }

    public int? PollAfterMs { get; init; }

    /// <summary>Provider conversion data required to reconstruct the final assistant message.</summary>
    public JsonNode? Data { get; init; }
}
