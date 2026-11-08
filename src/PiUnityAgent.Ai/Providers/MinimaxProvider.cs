using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>MiniMax 国际/国内双变体（pi providers/minimax.ts 与 minimax-cn.ts；Anthropic 兼容端点）。</summary>
public static class MinimaxProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "minimax",
        Name = "MiniMax",
        BaseUrl = "https://api.minimax.io/anthropic",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("MiniMax API key", new[] { "MINIMAX_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("minimax"),
        Api = AnthropicMessages.Streams(),
    });

    public static IProvider CreateCn() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "minimax-cn",
        Name = "MiniMax CN",
        BaseUrl = "https://api.minimaxi.com/anthropic",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("MiniMax CN API key", new[] { "MINIMAX_CN_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("minimax-cn"),
        Api = AnthropicMessages.Streams(),
    });
}
