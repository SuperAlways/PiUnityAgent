using System.Collections.Generic;
using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>
/// Fireworks AI profile（pi providers/fireworks.ts）：同一网关同时暴露
/// anthropic-messages 与 openai-completions 两套协议，按 model.Api 分派。
/// </summary>
public static class FireworksProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "fireworks",
        Name = "Fireworks",
        BaseUrl = "https://api.fireworks.ai/inference",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Fireworks API key", new[] { "FIREWORKS_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("fireworks"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = AnthropicMessages.Streams(),
            ["openai-completions"] = OpenAiCompletions.Streams(),
        },
    });
}
