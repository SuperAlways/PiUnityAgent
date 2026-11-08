using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>OpenAI profile（pi providers/openai.ts 的 api-key 面；OAuth 订阅登录不在本批）。</summary>
public static class OpenAiProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "openai",
        Name = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("OpenAI API key", new[] { "OPENAI_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("openai"),
        Api = OpenAiResponses.Streams(),
    });
}
