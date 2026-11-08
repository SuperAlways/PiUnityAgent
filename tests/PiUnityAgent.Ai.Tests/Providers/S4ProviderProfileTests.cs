using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

/// <summary>
/// S4 Task 7：8 家 provider profile（12 个变体）与 ModelCatalog 嵌入式模型目录。
/// 期望值全部来自 pi TS providers/*.ts 与 pi.net ModelData/*.json，不猜。
/// </summary>
public class S4ProviderProfileTests
{
    public sealed record ProfileExpectation(
        string Id, string Name, string BaseUrl, string AuthName, string EnvVar);

    public static TheoryData<ProfileExpectation> ProfileVariants() => new()
    {
        new("openai", "OpenAI", "https://api.openai.com/v1", "OpenAI API key", "OPENAI_API_KEY"),
        new("xai", "xAI", "https://api.x.ai/v1", "xAI API key", "XAI_API_KEY"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "DeepSeek API key", "DEEPSEEK_API_KEY"),
        new("moonshotai", "Moonshot AI", "https://api.moonshot.ai/v1", "Moonshot AI API key", "MOONSHOT_API_KEY"),
        new("moonshotai-cn", "Moonshot AI CN", "https://api.moonshot.cn/v1", "Moonshot AI API key", "MOONSHOT_API_KEY"),
        new("zai", "Z.AI", "https://api.z.ai/api/coding/paas/v4", "Z.AI API key", "ZAI_API_KEY"),
        new("zai-coding-cn", "Z.AI Coding CN", "https://open.bigmodel.cn/api/coding/paas/v4", "Z.AI Coding CN API key", "ZAI_CODING_CN_API_KEY"),
        new("qwen-token-plan", "Qwen Token Plan", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", "Qwen Token Plan API key", "QWEN_TOKEN_PLAN_API_KEY"),
        new("qwen-token-plan-cn", "Qwen Token Plan CN", "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", "Qwen Token Plan CN API key", "QWEN_TOKEN_PLAN_CN_API_KEY"),
        new("qwen-token-plan-individual", "Qwen Token Plan Individual", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", "Qwen Token Plan Individual API key", "QWEN_TOKEN_PLAN_API_KEY"),
        new("xiaomi", "Xiaomi", "https://api.xiaomimimo.com/v1", "Xiaomi API key", "XIAOMI_API_KEY"),
        new("groq", "Groq", "https://api.groq.com/openai/v1", "Groq API key", "GROQ_API_KEY"),
    };

    private static IProvider CreateProfile(string id) => id switch
    {
        "openai" => OpenAiProvider.Create(),
        "xai" => XaiProvider.Create(),
        "deepseek" => DeepseekProvider.Create(),
        "moonshotai" => MoonshotProvider.Create(),
        "moonshotai-cn" => MoonshotProvider.CreateCn(),
        "zai" => ZaiProvider.Create(),
        "zai-coding-cn" => ZaiProvider.CreateCodingCn(),
        "qwen-token-plan" => QwenTokenPlanProvider.Create(),
        "qwen-token-plan-cn" => QwenTokenPlanProvider.CreateCn(),
        "qwen-token-plan-individual" => QwenTokenPlanProvider.CreateIndividual(),
        "xiaomi" => XiaomiProvider.Create(),
        "groq" => GroqProvider.Create(),
        _ => throw new ArgumentException($"no profile factory for: {id}"),
    };

    [Theory, MemberData(nameof(ProfileVariants))]
    public void Profile_Has_Expected_Identity(ProfileExpectation expected)
    {
        var provider = CreateProfile(expected.Id);

        Assert.Equal(expected.Id, provider.Id);
        Assert.Equal(expected.Name, provider.Name);
        Assert.Equal(expected.BaseUrl, provider.BaseUrl);
        Assert.NotNull(provider.Auth.ApiKey);
        Assert.Equal(expected.AuthName, provider.Auth.ApiKey!.Name);

        var models = provider.GetModels();
        Assert.NotEmpty(models);
        Assert.All(models, model =>
        {
            Assert.Equal(ModelType.Chat, model.Type);
            Assert.False(string.IsNullOrEmpty(model.Id));
            Assert.False(string.IsNullOrEmpty(model.Name));
            Assert.False(string.IsNullOrEmpty(model.Api));
            Assert.False(string.IsNullOrEmpty(model.BaseUrl));
        });
    }

    [Theory, MemberData(nameof(ProfileVariants))]
    public void Profile_Catalog_Models_Match_Provider_Identity(ProfileExpectation expected)
    {
        // pi 约定：Model.Provider 以 JSON 内的 provider 字段为准；baseUrl 与 profile 同源。
        // 注：Model 是 record 但 Input/Compat 等成员按引用比较，两次解析的实例不相等——按标量字段投影比较。
        var provider = CreateProfile(expected.Id);
        var catalog = ModelCatalog.GetChatModels(expected.Id);

        Assert.Equal(
            provider.GetModels().Select(model => (
                model.Id, model.Name, model.Api, model.Provider, model.BaseUrl,
                model.Reasoning, model.ContextWindow, model.MaxTokens)),
            catalog.Select(model => (
                model.Id, model.Name, model.Api, model.Provider, model.BaseUrl,
                model.Reasoning, model.ContextWindow, model.MaxTokens)));
        Assert.All(catalog, model =>
        {
            Assert.Equal(expected.Id, model.Provider);
            Assert.Equal(expected.BaseUrl, model.BaseUrl);
        });
    }

    [Theory, MemberData(nameof(ProfileVariants))]
    public async Task Profile_Env_Key_Resolves_Auth(ProfileExpectation expected)
    {
        var models = new Models(new CreateModelsOptions
        {
            AuthContext = new StaticEnvAuthContext(new Dictionary<string, string>
            {
                [expected.EnvVar] = "test-key",
            }),
        });
        models.SetProvider(CreateProfile(expected.Id));

        var auth = await models.GetAuthAsync(expected.Id);

        Assert.NotNull(auth);
        Assert.Equal("test-key", auth!.Auth.ApiKey);
        Assert.Equal(expected.EnvVar, auth.Source);
    }

    [Theory, MemberData(nameof(ProfileVariants))]
    public void Model_Data_Resource_Is_Embedded(ProfileExpectation expected)
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(
            $"PiUnityAgent.Ai.Providers.ModelData.{expected.Id}.json");
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task Profiles_Are_Unavailable_Without_Env_Or_Credential()
    {
        var models = new Models(new CreateModelsOptions
        {
            AuthContext = new StaticEnvAuthContext(new Dictionary<string, string>()),
        });
        foreach (var expected in ProfileVariants())
        {
            models.SetProvider(CreateProfile(expected.Id));
        }

        // 未配置任何 env key / 凭据：全部不在可用列表。
        Assert.Empty(await models.GetAvailableAsync());
    }

    [Fact]
    public async Task Profiles_Are_Available_When_Env_Keys_Configured()
    {
        var env = new Dictionary<string, string>();
        foreach (var expected in ProfileVariants())
        {
            env[expected.EnvVar] = "key-" + expected.Id;
        }
        var models = new Models(new CreateModelsOptions
        {
            AuthContext = new StaticEnvAuthContext(env),
        });
        foreach (var expected in ProfileVariants())
        {
            models.SetProvider(CreateProfile(expected.Id));
        }

        var available = await models.GetAvailableAsync();

        Assert.NotEmpty(available);
        Assert.All(available, model => Assert.Equal(ModelType.Chat, model.Type));

        // 12 个变体各有模型进入可用列表（Provider 取 JSON 内的 provider 字段）。
        var providers = available.Select(model => model.Provider).ToHashSet(StringComparer.Ordinal);
        var expectedProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expected in ProfileVariants())
        {
            expectedProviders.Add(expected.Id);
        }
        Assert.Equal(expectedProviders, providers);
    }

    [Fact]
    public void GetChatModels_Parses_DeepSeek_Model_Fields()
    {
        var models = ModelCatalog.GetChatModels("deepseek");

        Assert.Equal(2, models.Count);
        var flash = models.Single(model => model.Id == "deepseek-flash");
        Assert.Equal("DeepSeek V4.1 Flash", flash.Name);
        Assert.Equal("openai-completions", flash.Api);
        Assert.Equal("deepseek", flash.Provider);
        Assert.Equal("https://api.deepseek.com", flash.BaseUrl);
        Assert.True(flash.Reasoning);
        Assert.Equal(1000000, flash.ContextWindow);
        Assert.Equal(384000, flash.MaxTokens);

        Assert.Equal(0.3, flash.Cost.Input);
        Assert.Equal(1.2, flash.Cost.Output);
        Assert.Equal(0.006, flash.Cost.CacheRead);
        Assert.Equal(0, flash.Cost.CacheWrite);

        Assert.Equal(ModelInputModality.Text, flash.Input[0]);
        Assert.Contains(ModelInputModality.Image, flash.Input);

        Assert.Equal("low", flash.ThinkingLevelMap![ModelThinkingLevel.Low]);
        Assert.Equal("high", flash.ThinkingLevelMap[ModelThinkingLevel.High]);
        Assert.Equal("max", flash.ThinkingLevelMap[ModelThinkingLevel.Max]);
        // pi 语义：显式 null = 该档位禁用。
        Assert.Null(flash.ThinkingLevelMap[ModelThinkingLevel.Minimal]);
        Assert.Null(flash.ThinkingLevelMap[ModelThinkingLevel.Medium]);

        Assert.Equal("max_tokens", flash.Compat!["maxTokensField"]!.GetValue<string>());
        Assert.False(flash.Compat["supportsStore"]!.GetValue<bool>());
        Assert.True(flash.Compat["requiresReasoningContentOnAssistantMessages"]!.GetValue<bool>());

        var resize = flash.InputLimits!.Images!.Resize!;
        Assert.Equal(2000, resize.MaxWidth);
        Assert.Equal(2000, resize.MaxHeight);
        Assert.Equal(4718592, resize.MaxBytes);
        Assert.Equal(80, resize.JpegQuality);
    }

    [Fact]
    public void GetChatModels_Parses_Cost_Tiers()
    {
        var models = ModelCatalog.GetChatModels("xai");

        var grok = models.Single(model => model.Id == "grok-4.6");
        Assert.Equal(2, grok.Cost.Input);
        Assert.Equal(6, grok.Cost.Output);
        var tier = Assert.Single(grok.Cost.Tiers!);
        Assert.Equal(200000, tier.InputTokensAbove);
        Assert.Equal(4, tier.Input);
        Assert.Equal(12, tier.Output);
        Assert.Equal(1, tier.CacheRead);
        Assert.Equal(0, tier.CacheWrite);
    }

    [Fact]
    public void GetChatModels_OpenAI_Returns_Only_Chat_Entries()
    {
        var models = ModelCatalog.GetChatModels("openai");

        // openai.json 的 openai-decisions 分类器组不进 chat 目录（44 chat vs 1 classifier）。
        Assert.Equal(44, models.Count);
        Assert.All(models, model =>
        {
            Assert.Equal(ModelType.Chat, model.Type);
            Assert.Equal("openai-responses", model.Api);
            Assert.Equal("openai", model.Provider);
            Assert.Equal("https://api.openai.com/v1", model.BaseUrl);
        });
    }

    [Fact]
    public void GetChatModels_Qwen_Individual_Uses_Json_Provider_And_BaseUrl()
    {
        var models = ModelCatalog.GetChatModels("qwen-token-plan-individual");

        Assert.Equal(9, models.Count);
        Assert.Contains(models, model => model.Id == "qwen3.8-max");
        Assert.All(models, model =>
        {
            Assert.Equal("qwen-token-plan-individual", model.Provider);
            Assert.Equal("https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", model.BaseUrl);
        });
    }

    [Fact]
    public void GetChatModels_Unknown_Provider_Returns_Empty()
    {
        // 缺资源返回空列表，不抛。
        Assert.Empty(ModelCatalog.GetChatModels("no-such-provider"));
        Assert.Empty(ModelCatalog.GetChatModels(""));
    }

    [Fact]
    public void Generic_OpenAi_Compatible_Recipe_Can_Be_Created()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "generic-openai-compatible",
            Name = "Generic OpenAI-compatible",
            BaseUrl = "http://localhost:9/v1",
            Auth = new ProviderAuth
            {
                ApiKey = AuthHelpers.EnvApiKeyAuth("Generic API key", new[] { "GENERIC_API_KEY" }),
            },
            Models = ModelCatalog.GetChatModels("groq"),
            Api = OpenAiCompletions.Streams(),
        });

        Assert.Equal("generic-openai-compatible", provider.Id);
        Assert.NotEmpty(provider.GetModels());
        Assert.All(provider.GetModels(), model => Assert.Equal("groq", model.Provider));
    }

    [Fact]
    public void Generic_ApiByApi_Recipe_Dispatches_By_Model_Api()
    {
        var models = new List<Model>();
        models.AddRange(ModelCatalog.GetChatModels("openai"));
        models.AddRange(ModelCatalog.GetChatModels("groq"));

        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "generic-multi",
            Name = "Generic multi-protocol",
            BaseUrl = "http://localhost:9",
            Auth = new ProviderAuth
            {
                ApiKey = AuthHelpers.EnvApiKeyAuth("Generic API key", new[] { "GENERIC_API_KEY" }),
            },
            Models = models,
            ApiByApi = new Dictionary<string, ProviderStreams>
            {
                ["openai-responses"] = OpenAiResponses.Streams(),
                ["openai-completions"] = OpenAiCompletions.Streams(),
            },
        });

        Assert.Equal(44 + 7, provider.GetAllModels().Count);
        Assert.Equal(44, provider.GetModels().Count(model => model.Api == "openai-responses"));
        Assert.Equal(7, provider.GetModels().Count(model => model.Api == "openai-completions"));
    }

    private sealed class StaticEnvAuthContext(IReadOnlyDictionary<string, string> env) : IAuthContext
    {
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(env.TryGetValue(name, out var value) ? value : null);

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
