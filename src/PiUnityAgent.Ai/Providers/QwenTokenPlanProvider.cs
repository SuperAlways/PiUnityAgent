using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>
/// Qwen Token Plan 三变体（pi providers/qwen-token-plan*.ts）：国际/国内/Individual。
/// Individual 与国际版共用 baseUrl 与 QWEN_TOKEN_PLAN_API_KEY，仅模型目录不同。
/// </summary>
public static class QwenTokenPlanProvider
{
    public static IProvider Create() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "qwen-token-plan",
        Name = "Qwen Token Plan",
        BaseUrl = "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Qwen Token Plan API key", new[] { "QWEN_TOKEN_PLAN_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("qwen-token-plan"),
        Api = OpenAiCompletions.Streams(),
    });

    public static IProvider CreateCn() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "qwen-token-plan-cn",
        Name = "Qwen Token Plan CN",
        BaseUrl = "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Qwen Token Plan CN API key", new[] { "QWEN_TOKEN_PLAN_CN_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("qwen-token-plan-cn"),
        Api = OpenAiCompletions.Streams(),
    });

    public static IProvider CreateIndividual() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "qwen-token-plan-individual",
        Name = "Qwen Token Plan Individual",
        BaseUrl = "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1",
        Auth = new ProviderAuth
        {
            ApiKey = AuthHelpers.EnvApiKeyAuth("Qwen Token Plan Individual API key", new[] { "QWEN_TOKEN_PLAN_API_KEY" }),
        },
        Models = ModelCatalog.GetChatModels("qwen-token-plan-individual"),
        Api = OpenAiCompletions.Streams(),
    });
}
