using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed class StaticProvider : IProvider, IDeferredProvider
{
    private readonly IReadOnlyList<Model> _models;
    private readonly StreamFn _stream;
    private readonly Func<Model, DeferredHandle, DeferredFetchOptions?, IAssistantMessageEventStream>? _fetchDeferred;
    private readonly Func<Model, DeferredHandle, DeferredCancelOptions?, CancellationToken, Task>? _cancelDeferred;

    public StaticProvider(
        string id,
        string name,
        IReadOnlyList<Model> models,
        StreamFn stream,
        ProviderAuth auth,
        string? baseUrl = null,
        IReadOnlyDictionary<string, string?>? headers = null,
        Func<Model, DeferredHandle, DeferredFetchOptions?, IAssistantMessageEventStream>? fetchDeferred = null,
        Func<Model, DeferredHandle, DeferredCancelOptions?, CancellationToken, Task>? cancelDeferred = null)
    {
        Id = id;
        Name = name;
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Auth = auth ?? throw new ArgumentNullException(nameof(auth));
        if (Auth.ApiKey is null && Auth.OAuth is null)
            throw new ArgumentException("ProviderAuth requires ApiKey or OAuth.", nameof(auth));
        BaseUrl = baseUrl;
        Headers = headers;
        _fetchDeferred = fetchDeferred;
        _cancelDeferred = cancelDeferred;
    }

    public string Id { get; }
    public string Name { get; }
    public string? BaseUrl { get; }
    public IReadOnlyDictionary<string, string?>? Headers { get; }
    public ProviderAuth Auth { get; }

    public IReadOnlyList<Model> GetModels() => _models;

    public IReadOnlyList<ProviderModel> GetAllModels() => _models;

    public IAssistantMessageEventStream Stream(
        Model model,
        TranscriptContext context,
        StreamOptions? options)
        => _stream(model, context, options);

    public IAssistantMessageEventStream StreamSimple(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options)
        => _stream(model, context, options);

    public bool SupportsFetchDeferred => _fetchDeferred is not null;
    public bool SupportsCancelDeferred => _cancelDeferred is not null;

    public IAssistantMessageEventStream StreamDeferred(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options)
        => _fetchDeferred?.Invoke(model, handle, options)
           ?? throw new ModelsError(
               ModelsErrorCode.Provider,
               $"Provider {Id} does not support deferred responses");

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
        => _cancelDeferred?.Invoke(model, handle, options, options?.CancellationToken ?? default)
           ?? throw new ModelsError(
               ModelsErrorCode.Provider,
               $"Provider {Id} does not support deferred responses");
}
