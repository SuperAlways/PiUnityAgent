using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Tests.Fakes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

/// <summary>
/// S5 Task 5：7 家 provider profile（Anthropic/MiniMax/MiniMax CN/Google/Fireworks/Vercel AI Gateway/OpenRouter）
/// 与 ModelCatalog 嵌入式模型目录。期望值全部来自 pi TS providers/*.ts 与 pi.net ModelData/*.json，不猜。
/// 范围钉死：仅 api-key 面——OAuth（D1）与图片/分类（D4/D5）不在本任务。
/// </summary>
public class S5ProviderProfileTests
{
    public sealed record ProfileExpectation(
        string Id, string Name, string BaseUrl, string AuthName, string EnvVar);

    public static TheoryData<ProfileExpectation> ProfileVariants() => new()
    {
        new("anthropic", "Anthropic", "https://api.anthropic.com", "Anthropic API key", "ANTHROPIC_API_KEY"),
        new("minimax", "MiniMax", "https://api.minimax.io/anthropic", "MiniMax API key", "MINIMAX_API_KEY"),
        new("minimax-cn", "MiniMax CN", "https://api.minimaxi.com/anthropic", "MiniMax CN API key", "MINIMAX_CN_API_KEY"),
        new("google", "Google", "https://generativelanguage.googleapis.com/v1beta", "Gemini API key", "GEMINI_API_KEY"),
        new("fireworks", "Fireworks", "https://api.fireworks.ai/inference", "Fireworks API key", "FIREWORKS_API_KEY"),
        new("vercel-ai-gateway", "Vercel AI Gateway", "https://ai-gateway.vercel.sh", "Vercel AI Gateway API key", "AI_GATEWAY_API_KEY"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "OpenRouter API key", "OPENROUTER_API_KEY"),
    };

    private static IProvider CreateProfile(string id) => id switch
    {
        "anthropic" => AnthropicProvider.Create(),
        "minimax" => MinimaxProvider.Create(),
        "minimax-cn" => MinimaxProvider.CreateCn(),
        "google" => GoogleProvider.Create(),
        "fireworks" => FireworksProvider.Create(),
        "vercel-ai-gateway" => VercelAiGatewayProvider.Create(),
        "openrouter" => OpenRouterProvider.Create(),
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

        // S5 边界：不设 provider 级 headers（OpenRouter 归属/身份走应用级 ClientIdentity）。
        Assert.Null(provider.Headers);

        var models = provider.GetModels();
        Assert.NotEmpty(models);
        Assert.All(models, model =>
        {
            Assert.Equal(ModelType.Chat, model.Type);
            Assert.False(string.IsNullOrEmpty(model.Id));
            Assert.False(string.IsNullOrEmpty(model.Name));
            Assert.False(string.IsNullOrEmpty(model.Api));
            Assert.False(string.IsNullOrEmpty(model.BaseUrl));
            Assert.Equal(expected.Id, model.Provider);
        });
    }

