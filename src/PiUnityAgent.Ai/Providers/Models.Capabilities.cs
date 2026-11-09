using System;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed partial class Models
{
    public IAssistantMessageEventStream StreamDeferred(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options = null)
    {
        return LazyStream.Run(model, async () =>
        {
            var provider = RequireProvider(model.Provider);
            if (provider is not IDeferredProvider deferred)
            {
                throw new ModelsError(
                    ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support deferred responses");
            }
            if (!deferred.SupportsFetchDeferred)
            {
                throw new ModelsError(
                    ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support deferred responses");
            }

            var cancellationToken = options?.CancellationToken ?? default;
            var auth = await ResolveAuthForStreamAsync(model, options).ConfigureAwait(false);
            var requestModel = auth.Auth.BaseUrl is null
                ? model
                : model with { BaseUrl = auth.Auth.BaseUrl };
            var applied = ApplyAuthOptions(requestModel, options, auth);
            applied = ApplyIdentity(applied);
            applied = await ApplyTransformHeaders(applied, cancellationToken)
                .ConfigureAwait(false);
            return deferred.StreamDeferred(requestModel, handle, applied);
        });
    }

    public Task<AssistantMessage> FetchDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options = null)
        => StreamDeferred(model, handle, options)
            .WaitForDoneAsync(options?.CancellationToken ?? default);

    public async Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options = null)
    {
        var provider = RequireProvider(model.Provider);
        if (provider is not IDeferredProvider deferred)
        {
            throw new ModelsError(
                ModelsErrorCode.Provider,
                $"Provider {model.Provider} does not support deferred responses");
        }
        if (!deferred.SupportsCancelDeferred)
        {
            throw new ModelsError(
                ModelsErrorCode.Provider,
                $"Provider {model.Provider} does not support deferred responses");
        }

        var auth = await ResolveAuthForStreamAsync(model, options).ConfigureAwait(false);
        var requestModel = auth.Auth.BaseUrl is null
            ? model
            : model with { BaseUrl = auth.Auth.BaseUrl };
        var applied = ApplyAuthOptions(requestModel, options, auth);
        await deferred.CancelDeferredAsync(requestModel, handle, applied)
            .ConfigureAwait(false);
    }

    public Task<AssistantImages> GenerateImagesAsync(
        ImageModel model,
        ImagesContext context,
        ImagesOptions? options = null)
        => throw new NotSupportedException("Deferred to 2B: image generation (D4)");

    public Task<ClassifierResult> ClassifyAsync(
        ClassifierModel model,
        ClassifierContext context,
        ClassifierOptions? options = null)
        => throw new NotSupportedException("Deferred to 2B: classification (D5)");
}
