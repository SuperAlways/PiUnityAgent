using System.Collections.Generic;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>动态目录刷新；不用默认接口方法。</summary>
public interface IRefreshableProvider
{
    Task RefreshModelsAsync(RefreshModelsContext context);
}

/// <summary>凭据相关的模型过滤；FilterAllModels 返回 null 时走 Models 的保序 fallback。</summary>
public interface IFilterableProvider
{
    IReadOnlyList<Model> FilterModels(IReadOnlyList<Model> models, Credential? credential);

    IReadOnlyList<ProviderModel>? FilterAllModels(
        IReadOnlyList<ProviderModel> models,
        Credential? credential);
}

/// <summary>Deferred 响应能力；行为后置 S8/D3，形状先存在。</summary>
public interface IDeferredProvider
{
    IAssistantMessageEventStream StreamDeferred(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options);

    Task<AssistantMessage> FetchDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options);

    Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options);
}

/// <summary>图片生成能力；行为后置 2B/D4。</summary>
public interface IImagesProvider
{
    Task<AssistantImages> GenerateImagesAsync(
        ImageModel model,
        ImagesContext context,
        ImagesOptions? options);
}

/// <summary>结构化分类能力；行为后置 2B/D5。</summary>
public interface IClassifierProvider
{
    Task<ClassifierResult> ClassifyAsync(
        ClassifierModel model,
        ClassifierContext context,
        ClassifierOptions? options);
}
