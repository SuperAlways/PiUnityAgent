using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers.Auth;

public sealed record ModelsStoreEntry
{
    public required IReadOnlyList<ProviderModel> Models { get; init; }
    public long? LastModified { get; init; }
    public long? CheckedAt { get; init; }
    public string? Etag { get; init; }
}

public sealed record ModelsStoreOperationOptions
{
    public CancellationToken CancellationToken { get; init; }
}

public interface IModelsStore
{
    Task<ModelsStoreEntry?> ReadAsync(string providerId, ModelsStoreOperationOptions? options = null);
    Task WriteAsync(string providerId, ModelsStoreEntry entry, ModelsStoreOperationOptions? options = null);
    Task DeleteAsync(string providerId, ModelsStoreOperationOptions? options = null);
}

public sealed class InMemoryModelsStore : IModelsStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelsStoreEntry> _entries = new(StringComparer.Ordinal);

    public Task<ModelsStoreEntry?> ReadAsync(string providerId, ModelsStoreOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        lock (_gate)
        {
            return Task.FromResult(_entries.TryGetValue(providerId, out var entry) ? entry : null);
        }
    }

    public Task WriteAsync(string providerId, ModelsStoreEntry entry, ModelsStoreOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        lock (_gate) _entries[providerId] = entry;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string providerId, ModelsStoreOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        lock (_gate) _entries.Remove(providerId);
        return Task.CompletedTask;
    }

    private static void ThrowIfCancelled(ModelsStoreOperationOptions? options)
        => options?.CancellationToken.ThrowIfCancellationRequested();
}

public sealed record ModelsPublication
{
    public ModelsStoreEntry? Persist { get; init; }
    public bool Delete { get; init; }
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
