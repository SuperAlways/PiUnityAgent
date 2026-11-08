using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>Xiaomi MiMo profile（pi providers/xiaomi.ts；token-plan 变体不在本批）。</summary>
public static class XiaomiProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "xiaomi",
        Name = "Xiaomi",
        BaseUrl = "https://api.xiaomimimo.com/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Xiaomi API key", new[] { "XIAOMI_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("xiaomi"),
        Api = OpenAiCompletions.Streams(),
    });
}
