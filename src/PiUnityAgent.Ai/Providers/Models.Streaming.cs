using System;
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
            var key = await ResolveApiKeyAsync(provider, options?.ApiKey).ConfigureAwait(false);
            if (key is not null)
            {
                options = (options ?? new StreamOptions()) with { ApiKey = key };
            }

            return provider.Stream(model, transcript, options);
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
            var key = await ResolveApiKeyAsync(provider, options?.ApiKey).ConfigureAwait(false);
            if (key is not null)
            {
                options = (options ?? new SimpleStreamOptions()) with { ApiKey = key };
            }

            return provider.StreamSimple(model, transcript, options);
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

    private async Task<string?> ResolveApiKeyAsync(IProvider provider, string? suppliedKey)
    {
        if (!string.IsNullOrEmpty(suppliedKey)) return null;
        if (provider.Auth?.ApiKey is null) return null;

        var auth = await GetAuthAsync(provider.Id).ConfigureAwait(false);
        if (auth?.Credential is ApiKeyCredential apiKey && !string.IsNullOrEmpty(apiKey.Key))
        {
            return apiKey.Key;
        }

        throw new InvalidOperationException(
            $"Authentication not configured for provider: {provider.Id}");
    }
}
