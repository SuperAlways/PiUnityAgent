using System;
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
        => throw new NotSupportedException(
            "Deferred to S8: deferred responses (D3)");

    public Task<AssistantMessage> FetchDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options = null)
        => throw new NotSupportedException(
            "Deferred to S8: deferred responses (D3)");

    public Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options = null)
        => throw new NotSupportedException(
            "Deferred to S8: deferred responses (D3)");

    public Task<AssistantImages> GenerateImagesAsync(
        ImageModel model,
        ImagesContext context,
        ImagesOptions? options = null)
        => throw new NotSupportedException(
            "Deferred to 2B: image generation (D4)");

    public Task<ClassifierResult> ClassifyAsync(
        ClassifierModel model,
        ClassifierContext context,
        ClassifierOptions? options = null)
        => throw new NotSupportedException(
            "Deferred to 2B: classification (D5)");
}
