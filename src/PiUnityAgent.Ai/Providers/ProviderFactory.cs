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

        // 校验的是"具体实现是否至少一个"（对齐 pi models.ts:1087-1091 / pi.net CreateProvider.cs:57-65）：
        // 数的是非 null 实现——空或全 null 的 ApiByApi 不算实现；缺 api 映射仍按 MissingApi 报错流。
        var hasStreams = options.Api is not null
            || options.ApiByApi?.Values.Any(entry => entry is not null) == true;
        var hasImages = options.Images?.Values.Any(entry => entry is not null) == true;
        var hasClassifiers = options.Classifiers?.Values.Any(entry => entry is not null) == true;
        if (!hasStreams && !hasImages && !hasClassifiers)
            throw new ModelsError(ModelsErrorCode.Provider,
                $"Provider {options.Id}: at least one of Api/ApiByApi/Images/Classifiers is required.");

        // D2：FetchModels 在手才可刷新——工厂产物只有它实现 IRefreshableProvider（对齐 pi createProvider models.ts:1129）。
        return options.FetchModels is not null
            ? new RefreshableCreatedProvider(options, options.Api, options.ApiByApi)
            : new CreatedProvider(options, options.Api, options.ApiByApi);
    }

    private class CreatedProvider : IProvider, IFilterableProvider, IImagesProvider, IClassifierProvider, IDeferredProvider
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

        public virtual IReadOnlyList<Model> GetModels() => _options.Models.OfType<Model>().ToList();
        public virtual IReadOnlyList<ProviderModel> GetAllModels() => _options.Models;

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

        public bool SupportsFetchDeferred => _single?.FetchDeferred is not null
            || (_byApi?.Values.Any(streams => streams is not null && streams.FetchDeferred is not null) ?? false);

        public bool SupportsCancelDeferred => _single?.CancelDeferred is not null
            || (_byApi?.Values.Any(streams => streams is not null && streams.CancelDeferred is not null) ?? false);

        public IAssistantMessageEventStream StreamDeferred(
            Model model,
            DeferredHandle handle,
            DeferredFetchOptions? options)
        {
            var streams = ApiFor(model);
            if (streams?.FetchDeferred is null)
            {
                throw new ModelsError(
                    ModelsErrorCode.Provider,
                    $"Provider {_options.Id} does not support deferred responses");
            }

            return streams.FetchDeferred(model, handle, options);
        }

        public Task<AssistantMessage> FetchDeferredAsync(
            Model model,
            DeferredHandle handle,
            DeferredFetchOptions? options)
            => StreamDeferred(model, handle, options)
                .WaitForDoneAsync(options?.CancellationToken ?? default);

        public Task CancelDeferredAsync(
            Model model,
            DeferredHandle handle,
            DeferredCancelOptions? options)
        {
            var streams = ApiFor(model);
            if (streams?.CancelDeferred is null)
            {
                throw new ModelsError(
                    ModelsErrorCode.Provider,
                    $"Provider {_options.Id} does not support deferred responses");
            }

            return streams.CancelDeferred(
                model,
                handle,
                options,
                options?.CancellationToken ?? default);
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

    /// <summary>
    /// D2：带 FetchModels 的工厂产物——动态目录可刷新。
    /// GetAllModels = 静态基线 ∪ 动态覆盖（同 type+id 就地替换，否则追加，pi models.ts:1093-1103）；
    /// GetModels = 覆盖后的 chat。refreshModels 包装对齐 pi models.ts:1129-1160。
    /// </summary>
    private sealed class RefreshableCreatedProvider : CreatedProvider, IRefreshableProvider
    {
        private readonly CreateProviderOptions _options;
        private readonly object _gate = new();
        private IReadOnlyList<ProviderModel> _dynamic = Array.Empty<ProviderModel>();

        public RefreshableCreatedProvider(
            CreateProviderOptions options,
            ProviderStreams? single,
            IReadOnlyDictionary<string, ProviderStreams>? byApi)
            : base(options, single, byApi)
        {
            _options = options;
        }

        public override IReadOnlyList<Model> GetModels() => Overlay().OfType<Model>().ToList();

        public override IReadOnlyList<ProviderModel> GetAllModels() => Overlay();

        public async Task RefreshModelsAsync(RefreshModelsContext context)
        {
            // a. 恢复存储的动态目录：只取本 provider 的条目（pi models.ts:1132-1135）。
            if (context.Stored is not null)
            {
                var restored = context.Stored.Models
                    .Where(model => string.Equals(model.Provider, _options.Id, StringComparison.Ordinal))
                    .ToList();
                if (!await context.Publish(new ModelsPublication
                    {
                        Update = () => SetDynamic(restored),
                    }).ConfigureAwait(false))
                {
                    return;
                }
            }

            // b. 网络阶段双闸：AllowNetwork + 取消（pi models.ts:1140）。
            if (!context.AllowNetwork || context.CancellationToken.IsCancellationRequested) return;

            // c. 拉取 → 滤掉未知类型 → 持久化 + 点亮（pi models.ts:1141-1159）。
            //    FetchModels 抛错不打原地捕获：向上传给 Models.RefreshAsync 按 provider 收集，旧表保留。
            var fetched = await _options.FetchModels!(context).ConfigureAwait(false);
            if (context.CancellationToken.IsCancellationRequested) return;
            var refreshed = fetched.Where(HasKnownModelType).ToList();
            if (!await context.Publish(new ModelsPublication
                {
                    Persist = new ModelsStoreEntry
                    {
                        Models = refreshed,
                        CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    },
                    Update = () => SetDynamic(refreshed),
                }).ConfigureAwait(false))
            {
                return;
            }
        }

        private IReadOnlyList<ProviderModel> Overlay()
        {
            lock (_gate)
            {
                if (_dynamic.Count == 0) return _options.Models;
                // pi currentModels：基线保序，同 type+id 就地替换，否则追加。
                var merged = new List<ProviderModel>(_options.Models);
                foreach (var model in _dynamic)
                {
                    var index = merged.FindIndex(
                        entry => entry.Type == model.Type && entry.Id == model.Id);
                    if (index >= 0) merged[index] = model;
                    else merged.Add(model);
                }
                return merged;
            }
        }

        private void SetDynamic(IReadOnlyList<ProviderModel> models)
        {
            lock (_gate) _dynamic = models;
        }

        // pi hasKnownModelType（models.ts:135）；C# 的 ProviderModel 是封闭三型，此处是口径自证。
        private static bool HasKnownModelType(ProviderModel model)
            => model.Type is ModelType.Chat or ModelType.Image or ModelType.Classifier;
    }
}
