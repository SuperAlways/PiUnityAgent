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
    /// <summary>pi DEFAULT_OAUTH_MINIMUM_VALIDITY_MS（auth/resolve.ts）：剩余不足五分钟即刷新。</summary>
    private const long DefaultOAuthMinimumValidityMs = 5 * 60 * 1000;

    /// <summary>pi DEFAULT_OAUTH_REFRESH_TIMEOUT_MS：已开始的刷新只受 15s 超时约束。</summary>
    private const int DefaultOAuthRefreshTimeoutMs = 15_000;

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

    /// <summary>
    /// D2：动态模型目录刷新（pi Models.refresh models.ts:565-675 的顺序化版本），每 provider 两阶段：
    /// 阶段 A 恢复（AllowNetwork=false，先于 auth 解析与网络；credential 读失败不挡恢复）→
    /// 阶段 B 解析有效凭据（resolveRefreshCredential，models.ts:627-654：stored OAuth 未过期原样、
    /// API key 走 provider Resolve 合成；解析不出即跳过网络）后联网拉取。
    /// AllowNetwork 缺省 true（对齐 pi）。只触达 IRefreshableProvider；
    /// 单个 provider 失败收集进 Errors，其余照常；取消 → Aborted。
    /// </summary>
    public async Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null)
    {
        var ct = options?.CancellationToken ?? default;
        var allowNetwork = options?.AllowNetwork ?? true;
        var errors = new Dictionary<string, Exception>();
        if (ct.IsCancellationRequested) return new ModelsRefreshResult(true, errors);

        var selected = options?.Providers is { } wanted
            ? new HashSet<string>(wanted, StringComparer.Ordinal)
            : null;
        foreach (var entry in GetProviders())
        {
            if (entry is not IRefreshableProvider refreshable) continue;
            if (selected is not null && !selected.Contains(entry.Id)) continue;

            try
            {
                // pi：credential 读失败不挡恢复阶段——先记下，恢复照跑，之后按 provider 抛出。
                Exception? credentialError = null;
                Credential? storedCredential = null;
                try
                {
                    storedCredential = await ReadCredentialAsync(entry.Id, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    credentialError = error;
                }

                // 阶段 A：恢复存储目录（pi models.ts:590，先于 auth 解析与网络）。
                await RunProviderRefreshPhaseAsync(
                    refreshable, entry.Id, entry.BaseUrl, storedCredential,
                    allowNetwork: false, force: false, ct).ConfigureAwait(false);
                if (credentialError is not null) throw credentialError;
                if (!allowNetwork) continue;
                if (ct.IsCancellationRequested) return new ModelsRefreshResult(true, errors);

                // 阶段 B：解析有效凭据后联网拉取（pi models.ts:594-596）；无凭据即跳过。
                var credential = await ResolveRefreshCredentialAsync(entry, storedCredential, ct)
                    .ConfigureAwait(false);
                if (credential is null) continue;
                await RunProviderRefreshPhaseAsync(
                    refreshable, entry.Id, entry.BaseUrl, credential,
                    allowNetwork: true, force: options?.Force ?? false, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ModelsRefreshResult(true, errors);
            }
            catch (Exception error)
            {
                errors[entry.Id] = error;
            }
        }

        return new ModelsRefreshResult(false, errors);
    }

    /// <summary>
    /// pi runProviderRefreshPhase（models.ts:549-563）的顺序化等价：每阶段自读 models store，
    /// 装配 RefreshModelsContext 后交给 provider；store 每阶段重读与 pi 一致（恢复可幂等重放）。
    /// </summary>
    private async Task RunProviderRefreshPhaseAsync(
        IRefreshableProvider refreshable,
        string providerId,
        string? baseUrl,
        Credential? credential,
        bool allowNetwork,
        bool force,
        CancellationToken ct)
    {
        var stored = await _modelsStore.ReadAsync(
            providerId,
            new ModelsStoreOperationOptions { CancellationToken = ct }).ConfigureAwait(false);
        var context = new RefreshModelsContext
        {
            Credential = credential,
            Stored = stored,
            BaseUrl = baseUrl,
            AllowNetwork = allowNetwork,
            // pi：force: allowNetwork ? force : undefined——恢复阶段不带 force。
            Force = allowNetwork && force,
            CancellationToken = ct,
            Publish = publication => PublishModelsAsync(providerId, publication, ct),
        };
        await refreshable.RefreshModelsAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// pi resolveStoredOAuth（auth/resolve.ts:153-191）的 C# 版：剩余有效期不足
    /// <see cref="DefaultOAuthMinimumValidityMs"/>（或显式 minOAuthValidityMs）即进
    /// store 锁刷新（双检：锁内复查，已被他人刷新/期间登出 → null），刷新结果已持久化；
    /// 显式要求 minOAuthValidityMs 而刷新后仍不够 → ModelsError(OAuth)。之后 ToAuth。
    /// </summary>
    private async Task<AuthResult?> ResolveStoredOAuthAsync(
        IProvider provider,
        OAuthAuth oauth,
        OAuthCredential stored,
        AuthResolutionOverrides? overrides,
        CancellationToken ct)
    {
        var minimumValidityMs = Math.Max(DefaultOAuthMinimumValidityMs, overrides?.MinOAuthValidityMs ?? 0);
        bool ExpiresSoon(OAuthCredential credential) => NowMs() + minimumValidityMs >= credential.ExpiresAt;
        var credential = stored;

        if (ExpiresSoon(credential))
        {
            // 乐观检查说已过期；权威检查在 store 锁内运行。
            var post = await RefreshStoredOAuthCredentialAsync(provider.Id, oauth, ExpiresSoon, ct)
                .ConfigureAwait(false);
            if (post is null) return null; // 期间已登出
            credential = post;
            // 常规五分钟窗口触发刷新但不构成 provider 契约；显式调用方（如 bearer-token 导出）
            // 刷新后才要求请求的最小有效期。
            if (overrides?.MinOAuthValidityMs is not null && ExpiresSoon(credential))
            {
                throw new ModelsError(ModelsErrorCode.OAuth,
                    $"OAuth refresh returned a token that expires too soon for {provider.Id}");
            }
        }

        try
        {
            var modelAuth = await oauth.ToAuth(credential, ct).ConfigureAwait(false);
            return new AuthResult(modelAuth, null, "OAuth");
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

    /// <summary>
    /// pi refreshStoredOAuthCredential（auth/resolve.ts:119-152）的 C# 版：在 credential
    /// store 的 modify 锁内刷新，锁内复查 needsRefresh，轮换出的凭据在释放锁前持久化
    /// （并发/跨进程只刷一次）。锁等待随 <paramref name="cancellationToken"/> 取消；已开始的
    /// 刷新只受 15s 超时约束（pi 同口径——否则取消会把唯一有效的 refresh token 丢掉）。
    /// fn 返回 null（期间登出/已被他人刷新）→ 返回 store 现值（pi：next ?? current）。
    /// </summary>
    private async Task<OAuthCredential?> RefreshStoredOAuthCredentialAsync(
        string providerId,
        OAuthAuth oauth,
        Func<OAuthCredential, bool> needsRefresh,
        CancellationToken cancellationToken)
    {
        Credential? post;
        try
        {
            post = await _credentials.ModifyAsync(providerId, async current =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (current is not OAuthCredential currentOauth) return null; // 期间已登出
                if (!needsRefresh(currentOauth)) return null;                 // 其他进程/请求已刷新
                try
                {
                    using var refreshTimeout = new CancellationTokenSource(
                        TimeSpan.FromMilliseconds(DefaultOAuthRefreshTimeoutMs));
                    return await oauth.Refresh(currentOauth, refreshTimeout.Token).ConfigureAwait(false);
                }
                catch (ModelsError)
                {
                    throw;
                }
                catch (Exception error)
                {
                    throw new ModelsError(ModelsErrorCode.OAuth,
                        $"OAuth refresh failed for {providerId}", error);
                }
            }, new AuthOperationOptions { CancellationToken = cancellationToken }).ConfigureAwait(false);
        }
        catch (ModelsError)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCode.Auth,
                $"Credential store modify failed for {providerId}", error);
        }

        return post as OAuthCredential;
    }

    /// <summary>
    /// pi resolveRefreshCredential（models.ts:627-654）的 C# 等价：
    /// stored OAuth 且 provider 支持 OAuth → 未过期原样使用，过期则锁内刷新并持久化轮换凭据；
    /// 其余走 provider ApiKey.Resolve（带 stored ApiKeyCredential 或 null），解析出结果即合成
    /// 仅含 key/env 的有效凭据；解析不出 → null（调用方跳过网络阶段）。错误原样上抛。
    /// </summary>
    private async Task<Credential?> ResolveRefreshCredentialAsync(
        IProvider provider,
        Credential? stored,
        CancellationToken ct)
    {
        if (stored is OAuthCredential oauth)
        {
            var handler = provider.Auth?.OAuth;
            if (handler is null) return null;
            if (NowMs() < oauth.ExpiresAt) return oauth;
            if (ct.IsCancellationRequested) return null;
            // 已开始的刷新不受取消/被后继刷新取代影响——轮换出的 refresh token 一定被持久化。
            return await RefreshStoredOAuthCredentialAsync(
                provider.Id, handler, current => NowMs() >= current.ExpiresAt, ct).ConfigureAwait(false);
        }

        var apiKey = provider.Auth?.ApiKey;
        if (apiKey is null) return null;
        var result = await apiKey
            .Resolve(new ApiKeyResolveInput(_authContext, stored as ApiKeyCredential), ct)
            .ConfigureAwait(false);
        return result is null
            ? null
            : new ApiKeyCredential(result.Auth.ApiKey) { Env = result.Env };
    }

    private static long NowMs() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    /// <summary>
    /// pi publishProviderModels（models.ts:517-542）的顺序化等价：
    /// 先 store 删/写（成功才算数），再点亮 Update。store 失败原样抛给 RefreshAsync 按 provider 收集（旧表保留）；
    /// 返回 true=发布生效；false=发布前发现取消（不动 store 也不点亮）。
    /// </summary>
    private async Task<bool> PublishModelsAsync(
        string providerId,
        ModelsPublication publication,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        var storeOptions = new ModelsStoreOperationOptions { CancellationToken = ct };
        if (publication.Delete)
        {
            await _modelsStore.DeleteAsync(providerId, storeOptions).ConfigureAwait(false);
        }
        else if (publication.Persist is not null)
        {
            await _modelsStore.WriteAsync(providerId, publication.Persist, storeOptions).ConfigureAwait(false);
        }
        publication.Update?.Invoke();
        return true;
    }

    /// <summary>
    /// pi Models.login（models.ts:775-830）的顺序化 C# 版：解析 provider → 校验该类型有 login →
    /// 走 provider login 取凭据 → 写入 credential store（锁等待随 ct 取消）。pi 的
    /// abort-竞速（mutation-started 侦听）在顺序流下退化为：store 失败包
    /// ModelsError(Auth)，取消原样上抛。返回值是 login 产出（pi 同）。
    /// </summary>
    public async Task<Credential> LoginAsync(
        string providerId,
        AuthType type,
        AuthInteraction interaction,
        LoginOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = GetProvider(providerId)
            ?? throw new ModelsError(ModelsErrorCode.Provider, $"Unknown provider: {providerId}");

        var credential = type == AuthType.OAuth
            ? (Credential)await LoginOAuthAsync(provider, interaction, options, cancellationToken).ConfigureAwait(false)
            : await LoginApiKeyAsync(provider, interaction, cancellationToken).ConfigureAwait(false);

        try
        {
            await _credentials.ModifyAsync(
                providerId,
                _ => Task.FromResult<Credential?>(credential),
                new AuthOperationOptions { CancellationToken = cancellationToken }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCode.Auth,
                $"Credential store modify failed for {providerId}", error);
        }

        return credential;
    }

    private static async Task<OAuthCredential> LoginOAuthAsync(
        IProvider provider,
        AuthInteraction interaction,
        LoginOptions? options,
        CancellationToken cancellationToken)
    {
        var oauth = provider.Auth?.OAuth
            ?? throw new ModelsError(ModelsErrorCode.Auth,
                $"{provider.Name} does not support {AuthTypeName(AuthType.OAuth)} login");
        return await oauth.Login(
            new ProviderAuthInteraction(interaction, cancellationToken), options, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ApiKeyCredential> LoginApiKeyAsync(
        IProvider provider,
        AuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        var apiKey = provider.Auth?.ApiKey
            ?? throw new ModelsError(ModelsErrorCode.Auth,
                $"{provider.Name} does not support {AuthTypeName(AuthType.ApiKey)} login");
        if (apiKey.Login is null)
        {
            throw new ModelsError(ModelsErrorCode.Auth,
                $"{provider.Name} does not support {AuthTypeName(AuthType.ApiKey)} login");
        }
        return await apiKey.Login(
            new ProviderAuthInteraction(interaction, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>pi 的 AuthType 字面量（"oauth"/"api_key"）用于报错文案。</summary>
    private static string AuthTypeName(AuthType type)
        => type == AuthType.OAuth ? "oauth" : "api_key";

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
            if (auth.OAuth is null) return null;
            return await ResolveStoredOAuthAsync(provider, auth.OAuth, oauth, overrides, ct).ConfigureAwait(false);
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
