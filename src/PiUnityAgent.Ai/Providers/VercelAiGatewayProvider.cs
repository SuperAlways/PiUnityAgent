using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>
/// Vercel AI Gateway profile（pi providers/vercel-ai-gateway.ts）。
/// TypeSafe System One 分类协议（classifiers）后置（D5，spec §10）。
/// </summary>
public static class VercelAiGatewayProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "vercel-ai-gateway",
        Name = "Vercel AI Gateway",
        BaseUrl = "https://ai-gateway.vercel.sh",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Vercel AI Gateway API key", new[] { "AI_GATEWAY_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("vercel-ai-gateway"),
        Api = AnthropicMessages.Streams(),
    });
}
