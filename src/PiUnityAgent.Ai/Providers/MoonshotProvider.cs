using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>Moonshot AI 国际/国内双变体（pi providers/moonshotai.ts 与 moonshotai-cn.ts；共用 MOONSHOT_API_KEY）。</summary>
public static class MoonshotProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "moonshotai",
        Name = "Moonshot AI",
        BaseUrl = "https://api.moonshot.ai/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Moonshot AI API key", new[] { "MOONSHOT_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("moonshotai"),
        Api = OpenAiCompletions.Streams(),
    });

    public static IProvider CreateCn() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "moonshotai-cn",
        Name = "Moonshot AI CN",
        BaseUrl = "https://api.moonshot.cn/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Moonshot AI API key", new[] { "MOONSHOT_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("moonshotai-cn"),
        Api = OpenAiCompletions.Streams(),
    });
}