    [Theory, MemberData(nameof(ProfileVariants))]
    public void Profile_Catalog_Models_Match_Provider(ProfileExpectation expected)
    {
        // Model 是 record 但 Input/Compat 等成员按引用比较——按标量字段投影比较。
        var provider = CreateProfile(expected.Id);
        var catalog = ModelCatalog.GetChatModels(expected.Id);

        Assert.Equal(
            provider.GetModels().Select(model => (
                model.Id, model.Name, model.Api, model.Provider, model.BaseUrl,
                model.Reasoning, model.ContextWindow, model.MaxTokens)),
            catalog.Select(model => (
                model.Id, model.Name, model.Api, model.Provider, model.BaseUrl,
                model.Reasoning, model.ContextWindow, model.MaxTokens)));
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

    // ---------------------------------------------------------------------
    // Anthropic 自定义认证链（pi providers/anthropic.ts anthropicApiKeyAuth）
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Anthropic_Stored_Credential_Wins_And_Carries_Env()
    {
        var auth = AnthropicProvider.Create().Auth.ApiKey!;
        var credentialEnv = new Dictionary<string, string> { ["REGION"] = "cn" };
        var stored = new ApiKeyCredential("stored-key") { Env = credentialEnv };

        var result = await auth.Resolve(
            new ApiKeyResolveInput(new StaticEnvAuthContext(new Dictionary<string, string>
            {
                ["ANTHROPIC_AUTH_TOKEN"] = "tok",
                ["ANTHROPIC_API_KEY"] = "env-key",
            }), stored), default);

        Assert.NotNull(result);
        Assert.Equal("stored-key", result!.Auth.ApiKey);
        Assert.Null(result.Auth.Headers);
        Assert.Equal(credentialEnv, result.Env);
        Assert.Equal("stored credential", result.Source);
    }

    [Fact]
    public async Task Anthropic_Auth_Token_Resolves_As_Bearer_Header()
    {
        var auth = AnthropicProvider.Create().Auth.ApiKey!;

        var result = await auth.Resolve(
            new ApiKeyResolveInput(
                new StaticEnvAuthContext(new Dictionary<string, string>
                {
                    ["ANTHROPIC_AUTH_TOKEN"] = "tok-123",
                }), null), default);

        Assert.NotNull(result);
        Assert.Null(result!.Auth.ApiKey);
        Assert.NotNull(result.Auth.Headers);
        Assert.Equal("Bearer tok-123", result.Auth.Headers!["Authorization"]);
        Assert.Equal("ANTHROPIC_AUTH_TOKEN", result.Source);
    }

    [Fact]
    public async Task Anthropic_Oauth_And_Api_Keys_Resolve_In_Order()
    {
        var auth = AnthropicProvider.Create().Auth.ApiKey!;

        var viaOAuth = await auth.Resolve(
            new ApiKeyResolveInput(
                new StaticEnvAuthContext(new Dictionary<string, string>
                {
                    ["ANTHROPIC_OAUTH_TOKEN"] = "oauth-tok",
                    ["ANTHROPIC_API_KEY"] = "api-key",
                }), null), default);
        Assert.Equal("oauth-tok", viaOAuth!.Auth.ApiKey);
        Assert.Equal("ANTHROPIC_OAUTH_TOKEN", viaOAuth.Source);

        var viaApiKey = await auth.Resolve(
            new ApiKeyResolveInput(
                new StaticEnvAuthContext(new Dictionary<string, string>
                {
                    ["ANTHROPIC_API_KEY"] = "api-key",
                }), null), default);
        Assert.Equal("api-key", viaApiKey!.Auth.ApiKey);
        Assert.Equal("ANTHROPIC_API_KEY", viaApiKey.Source);
        Assert.Null(viaApiKey.Auth.Headers);
    }

    [Fact]
    public async Task Anthropic_Auth_Token_Beats_Oauth_And_Api_Keys()
    {
        var auth = AnthropicProvider.Create().Auth.ApiKey!;

        var result = await auth.Resolve(
            new ApiKeyResolveInput(
                new StaticEnvAuthContext(new Dictionary<string, string>
                {
                    ["ANTHROPIC_AUTH_TOKEN"] = "tok-123",
                    ["ANTHROPIC_OAUTH_TOKEN"] = "oauth-tok",
                    ["ANTHROPIC_API_KEY"] = "api-key",
                }), null), default);

        Assert.Equal("Bearer tok-123", result!.Auth.Headers!["Authorization"]);
        Assert.Equal("ANTHROPIC_AUTH_TOKEN", result.Source);
    }

    [Fact]
    public async Task Anthropic_Empty_Envs_Fall_Through_The_Chain()
    {
        var auth = AnthropicProvider.Create().Auth.ApiKey!;

        // AUTH_TOKEN 为空串 → 落到 OAUTH_TOKEN；OAUTH_TOKEN 为空串 → 落到 API_KEY。
        var result = await auth.Resolve(
            new ApiKeyResolveInput(
                new StaticEnvAuthContext(new Dictionary<string, string>
                {
                    ["ANTHROPIC_AUTH_TOKEN"] = "",
                    ["ANTHROPIC_OAUTH_TOKEN"] = "",
                    ["ANTHROPIC_API_KEY"] = "api-key",
                }), null), default);

        Assert.Equal("api-key", result!.Auth.ApiKey);
        Assert.Equal("ANTHROPIC_API_KEY", result.Source);
    }

    [Fact]
    public async Task Anthropic_Without_Env_Or_Credential_Returns_Null()
    {
        var auth = AnthropicProvider.Create().Auth.ApiKey!;

        var resolved = await auth.Resolve(
            new ApiKeyResolveInput(new StaticEnvAuthContext(new Dictionary<string, string>()), null), default);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task Anthropic_Login_Prompts_Secret()
    {
        var auth = AnthropicProvider.Create().Auth.ApiKey!;
        var interaction = new RecordingInteraction("sk-ant-test");

        var credential = await auth.Login!(new ProviderAuthInteraction(interaction, default), default);

        Assert.Equal("sk-ant-test", credential.Key);
        var prompt = Assert.IsType<AuthPrompt.Secret>(Assert.Single(interaction.Prompts));
        Assert.Equal("Enter Anthropic API key", prompt!.Message);
    }

    // ---------------------------------------------------------------------
    // S5 范围边界：无 OAuth fetch 的 profile 只走 api-key 面（分类/图片后置 D4/D5）
    // ---------------------------------------------------------------------

    [Theory, MemberData(nameof(ProfileVariants))]
    public void Profiles_Without_OAuth_Fetch_Keep_ApiKey_Only(ProfileExpectation expected)
    {
        // CreateProfile() 不传 oauthFetch——Task 6（D1）已给 anthropic/openrouter 接
        // LazyOAuth，但仅在宿主注入 fetch 时挂载（S5AuthIntegrationTests.Profile_With_Fetch_Attaches_Lazy_OAuth）。
        var provider = CreateProfile(expected.Id);
        Assert.Null(provider.Auth.OAuth);
    }

    // ---------------------------------------------------------------------
    // 目录数据（pi.net ModelData 原样拷贝）
    // ---------------------------------------------------------------------

    [Fact]
    public void GetChatModels_Anthropic_Parses_Catalog()
    {
        var models = ModelCatalog.GetChatModels("anthropic");

        Assert.Equal(17, models.Count);
        Assert.All(models, model =>
        {
            Assert.Equal("anthropic-messages", model.Api);
            Assert.Equal("anthropic", model.Provider);
            Assert.Equal("https://api.anthropic.com", model.BaseUrl);
        });
        Assert.Contains(models, model => model.Id == "claude-haiku-5-5");
        Assert.Contains(models, model => model.Id == "claude-opus-4-7");
    }

    [Fact]
    public void GetChatModels_Minimax_Variants_Have_Distinct_BaseUrls()
    {
        var intl = ModelCatalog.GetChatModels("minimax");
        var cn = ModelCatalog.GetChatModels("minimax-cn");

        Assert.Equal(4, intl.Count);
        Assert.Equal(4, cn.Count);
        Assert.All(intl, model =>
        {
            Assert.Equal("https://api.minimax.io/anthropic", model.BaseUrl);
            Assert.Equal("minimax", model.Provider);
        });
        Assert.All(cn, model =>
        {
            Assert.Equal("https://api.minimaxi.com/anthropic", model.BaseUrl);
            Assert.Equal("minimax-cn", model.Provider);
        });
        // 同 id 集、不同 provider/baseUrl。
        Assert.Equal(intl.Select(model => model.Id), cn.Select(model => model.Id));
        Assert.Contains(intl, model => model.Id == "MiniMax-M3");
    }

    [Fact]
    public void GetChatModels_Google_Parses_Catalog()
    {
        var models = ModelCatalog.GetChatModels("google");

        Assert.Equal(23, models.Count);
        Assert.All(models, model =>
        {
            Assert.Equal("google-generative-ai", model.Api);
            Assert.Equal("google", model.Provider);
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta", model.BaseUrl);
        });
        Assert.Contains(models, model => model.Id == "gemini-2.5-flash");
        Assert.Contains(models, model => model.Id == "gemini-3-flash-preview");
    }

    [Fact]
    public void GetChatModels_Fireworks_Covers_Both_Protocols()
    {
        var models = ModelCatalog.GetChatModels("fireworks");

        // pi.net fireworks.json：anthropic-messages 19 + openai-completions 10。
        Assert.Equal(19, models.Count(model => model.Api == "anthropic-messages"));
        Assert.Equal(10, models.Count(model => model.Api == "openai-completions"));
        Assert.All(models, model => Assert.Equal("fireworks", model.Provider));
        // openai-completions 组自带 /v1 后缀 baseUrl（数据如此，照搬不圆）。
        Assert.All(
            models.Where(model => model.Api == "anthropic-messages"),
            model => Assert.Equal("https://api.fireworks.ai/inference", model.BaseUrl));
        Assert.All(
            models.Where(model => model.Api == "openai-completions"),
            model => Assert.Equal("https://api.fireworks.ai/inference/v1", model.BaseUrl));
    }

    [Fact]
    public void GetChatModels_Vercel_Chat_Only_No_Classifier()
    {
        var models = ModelCatalog.GetChatModels("vercel-ai-gateway");

        // vercel-ai-gateway.json 有 302 chat + 14 classifier；分类器组不进 chat 目录（D5 后置）。
        Assert.Equal(302, models.Count);
        Assert.All(models, model =>
        {
            Assert.Equal("anthropic-messages", model.Api);
            Assert.Equal("vercel-ai-gateway", model.Provider);
            Assert.Equal("https://ai-gateway.vercel.sh", model.BaseUrl);
        });
        Assert.DoesNotContain(models, model => model.Api == "typesafe-system-one");
    }

    [Fact]
    public void GetChatModels_OpenRouter_Covers_Both_Protocols_No_Image_Or_Classifier()
    {
        var models = ModelCatalog.GetChatModels("openrouter");

        // pi.net openrouter.json：anthropic-messages 19 + openai-completions 370；
        // image（63）与 classifier（33）组不进 chat 目录（D4/D5 后置）。
        Assert.Equal(389, models.Count);
        Assert.Equal(19, models.Count(model => model.Api == "anthropic-messages"));
        Assert.Equal(370, models.Count(model => model.Api == "openai-completions"));
        Assert.DoesNotContain(models, model => model.Api == "openrouter-images");
        Assert.DoesNotContain(models, model => model.Api == "typesafe-system-one");
        // anthropic 组自带不带 /v1 的 baseUrl（数据如此，照搬不圆）。
        Assert.All(
            models.Where(model => model.Api == "anthropic-messages"),
            model => Assert.Equal("https://openrouter.ai/api", model.BaseUrl));
        Assert.All(
            models.Where(model => model.Api == "openai-completions"),
            model => Assert.Equal("https://openrouter.ai/api/v1", model.BaseUrl));
    }

    // ---------------------------------------------------------------------
    // 双协议 ApiByApi 分派（Fireworks / OpenRouter）
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Fireworks_ApiByApi_Dispatches_Requests_By_Model_Api()
    {
        // anthropic-messages 走 /v1/messages + x-api-key；openai-completions 走 /chat/completions + Bearer。
        var fetch = new MockFetch();
        fetch.EnqueueText(
            AnthropicSse(MessageStart(), MessageDelta("end_turn"), MessageStop()),
            headers: SseHeaders());
        fetch.EnqueueText(
            Sse(ChoiceChunk("{}", finishReason: "stop")),
            headers: SseHeaders());

        var models = new Models(new CreateModelsOptions
        {
            AuthContext = new StaticEnvAuthContext(new Dictionary<string, string>
            {
                ["FIREWORKS_API_KEY"] = "fw-key",
            }),
        });
        models.SetProvider(FireworksProvider.Create());

        var anthropicModel = models.GetModels("fireworks")
            .First(model => model.Api == "anthropic-messages");
        var completionsModel = models.GetModels("fireworks")
            .First(model => model.Api == "openai-completions");

        // 门面按协议 options 类型透传（AnthropicOptions/OpenAiCompletionsOptions 继承 StreamOptions）。
        _ = await CollectAsync(models.Stream(
            anthropicModel, new Context { Messages = Array.Empty<Message>() },
            new AnthropicOptions { Fetch = fetch.Fetch }));
        var second = await CollectAsync(models.Stream(
            completionsModel, new Context { Messages = Array.Empty<Message>() },
            new OpenAiCompletionsOptions { Fetch = fetch.Fetch }));
        Assert.Equal(2, fetch.Requests.Count);
        Assert.EndsWith("/v1/messages", fetch.Requests[0].Url);
        Assert.Equal("fw-key", fetch.Requests[0].Headers["x-api-key"]);
        Assert.EndsWith("/chat/completions", fetch.Requests[1].Url);
        Assert.Equal("Bearer fw-key", fetch.Requests[1].Headers["authorization"]);
    }

    [Fact]
    public async Task OpenRouter_ApiByApi_Dispatches_Requests_By_Model_Api()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            AnthropicSse(MessageStart(), MessageDelta("end_turn"), MessageStop()),
            headers: SseHeaders());
        fetch.EnqueueText(
            Sse(ChoiceChunk("{}", finishReason: "stop")),
            headers: SseHeaders());

        var models = new Models(new CreateModelsOptions
        {
            AuthContext = new StaticEnvAuthContext(new Dictionary<string, string>
            {
                ["OPENROUTER_API_KEY"] = "or-key",
            }),
        });
        models.SetProvider(OpenRouterProvider.Create());

        var anthropicModel = models.GetModels("openrouter")
            .First(model => model.Api == "anthropic-messages");
        var completionsModel = models.GetModels("openrouter")
            .First(model => model.Api == "openai-completions");

        await CollectAsync(models.Stream(
            anthropicModel, new Context { Messages = Array.Empty<Message>() },
            new AnthropicOptions { Fetch = fetch.Fetch }));
        await CollectAsync(models.Stream(
            completionsModel, new Context { Messages = Array.Empty<Message>() },
            new OpenAiCompletionsOptions { Fetch = fetch.Fetch }));

        Assert.Equal(2, fetch.Requests.Count);
        // anthropic 组自带不带 /v1 的 baseUrl（pi.net 数据如此）。
        Assert.EndsWith("/v1/messages", fetch.Requests[0].Url);
        Assert.StartsWith("https://openrouter.ai/api", fetch.Requests[0].Url);
        Assert.Equal("Bearer or-key", fetch.Requests[1].Headers["authorization"]);
        Assert.StartsWith("https://openrouter.ai/api/v1", fetch.Requests[1].Url);
        Assert.EndsWith("/chat/completions", fetch.Requests[1].Url);
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

        // 7 家 profile 各有模型进入可用列表（Provider 取 JSON 内的 provider 字段）。
        var providers = available.Select(model => model.Provider).ToHashSet(StringComparer.Ordinal);
        var expectedProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expected in ProfileVariants())
        {
            expectedProviders.Add(expected.Id);
        }
        Assert.Equal(expectedProviders, providers);
    }

    private sealed class RecordingInteraction(string secretReply) : AuthInteraction
    {
        public List<AuthPrompt> Prompts { get; } = new();

        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(secretReply);
        }

        public void Notify(AuthEvent @event)
        {
        }
    }

    private sealed class StaticEnvAuthContext(IReadOnlyDictionary<string, string> env) : IAuthContext
    {
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(env.TryGetValue(name, out var value) ? value : null);

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    // ------------------------------------------------------------------
    // 双协议分派测试的最小 wire 件（与 Api/Adapter 测试同构；完整流断言归 Task 3/4）
    // ------------------------------------------------------------------

    private static async Task<List<AssistantMessageEvent>> CollectAsync(
        IAssistantMessageEventStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            events.Add(@event);
        }
        return events;
    }

    private static IReadOnlyDictionary<string, string?> SseHeaders()
        => new Dictionary<string, string?> { ["content-type"] = "text/event-stream" };

    private static string Sse(params string[] dataPayloads)
        => string.Concat(dataPayloads.Select(payload => $"data: {payload}\n\n"));

    private static string AnthropicSse(params string[] events) => string.Concat(events);

    private static string SseNamed(string eventName, JsonObject data)
        => $"event: {eventName}\ndata: {data.ToJsonString()}\n\n";

    private static string MessageStart() => SseNamed("message_start", new JsonObject
    {
        ["type"] = "message_start",
        ["message"] = new JsonObject
        {
            ["id"] = "msg_1",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-test",
            ["usage"] = new JsonObject { ["input_tokens"] = 4, ["output_tokens"] = 1 },
        },
    });

    private static string MessageDelta(string? stopReason)
    {
        var delta = new JsonObject();
        if (stopReason is not null) delta["stop_reason"] = stopReason;
        return SseNamed("message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = delta });
    }

    private static string MessageStop()
        => SseNamed("message_stop", new JsonObject { ["type"] = "message_stop" });

    private static string ChoiceChunk(string deltaJson, string? finishReason = null)
    {
        var choice = new JsonObject
        {
            ["index"] = 0,
            ["delta"] = JsonNode.Parse(deltaJson),
        };
        if (finishReason is not null) choice["finish_reason"] = finishReason;
        return Sse(new JsonObject { ["choices"] = new JsonArray(choice) }.ToJsonString());
    }
}
