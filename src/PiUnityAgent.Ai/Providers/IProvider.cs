using System.Collections.Generic;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>pi Provider 的 C# 形状；运行时单元 = 元数据 + 模型列举 + 操作。</summary>
public interface IProvider
{
    string Id { get; }
    string Name { get; }
    string? BaseUrl { get; }
    IReadOnlyDictionary<string, string?>? Headers { get; }
    ProviderAuth? Auth { get; }

    IReadOnlyList<Model> GetModels();
    IReadOnlyList<ProviderModel> GetAllModels();

    IAssistantMessageEventStream Stream(
        Model model,
        TranscriptContext context,
        StreamOptions? options);

    IAssistantMessageEventStream StreamSimple(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options);
}
