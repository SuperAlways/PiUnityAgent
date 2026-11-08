using System;
using System.Collections.Generic;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed class StaticProvider : IProvider
{
    private readonly IReadOnlyList<Model> _models;
    private readonly StreamFn _stream;

    public StaticProvider(
        string id,
        string name,
        IReadOnlyList<Model> models,
        StreamFn stream,
        ProviderAuth auth,
        string? baseUrl = null,
        IReadOnlyDictionary<string, string?>? headers = null)
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
}
