using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>Google Gemini profile（pi providers/google.ts；Generative Language API v1beta）。</summary>
public static class GoogleProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "google",
        Name = "Google",
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Gemini API key", new[] { "GEMINI_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("google"),
        Api = GoogleGenerativeAi.Streams(),
    });
}
