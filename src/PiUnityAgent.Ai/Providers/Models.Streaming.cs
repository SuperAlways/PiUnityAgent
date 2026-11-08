using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed partial class Models
{
    public IAssistantMessageEventStream Stream(
        Model model,
        Context context,
        StreamOptions? options = null)
    {
        var transcript = TranscriptNormalization.NormalizeContext(context);
        return LazyStream.Run(model, async () =>
        {
            var provider = RequireProvider(model.Provider);
            var ct = options?.CancellationToken ?? default;
            var auth = await ResolveAuthForStreamAsync(model, options).ConfigureAwait(false);
            var applied = ApplyAuthOptions(options, auth);
            applied = ApplyIdentity(applied);
            applied = await ApplyTransformHeaders(applied, ct).ConfigureAwait(false);
            var requestModel = auth.Auth.BaseUrl is null ? model : model with { BaseUrl = auth.Auth.BaseUrl };
            return provider.Stream(requestModel, transcript, applied with { TransformHeaders = null });
        });
    }

    public Task<AssistantMessage> CompleteAsync(
        Model model,
        Context context,
        StreamOptions? options = null)
        => Stream(model, context, options)
            .WaitForDoneAsync(options?.CancellationToken ?? default);

    public IAssistantMessageEventStream StreamSimple(
        Model model,
        Context context,
        SimpleStreamOptions? options = null)
    {
        var transcript = TranscriptNormalization.NormalizeContext(context);
        return LazyStream.Run(model, async () =>
        {
            var provider = RequireProvider(model.Provider);
            var ct = options?.CancellationToken ?? default;
            var auth = await ResolveAuthForStreamAsync(model, options).ConfigureAwait(false);
            var applied = ApplyAuthOptions(options, auth);
            applied = ApplyIdentity(applied);
            applied = await ApplyTransformHeaders(applied, ct).ConfigureAwait(false);
            var requestModel = auth.Auth.BaseUrl is null ? model : model with { BaseUrl = auth.Auth.BaseUrl };
            return provider.StreamSimple(requestModel, transcript, applied with { TransformHeaders = null });
        });
    }

    public Task<AssistantMessage> CompleteSimpleAsync(
        Model model,
        Context context,
        SimpleStreamOptions? options = null)
        => StreamSimple(model, context, options)
            .WaitForDoneAsync(options?.CancellationToken ?? default);

    private IProvider RequireProvider(string providerId)
        => GetProvider(providerId)
           ?? throw new ModelsError(ModelsErrorCode.Provider, $"Unknown provider: {providerId}");

    /// <summary>
    /// pi applyAuth（models.ts）：getAuth(model, { apiKey, env, signal })——
    /// 请求级 env 参与解析期 overlay，不只是事后合并；未配置文案与 pi 逐字一致。
    /// </summary>
    private async Task<AuthResult> ResolveAuthForStreamAsync(Model model, StreamOptions? options)
    {
        var auth = await GetAuthAsync((ProviderModel)model, new AuthResolutionOverrides
        {
            ApiKey = options?.ApiKey,
            Env = options?.Env,
            Signal = options?.CancellationToken ?? default,
        }).ConfigureAwait(false);
        if (auth is null)
            throw new ModelsError(ModelsErrorCode.Auth,
                $"Provider is not configured: {model.Provider}");
        return auth;
    }

    private static StreamOptions ApplyAuthOptions(StreamOptions? options, AuthResult auth)
    {
        options ??= new StreamOptions();
        var headers = MergeHeaders(auth.Auth.Headers, options.Headers);
        var env = MergeEnv(auth.Env, options.Env);
        return options with
        {
            // pi：options.apiKey ?? auth.apiKey——空串是显式值，胜过解析结果。
            ApiKey = options.ApiKey is null ? auth.Auth.ApiKey : options.ApiKey,
            Headers = headers,
            Env = env,
        };
    }

    private static SimpleStreamOptions ApplyAuthOptions(SimpleStreamOptions? options, AuthResult auth)
    {
        options ??= new SimpleStreamOptions();
        var headers = MergeHeaders(auth.Auth.Headers, options.Headers);
        var env = MergeEnv(auth.Env, options.Env);
        return options with
        {
            ApiKey = options.ApiKey is null ? auth.Auth.ApiKey : options.ApiKey,
            Headers = headers,
            Env = env,
        };
    }

    /// <summary>pi 门面级 identity 与请求级 identity 逐字段合并（请求级覆盖门面级）。</summary>
    private StreamOptions ApplyIdentity(StreamOptions options)
    {
        var identity = ClientIdentityMerger.Merge(_identity, options.Identity);
        return identity is null ? options : options with { Identity = identity };
    }

    private SimpleStreamOptions ApplyIdentity(SimpleStreamOptions options)
    {
        var identity = ClientIdentityMerger.Merge(_identity, options.Identity);
        return identity is null ? options : options with { Identity = identity };
    }

    /// <summary>pi transformHeaders：进 provider 前执行一次并清除；provider 不感知该字段。</summary>
    private static async Task<StreamOptions> ApplyTransformHeaders(
        StreamOptions options,
        CancellationToken cancellationToken)
    {
        var transform = options.TransformHeaders;
        if (transform is null) return options;
        cancellationToken.ThrowIfCancellationRequested();
        var headers = await transform(options.Headers ?? new Dictionary<string, string?>())
            .ConfigureAwait(false);
        return options with { Headers = headers, TransformHeaders = null };
    }

    private static async Task<SimpleStreamOptions> ApplyTransformHeaders(
        SimpleStreamOptions options,
        CancellationToken cancellationToken)
    {
        var transform = options.TransformHeaders;
        if (transform is null) return options;
        cancellationToken.ThrowIfCancellationRequested();
        var headers = await transform(options.Headers ?? new Dictionary<string, string?>())
            .ConfigureAwait(false);
        return options with { Headers = headers, TransformHeaders = null };
    }

    private static IReadOnlyDictionary<string, string?>? MergeHeaders(
        IReadOnlyDictionary<string, string?>? provider,
        IReadOnlyDictionary<string, string?>? request)
    {
        if (provider is null && request is null) return null;
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (provider is not null)
            foreach (var entry in provider) merged[entry.Key] = entry.Value;
        if (request is not null)
        {
            foreach (var entry in request)
            {
                foreach (var existing in merged.Keys.ToList())
                {
                    if (string.Equals(existing, entry.Key, StringComparison.OrdinalIgnoreCase))
                        merged.Remove(existing);
                }
                merged[entry.Key] = entry.Value;
            }
        }
        return merged;
    }

    private static IReadOnlyDictionary<string, string>? MergeEnv(
        IReadOnlyDictionary<string, string>? provider,
        IReadOnlyDictionary<string, string>? request)
    {
        if (provider is null && request is null) return null;
        var merged = new Dictionary<string, string>(provider ?? new Dictionary<string, string>());
        if (request is not null) foreach (var entry in request) merged[entry.Key] = entry.Value;
        return merged;
    }
}
