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

    public async Task<AuthResult?> GetAuthAsync(
        ProviderModel model,
        AuthResolutionOverrides? overrides = null)
    {
        var result = await GetAuthAsync(model.Provider, overrides).ConfigureAwait(false);
        if (result is null || model.Headers is null || model.Headers.Count == 0) return result;

        // ProviderModel.Headers 是 string；ModelAuth.Headers 允许 null 抑制默认头，显式协变。
        var modelHeaders = model.Headers.ToDictionary(
            pair => pair.Key,
            pair => (string?)pair.Value,
            StringComparer.Ordinal);
        return result with
        {
            Auth = result.Auth with
            {
                Headers = MergeHeaders(result.Auth.Headers, modelHeaders),
            },
        };
    }

    public async Task<AuthCheck?> CheckAuthAsync(
        string providerId,
        AuthOperationOptions? options = null)
    {
        var ct = options?.CancellationToken ?? default;
        ct.ThrowIfCancellationRequested();
        var provider = GetProvider(providerId);
        if (provider is null) return null;
        var auth = provider.Auth;
        if (auth is null) return null;

        var credential = await ReadCredentialAsync(providerId, ct).ConfigureAwait(false);
        if (credential is OAuthCredential)
        {
            return auth.OAuth is not null
                ? new AuthCheck(AuthType.OAuth, "OAuth")
                : null;
        }

        var apiKey = auth.ApiKey;
        if (apiKey is null) return null;
        if (apiKey.Check is not null)
        {
            return await CheckApiKeyAsync(provider.Id, apiKey, credential as ApiKeyCredential, ct).ConfigureAwait(false);
        }

        var result = await ResolveAuthAsync(provider, new AuthResolutionOverrides { Signal = ct })
            .ConfigureAwait(false);
        return result is null ? null : new AuthCheck(AuthType.ApiKey, result.Source);
    }

    public async Task<IReadOnlyList<Model>> GetAvailableAsync(
        string? provider = null,
        AuthOperationOptions? options = null)
    {
        var result = new List<Model>();
        foreach (var entry in SelectProviders(provider))
        {
            if (await CheckAuthAsync(entry.Id, options).ConfigureAwait(false) is null) continue;

            var credential = await ReadCredentialAsync(
                entry.Id, options?.CancellationToken ?? default).ConfigureAwait(false);
            var models = SafeChatModels(entry);
            if (entry is IFilterableProvider filterable)
            {
                models = filterable.FilterModels(models, credential);
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
            result.AddRange((await GetAvailableAllAsync(entry, options).ConfigureAwait(false))
                .Where(model => model.Type == type));
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
            result.AddRange(await GetAvailableAllAsync(entry, options).ConfigureAwait(false));
        }
        return result;
    }

    private async Task<IReadOnlyList<ProviderModel>> GetAvailableAllAsync(
        IProvider entry,
        AuthOperationOptions? options)
    {
        if (await CheckAuthAsync(entry.Id, options).ConfigureAwait(false) is null)
            return Array.Empty<ProviderModel>();

        var credential = await ReadCredentialAsync(
            entry.Id, options?.CancellationToken ?? default).ConfigureAwait(false);
        var all = SafeAllModels(entry);
        if (entry is not IFilterableProvider filterable) return all;

        var filteredAll = filterable.FilterAllModels(all, credential);
        if (filteredAll is not null) return filteredAll;

        // pi fallback：保持原顺序，只剔除不可用 chat；其他类别原样保留。
        var availableChatIds = filterable.FilterModels(SafeChatModels(entry), credential)
            .Select(model => model.Id)
            .ToHashSet(StringComparer.Ordinal);
        return all
            .Where(model => model.Type != ModelType.Chat || availableChatIds.Contains(model.Id))
            .ToList();
    }

    public Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null)
    {
        // TODO(S4, D2): 动态模型目录刷新。
        return Task.FromResult(new ModelsRefreshResult(false, new Dictionary<string, Exception>()));
    }

    public Task<Credential> LoginAsync(
        string providerId,
        AuthType type,
        AuthInteraction interaction,
        LoginOptions? options = null)
        => throw new NotSupportedException("Deferred to S5: interactive login (D1)");

    public async Task LogoutAsync(string providerId, AuthOperationOptions? options = null)
    {
        // pi logout：只删 store，不校验 provider 是否注册（未注册/未存凭据都是安静删除）。
        try
        {
            await _credentials.DeleteAsync(providerId, options).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCode.Auth,
                $"Credential store delete failed for {providerId}", error);
        }
    }

    private async Task<AuthResult?> ResolveAuthAsync(
        IProvider provider,
        AuthResolutionOverrides? overrides)
    {
        var ct = overrides?.Signal ?? default;
        ct.ThrowIfCancellationRequested();
        var auth = provider.Auth;
        if (auth is null) return null;

        // 1. 显式 apiKey 只在 provider 支持 api-key 时走 provider Resolve；合成凭据带 Env。
        // pi：overrides.apiKey !== undefined——空串也是显式 override。
        if (overrides?.ApiKey is not null && auth.ApiKey is not null)
        {
            var synthetic = new ApiKeyCredential(overrides.ApiKey) { Env = overrides.Env };
            return await ResolveApiKeyAsync(provider, synthetic, ct, overrides.Env).ConfigureAwait(false);
        }

        // 2. stored credential owns provider。
        var stored = await ReadCredentialAsync(provider.Id, ct).ConfigureAwait(false);
        if (stored is OAuthCredential oauth)
        {
            if (auth.OAuth?.ToAuth is null) return null;
            try
            {
                var modelAuth = await auth.OAuth.ToAuth(oauth, ct).ConfigureAwait(false);
                return new AuthResult(modelAuth, null, "OAuth"); // OAuth Extra→env 随 D1/S5
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ModelsError(ModelsErrorCode.OAuth,
                    $"OAuth auth derivation failed for provider {provider.Id}", error);
            }
        }

        if (stored is ApiKeyCredential apiKey)
        {
            if (auth.ApiKey is null) return null;
            var env = MergeEnv(apiKey.Env, overrides?.Env);
            var credential = env is null ? apiKey : apiKey with { Env = env };
            return await ResolveApiKeyAsync(provider, credential, ct, overrides?.Env).ConfigureAwait(false);
        }

        // stored 类型无匹配：不回落 ambient。
        if (stored is not null) return null;
        if (auth.ApiKey is null) return null;

        // 3. 仅无 stored 时走 ambient。
        return await ResolveApiKeyAsync(provider, null, ct, overrides?.Env).ConfigureAwait(false);
    }

    private async Task<AuthResult?> ResolveApiKeyAsync(
        IProvider provider,
        ApiKeyCredential? credential,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? overrideEnv = null)
    {
        var apiKey = provider.Auth?.ApiKey
            ?? throw new ModelsError(ModelsErrorCode.Provider,
                $"Provider {provider.Id} has no api-key auth");
        var context = overrideEnv is { Count: > 0 }
            ? new OverlayAuthContext(_authContext, overrideEnv)
            : _authContext;
        try
        {
            return await apiKey.Resolve(new ApiKeyResolveInput(context, credential), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCode.Auth,
                $"API key auth failed for provider {provider.Id}", error);
        }
    }

    private async Task<AuthCheck?> CheckApiKeyAsync(
        string providerId,
        ApiKeyAuth apiKey,
        ApiKeyCredential? credential,
        CancellationToken ct)
    {
        try
        {
            return await apiKey.Check!(new ApiKeyCheckInput(_authContext, credential), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCode.Auth,
                $"API key auth check failed for {providerId}", error);
        }
    }

    private async Task<Credential?> ReadCredentialAsync(string providerId, CancellationToken ct)
    {
        try
        {
            return await _credentials.ReadAsync(
                providerId, new AuthOperationOptions { CancellationToken = ct }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCode.Auth,
                $"Credential store read failed for {providerId}", error);
        }
    }

    private IReadOnlyList<IProvider> SelectProviders(string? providerId)
        => providerId is null
            ? GetProviders()
            : GetProvider(providerId) is { } provider
                ? new[] { provider }
                : Array.Empty<IProvider>();

    private sealed class OverlayAuthContext : IAuthContext
    {
        private readonly IAuthContext _base;
        private readonly IReadOnlyDictionary<string, string> _env;

        public OverlayAuthContext(IAuthContext @base, IReadOnlyDictionary<string, string> env)
        {
            _base = @base;
            _env = env;
        }

        public async Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
        {
            // pi overlayEnvAuthContext：env[name] || base.env(name)——空串回落 base。
            var value = _env.TryGetValue(name, out var found) && !string.IsNullOrEmpty(found)
                ? found
                : null;
            return value ?? await _base.EnvAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => _base.FileExistsAsync(path, cancellationToken);
    }
}
