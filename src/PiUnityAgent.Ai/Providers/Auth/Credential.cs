using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Providers.Auth;

public abstract record Credential;

public sealed record ApiKeyCredential(string? Key = null) : Credential
{
    public IReadOnlyDictionary<string, string>? Env { get; init; }
}

public sealed record OAuthCredential : Credential
{
    public required string RefreshToken { get; init; }
    public required string AccessToken { get; init; }
    public required long ExpiresAt { get; init; }

    /// <summary>pi OAuthCredentials 的 `[key: string]: unknown` 额外字段（内存表示）；落盘时与 pi 一样平铺在凭据条目顶层。</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Extra { get; init; }
}

public sealed record CredentialInfo(string ProviderId, AuthType Type);

public interface CredentialStore
{
    Task<Credential?> ReadAsync(string providerId, AuthOperationOptions? options = null);
    Task<IReadOnlyList<CredentialInfo>> ListAsync(AuthOperationOptions? options = null);
    Task<Credential?> ModifyAsync(
        string providerId,
        Func<Credential?, Task<Credential?>> modify,
        AuthOperationOptions? options = null);
    Task DeleteAsync(string providerId, AuthOperationOptions? options = null);
}

public sealed class InMemoryCredentialStore : CredentialStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Credential> _credentials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    private SemaphoreSlim LockFor(string providerId)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(providerId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _locks[providerId] = gate;
            }
            return gate;
        }
    }

    public Task<Credential?> ReadAsync(string providerId, AuthOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        lock (_gate)
        {
            return Task.FromResult<Credential?>(
                _credentials.TryGetValue(providerId, out var credential) ? credential : null);
        }
    }

    public Task<IReadOnlyList<CredentialInfo>> ListAsync(AuthOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<CredentialInfo>>(
                _credentials.Select(entry => new CredentialInfo(
                    entry.Key,
                    entry.Value is OAuthCredential ? AuthType.OAuth : AuthType.ApiKey)).ToList());
        }
    }

    public async Task<Credential?> ModifyAsync(
        string providerId,
        Func<Credential?, Task<Credential?>> modify,
        AuthOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        if (modify is null) throw new ArgumentNullException(nameof(modify));

        var gate = LockFor(providerId);
        await gate.WaitAsync(options?.CancellationToken ?? default).ConfigureAwait(false);
        try
        {
            ThrowIfCancelled(options);
            Credential? current;
            lock (_gate) _credentials.TryGetValue(providerId, out current);

            var next = await modify(current).ConfigureAwait(false);
            ThrowIfCancelled(options);
            if (next is not null)
            {
                lock (_gate) _credentials[providerId] = next;
                return next;
            }

            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string providerId, AuthOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        var gate = LockFor(providerId);
        await gate.WaitAsync(options?.CancellationToken ?? default).ConfigureAwait(false);
        try
        {
            ThrowIfCancelled(options);
            lock (_gate) _credentials.Remove(providerId);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ThrowIfCancelled(AuthOperationOptions? options)
        => options?.CancellationToken.ThrowIfCancellationRequested();
}
