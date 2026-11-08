using System;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>pi ProviderStreams 的 C# 形状：一个 api 实现的流式操作集合。</summary>
public sealed record ProviderStreams
{
    public required StreamFn Stream { get; init; }
    public required SimpleStreamFn StreamSimple { get; init; }
    public Func<Model, DeferredHandle, DeferredFetchOptions?, IAssistantMessageEventStream>? FetchDeferred { get; init; }
    public Func<Model, DeferredHandle, DeferredCancelOptions?, CancellationToken, Task>? CancelDeferred { get; init; }
}

/// <summary>pi ProviderImages 的 C# 形状：一个 api 实现的图片生成操作。</summary>
public sealed record ProviderImages
{
    public required Func<ImageModel, ImagesContext, ImagesOptions?, Task<AssistantImages>> GenerateImages { get; init; }
}

/// <summary>pi ProviderClassifier 的 C# 形状：一个 api 实现的分类操作。</summary>
public sealed record ProviderClassifier
{
    public required Func<ClassifierModel, ClassifierContext, ClassifierOptions?, Task<ClassifierResult>> Classify { get; init; }
}
