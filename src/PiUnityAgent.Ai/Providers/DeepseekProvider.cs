using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>DeepSeek profile（pi providers/deepseek.ts）。</summary>
public static class DeepseekProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "deepseek",
        Name = "DeepSeek",
        BaseUrl = "https://api.deepseek.com",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("DeepSeek API key", new[] { "DEEPSEEK_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("deepseek"),
        Api = OpenAiCompletions.Streams(),
    });
}
