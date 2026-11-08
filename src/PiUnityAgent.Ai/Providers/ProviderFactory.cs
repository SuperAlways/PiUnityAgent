using System;
using System.Collections.Generic;
using System.Linq;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>pi createProvider 的 C# 等价物：校验入参并装配静态 IProvider。</summary>
public static class ProviderFactory
{
    public static IProvider Create(CreateProviderOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (options.Auth is null || (options.Auth.ApiKey is null && options.Auth.OAuth is null))
            throw new ModelsError(ModelsErrorCode.Provider,
                $"Provider {options.Id}: at least one of auth.apiKey/auth.oauth is required.");
        if (options.Api is not null && options.ApiByApi is not null)
            throw new ModelsError(ModelsErrorCode.Provider,
                $"Provider {options.Id}: specify Api or ApiByApi, not both.");

        // 校验的是"槽位是否提供"而非"集合非空"：空 ApiByApi 合法，缺 api 时按 MissingApi 报错流。
        var hasStreams = options.Api is not null || options.ApiByApi is not null;
        var hasImages = options.Images?.Count > 0;
        var hasClassifiers = options.Classifiers?.Count > 0;
        if (!hasStreams && !hasImages && !hasClassifiers)
            throw new ModelsError(ModelsErrorCode.Provider,
                $"Provider {options.Id}: at least one of Api/ApiByApi/Images/Classifiers is required.");

        return new CreatedProvider(options, options.Api, options.ApiByApi);
    }

    private sealed class CreatedProvider : IProvider, IFilterableProvider, IImagesProvider, IClassifierProvider
    {
        private readonly CreateProviderOptions _options;
        private readonly ProviderStreams? _single;
        private readonly IReadOnlyDictionary<string, ProviderStreams>? _byApi;

        public CreatedProvider(
            CreateProviderOptions options,
            ProviderStreams? single,
            IReadOnlyDictionary<string, ProviderStreams>? byApi)
        {
            _options = options;
            _single = single;
            _byApi = byApi;
        }

        public string Id => _options.Id;
        public string Name => _options.Name ?? _options.Id;
        public string? BaseUrl => _options.BaseUrl;
        public IReadOnlyDictionary<string, string?>? Headers => _options.Headers;
        public ProviderAuth Auth => _options.Auth;

        public IReadOnlyList<Model> GetModels() => _options.Models.OfType<Model>().ToList();
        public IReadOnlyList<ProviderModel> GetAllModels() => _options.Models;

        public IReadOnlyList<Model> FilterModels(IReadOnlyList<Model> models, Credential? credential)
            => _options.FilterModels?.Invoke(models, credential) ?? models;

        public IReadOnlyList<ProviderModel>? FilterAllModels(
            IReadOnlyList<ProviderModel> models, Credential? credential)
            => _options.FilterAllModels?.Invoke(models, credential);

        public IAssistantMessageEventStream Stream(
            Model model, TranscriptContext context, StreamOptions? options)
        {
            var streams = ApiFor(model);
            return streams is null ? MissingApi(model) : streams.Stream(model, context, options);
        }

        public IAssistantMessageEventStream StreamSimple(
            Model model, TranscriptContext context, SimpleStreamOptions? options)
        {
            var streams = ApiFor(model);
            return streams is null ? MissingApi(model) : streams.StreamSimple(model, context, options);
        }

        private ProviderStreams? ApiFor(Model model) => _single ?? _byApi?.GetValueOrDefault(model.Api);

        private IAssistantMessageEventStream MissingApi(Model model)
            => LazyStream.Run(model, () => throw new ModelsError(
                ModelsErrorCode.Stream,
                $"Provider {_options.Id} has no API implementation for \"{model.Api}\""));

        // D4/D5：S3 只保证能力接口与 api-map 分派骨架；真实图片/分类行为在 2B。
        public Task<AssistantImages> GenerateImagesAsync(
            ImageModel model, ImagesContext context, ImagesOptions? options)
            => _options.Images is not null && _options.Images.TryGetValue(model.Api, out var implementation)
                ? implementation.GenerateImages(model, context, options)
                : throw new NotSupportedException("Deferred to 2B: image generation (D4)");

        public Task<ClassifierResult> ClassifyAsync(
            ClassifierModel model, ClassifierContext context, ClassifierOptions? options)
            => _options.Classifiers is not null && _options.Classifiers.TryGetValue(model.Api, out var implementation)
                ? implementation.Classify(model, context, options)
                : throw new NotSupportedException("Deferred to 2B: classification (D5)");
    }
}
