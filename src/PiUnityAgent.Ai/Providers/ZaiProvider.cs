using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>Z.AI 国际 Coding Plan/国内 Coding Plan 双变体（pi providers/zai.ts 与 zai-coding-cn.ts）。</summary>
public static class ZaiProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "zai",
        Name = "Z.AI",
        BaseUrl = "https://api.z.ai/api/coding/paas/v4",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Z.AI API key", new[] { "ZAI_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("zai"),
        Api = OpenAiCompletions.Streams(),
    });

    public static IProvider CreateCodingCn() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "zai-coding-cn",
        Name = "Z.AI Coding CN",
        BaseUrl = "https://open.bigmodel.cn/api/coding/paas/v4",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Z.AI Coding CN API key", new[] { "ZAI_CODING_CN_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("zai-coding-cn"),
        Api = OpenAiCompletions.Streams(),
    });
}
