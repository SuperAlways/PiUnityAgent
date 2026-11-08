using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>pi CreateProviderOptions 的 C# 形状：ProviderFactory 的全部输入。</summary>
public sealed record CreateProviderOptions
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? BaseUrl { get; init; }
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    public required ProviderAuth Auth { get; init; }
    public IReadOnlyList<ProviderModel> Models { get; init; } = Array.Empty<ProviderModel>();

    public Func<RefreshModelsContext, Task<IReadOnlyList<ProviderModel>>>? FetchModels { get; init; }
    public Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? FilterModels { get; init; }
    public Func<IReadOnlyList<ProviderModel>, Credential?, IReadOnlyList<ProviderModel>>? FilterAllModels { get; init; }

    public ProviderStreams? Api { get; init; }
    public IReadOnlyDictionary<string, ProviderStreams>? ApiByApi { get; init; }
    public IReadOnlyDictionary<string, ProviderImages>? Images { get; init; }
    public IReadOnlyDictionary<string, ProviderClassifier>? Classifiers { get; init; }
}
