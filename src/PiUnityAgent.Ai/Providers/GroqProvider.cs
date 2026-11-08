using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>Groq profile（pi providers/groq.ts）。</summary>
public static class GroqProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "groq",
        Name = "Groq",
        BaseUrl = "https://api.groq.com/openai/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Groq API key", new[] { "GROQ_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("groq"),
        Api = OpenAiCompletions.Streams(),
    });
}
