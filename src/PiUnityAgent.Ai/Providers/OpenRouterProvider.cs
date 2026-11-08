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
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "openrouter",
        Name = "OpenRouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("OpenRouter API key", new[] { "OPENROUTER_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("openrouter"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = AnthropicMessages.Streams(),
            ["openai-completions"] = OpenAiCompletions.Streams(),
        },
    });
}
