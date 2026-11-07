using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Providers.Auth;

public abstract record Credential;

public sealed record ApiKeyCredential(string Key) : Credential;

public sealed record OAuthCredential : Credential
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public long? ExpiresAt { get; init; }
}

public sealed record CredentialInfo(AuthType Type, string? Source = null);

public interface CredentialStore
{
    Task<Credential?> GetAsync(string providerId, CancellationToken ct = default);
    Task SetAsync(string providerId, Credential credential, CancellationToken ct = default);
    Task DeleteAsync(string providerId, CancellationToken ct = default);
}

public sealed class InMemoryCredentialStore : CredentialStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Credential> _credentials = new(StringComparer.Ordinal);

    public Task<Credential?> GetAsync(string providerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<Credential?>(
                _credentials.TryGetValue(providerId, out var credential) ? credential : null);
        }
    }

    public Task SetAsync(string providerId, Credential credential, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (credential is null) throw new ArgumentNullException(nameof(credential));
        lock (_gate) _credentials[providerId] = credential;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string providerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) _credentials.Remove(providerId);
        return Task.CompletedTask;
    }
}
