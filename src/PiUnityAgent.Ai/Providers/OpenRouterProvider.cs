using System.Collections.Generic;
using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>
/// OpenRouter profile（pi providers/openrouter.ts 的 api-key 面）：同一网关同时暴露
/// anthropic-messages 与 openai-completions 两套协议，按 model.Api 分派。
/// 后置项（spec §10）：OAuth（Task 6/D1）；openrouter-images（D4）与 typesafe-system-one
/// 分类（D5）；按 <c>GET /models/user</c> 做目录个性化（C 偏离，Task 7 审计登记）。
/// 归属/身份头不设在 provider 级——走应用级 <c>ClientIdentity</c>。
/// </summary>
public static class OpenRouterProvider
{
    /// <summary>
    /// <paramref name="oauthFetch"/> 非空时挂 OpenRouter OAuth（manual code → 永久 key）——经
    /// <see cref="AuthHelpers.LazyOAuth"/> 对齐 pi 的 loadOpenRouterOAuth 形状；为空保持
    /// API-key-only（spec §10：loopback 回调 C 偏离、按用户做目录个性化另案）。
    /// </summary>
    public static IProvider Create(Types.FetchFunction? oauthFetch = null) => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "openrouter",
        Name = "OpenRouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("OpenRouter API key", new[] { "OPENROUTER_API_KEY" }),
            OAuth = oauthFetch is null
                ? null
                : AuthHelpers.LazyOAuth(
                    "OpenRouter OAuth",
                    () => System.Threading.Tasks.Task.FromResult<OAuthAuth>(new OpenRouterOAuth(oauthFetch).ToOAuthAuth()),
                    isSubscription: false,
                    loginLabel: "Sign in with OpenRouter"),
        },
        Models = ModelCatalog.GetChatModels("openrouter"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = AnthropicMessages.Streams(),
            ["openai-completions"] = OpenAiCompletions.Streams(),
        },
    });
}
