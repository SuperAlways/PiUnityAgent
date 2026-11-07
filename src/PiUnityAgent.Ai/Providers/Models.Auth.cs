using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed partial class Models
{
    public async Task<AuthResult?> GetAuthAsync(
        string providerId,
        AuthResolutionOverrides? overrides = null)
    {
        var provider = GetProvider(providerId);
        return provider is null
            ? null
            : await ResolveAuthAsync(provider, overrides).ConfigureAwait(false);
    }

    public Task<AuthResult?> GetAuthAsync(
        ProviderModel model,
        AuthResolutionOverrides? overrides = null)
        => GetAuthAsync(model.Provider, overrides);

    public async Task<AuthCheck?> CheckAuthAsync(
        string providerId,
        AuthOperationOptions? options = null)
    {
        var provider = GetProvider(providerId);
        if (provider is null) return null;
        if (provider.Auth is null) return new AuthCheck(true);

        var auth = await GetAuthAsync(providerId).ConfigureAwait(false);
        return auth?.Credential switch
        {
            ApiKeyCredential => new AuthCheck(true, AuthType.ApiKey),
            OAuthCredential => new AuthCheck(true, AuthType.OAuth),
            _ => null,
        };
    }

    public async Task<IReadOnlyList<Model>> GetAvailableAsync(
        string? provider = null,
        AuthOperationOptions? options = null)
    {
        var result = new List<Model>();
        foreach (var entry in SelectProviders(provider))
        {
            if (!await IsAvailableAsync(entry, options).ConfigureAwait(false)) continue;

            var credential = (await GetAuthAsync(entry.Id).ConfigureAwait(false))?.Credential;
            var models = SafeChatModels(entry);
            if (entry is IRefreshableProvider refreshable)
            {
                models = refreshable
                    .FilterModels(models.Cast<ProviderModel>().ToList(), credential)
                    .OfType<Model>()
                    .ToList();
            }

            result.AddRange(models);
        }

        return result;
    }

    public async Task<IReadOnlyList<ProviderModel>> GetAvailableOfTypeAsync(
        ModelType type,
        string? provider = null,
        AuthOperationOptions? options = null)
    {
        var result = new List<ProviderModel>();
        foreach (var entry in SelectProviders(provider))
        {
            if (!await IsAvailableAsync(entry, options).ConfigureAwait(false)) continue;

            var credential = (await GetAuthAsync(entry.Id).ConfigureAwait(false))?.Credential;
            var models = SafeAllModels(entry);
            if (entry is IRefreshableProvider refreshable)
            {
                models = refreshable.FilterModels(models, credential);
            }

            result.AddRange(models.Where(model => model.Type == type));
        }

        return result;
    }

    public async Task<IReadOnlyList<ProviderModel>> GetAllAvailableAsync(
        string? provider = null,
        AuthOperationOptions? options = null)
    {
        var result = new List<ProviderModel>();
        foreach (var entry in SelectProviders(provider))
        {
            if (!await IsAvailableAsync(entry, options).ConfigureAwait(false)) continue;

            var credential = (await GetAuthAsync(entry.Id).ConfigureAwait(false))?.Credential;
            var models = SafeAllModels(entry);
            if (entry is IRefreshableProvider refreshable)
            {
                models = refreshable.FilterModels(models, credential);
            }

            result.AddRange(models);
        }

        return result;
    }

    public Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null)
    {
        // TODO(S4, D2): 动态模型目录刷新。
        return Task.FromResult(new ModelsRefreshResult(
            false,
            new Dictionary<string, Exception>()));
    }

    public Task<Credential> LoginAsync(
        string providerId,
        AuthType type,
        AuthInteraction interaction,
        LoginOptions? options = null)
        => throw new NotSupportedException(
            "Deferred to S5: interactive login (D1)");

    public async Task LogoutAsync(
        string providerId,
        AuthOperationOptions? options = null)
    {
        var provider = GetProvider(providerId)
            ?? throw new InvalidOperationException($"Unknown provider: {providerId}");

        if (provider.Auth?.OAuth?.Logout is not null)
        {
            await provider.Auth.OAuth.Logout(providerId, default).ConfigureAwait(false);
        }

        await _credentials.DeleteAsync(providerId).ConfigureAwait(false);
    }

    private async Task<AuthResult?> ResolveAuthAsync(
        IProvider provider,
        AuthResolutionOverrides? overrides)
    {
        if (overrides?.Credential is not null)
        {
            return new AuthResult(overrides.Credential, "options");
        }

        if (!string.IsNullOrEmpty(overrides?.ApiKey))
        {
            return new AuthResult(new ApiKeyCredential(overrides.ApiKey), "options");
        }

        var stored = await _credentials.GetAsync(provider.Id).ConfigureAwait(false);
        if (stored is ApiKeyCredential apiKey && !string.IsNullOrEmpty(apiKey.Key))
        {
            return new AuthResult(apiKey, "store");
        }

        if (stored is OAuthCredential oauth)
        {
            // TODO(S5, D1): OAuth 刷新/派生请求认证。
            return new AuthResult(oauth, "store");
        }

        var resolve = provider.Auth?.ApiKey?.Resolve;
        if (resolve is not null)
        {
            var key = await resolve(_authContext, null, default).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(key))
            {
                return new AuthResult(new ApiKeyCredential(key), "provider");
            }
        }

        return null;
    }

    private IReadOnlyList<IProvider> SelectProviders(string? providerId)
        => providerId is null
            ? GetProviders()
            : GetProvider(providerId) is { } provider
                ? new[] { provider }
                : Array.Empty<IProvider>();

    private async Task<bool> IsAvailableAsync(
        IProvider provider,
        AuthOperationOptions? options)
    {
        if (provider.Auth is null) return true;
        var check = await CheckAuthAsync(provider.Id, options).ConfigureAwait(false);
        return check?.Configured == true;
    }
}
