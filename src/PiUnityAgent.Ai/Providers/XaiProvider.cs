using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>xAI profile（pi providers/xai.ts 的 api-key 面；OAuth 订阅登录不在本批）。</summary>
public static class XaiProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "xai",
        Name = "xAI",
        BaseUrl = "https://api.x.ai/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("xAI API key", new[] { "XAI_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("xai"),
        Api = OpenAiResponses.Streams(),
    });
}
