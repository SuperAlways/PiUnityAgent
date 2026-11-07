using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Providers.Auth;

public sealed record ModelsStoreEntry
{
    public required string Provider { get; init; }
    public JsonObject? Data { get; init; }
    public long? UpdatedAt { get; init; }
}

public interface ModelsStore
{
    Task<ModelsStoreEntry?> GetAsync(string providerId, CancellationToken ct = default);
    Task SetAsync(string providerId, ModelsStoreEntry? entry, CancellationToken ct = default);
}

public sealed record ModelsPublication
{
    public ModelsStoreEntry? Persist { get; init; }
    public Action? Update { get; init; }
}

public sealed record RefreshModelsContext
{
    public Credential? Credential { get; init; }
    public ModelsStoreEntry? Stored { get; init; }
    public string? BaseUrl { get; init; }
    public required Func<ModelsPublication, Task<bool>> Publish { get; init; }
    public bool AllowNetwork { get; init; }
    public bool Force { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record ModelsRefreshOptions
{
    public bool? AllowNetwork { get; init; }
    public IReadOnlyList<string>? Providers { get; init; }
    public bool? Force { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record ModelsRefreshResult(
    bool Aborted,
    IReadOnlyDictionary<string, Exception> Errors);
