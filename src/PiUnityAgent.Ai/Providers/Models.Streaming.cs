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
            var auth = await ResolveAuthForStreamAsync(model, options?.ApiKey, ct).ConfigureAwait(false);
            options = ApplyAuthOptions(options, auth);
            var requestModel = auth.Auth.BaseUrl is null ? model : model with { BaseUrl = auth.Auth.BaseUrl };
            return provider.Stream(requestModel, transcript, options);
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
            var auth = await ResolveAuthForStreamAsync(model, options?.ApiKey, ct).ConfigureAwait(false);
            options = ApplyAuthOptions(options, auth);
            var requestModel = auth.Auth.BaseUrl is null ? model : model with { BaseUrl = auth.Auth.BaseUrl };
            return provider.StreamSimple(requestModel, transcript, options);
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
           ?? throw new InvalidOperationException($"Unknown provider: {providerId}");

    private async Task<AuthResult> ResolveAuthForStreamAsync(
        Model model, string? suppliedKey, CancellationToken ct)
    {
        var auth = await GetAuthAsync((ProviderModel)model, new AuthResolutionOverrides
        {
            ApiKey = suppliedKey,
            Signal = ct,
        }).ConfigureAwait(false);
        if (auth is null)
            throw new ModelsError(ModelsErrorCode.Auth,
                $"Authentication not configured for provider: {model.Provider}");
        return auth;
    }

    private static StreamOptions ApplyAuthOptions(StreamOptions? options, AuthResult auth)
    {
        options ??= new StreamOptions();
        var headers = MergeHeaders(auth.Auth.Headers, options.Headers);
        var env = MergeEnv(auth.Env, options.Env);
        return options with
        {
            ApiKey = string.IsNullOrEmpty(options.ApiKey) ? auth.Auth.ApiKey : options.ApiKey,
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
            ApiKey = string.IsNullOrEmpty(options.ApiKey) ? auth.Auth.ApiKey : options.ApiKey,
            Headers = headers,
            Env = env,
        };
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
