using System.Reflection;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class ModelsAuthTests
{
    private static ApiKeyAuth Resolver(string? key)
        => new()
        {
            Name = "resolver",
            Resolve = (_, _) => Task.FromResult<AuthResult?>(
                new AuthResult(new ModelAuth { ApiKey = key }, null, "resolver")),
        };

    [Fact]
    public async Task Missing_Auth_Becomes_Error_On_Stream()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth { ApiKey = new ApiKeyAuth
            {
                Name = "test",
                Resolve = (_, _) => Task.FromResult<AuthResult?>(null),
            }
            }));

        var stream = models.Stream(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() });
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        // pi models.ts applyAuth 的未配置文案。
        Assert.Contains("Provider is not configured", done.ErrorMessage);
    }

    [Fact]
    public async Task Stream_Request_Env_Is_Visible_To_Provider_Resolve()
    {
        string? appliedKey = null;
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "env",
                    Resolve = async (input, ct) => new AuthResult(
                        new ModelAuth { ApiKey = await input.Context.EnvAsync("REQ_KEY", ct) },
                        null,
                        "env"),
                },
            },
            stream: (model, context, options) =>
            {
                appliedKey = options?.ApiKey;
                return ProviderTestSupport.DoneStream(model, context, options);
            }));

        // pi applyAuth 把请求级 env 传进 getAuth：resolver 解析期即可见。
        var stream = models.Stream(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() },
            new StreamOptions { Env = new Dictionary<string, string> { ["REQ_KEY"] = "request-env-key" } });
        await ProviderTestSupport.CollectAsync(stream);

        Assert.Equal("request-env-key", appliedKey);
    }

    [Fact]
    public async Task Stream_Explicit_Empty_Request_ApiKey_Wins()
    {
        string? appliedKey = null;
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth { ApiKey = Resolver("resolved-key") },
            stream: (model, context, options) =>
            {
                appliedKey = options?.ApiKey;
                return ProviderTestSupport.DoneStream(model, context, options);
            }));

        // pi：options.apiKey ?? auth.apiKey——空串是显式值，胜过解析结果。
        var stream = models.Stream(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() },
            new StreamOptions { ApiKey = "" });
        await ProviderTestSupport.CollectAsync(stream);

        Assert.Equal(string.Empty, appliedKey);
    }

    [Fact]
    public async Task GetAuth_Stored_OAuth_Wins_Over_Ambient_ApiKey()
    {
        var store = new InMemoryCredentialStore();
        var oauthCredential = new OAuthCredential
        {
            RefreshToken = "r",
            AccessToken = "oauth-key",
            ExpiresAt = long.MaxValue,
        };
        await store.ModifyAsync("p1", _ => Task.FromResult<Credential?>(oauthCredential));
        var models = new Models(new CreateModelsOptions { Credentials = store });
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "ambient",
                    Resolve = (_, _) => Task.FromResult<AuthResult?>(
                        new AuthResult(new ModelAuth { ApiKey = "ambient-key" }, null, "ambient")),
                },
                OAuth = new OAuthAuth
                {
                    Name = "oauth",
                    Login = (_, _, _) => Task.FromResult(oauthCredential),
                    Refresh = (credential, _) => Task.FromResult(credential),
                    ToAuth = (credential, _) => Task.FromResult(new ModelAuth { ApiKey = credential.AccessToken }),
                },
            }));

        var result = await models.GetAuthAsync("p1");

        Assert.NotNull(result);
        Assert.Equal("oauth-key", result!.Auth.ApiKey);
        Assert.Equal("OAuth", result.Source);
    }

    [Fact]
    public async Task GetAuth_Override_ApiKey_Passes_Env_To_Provider()
    {
        IReadOnlyDictionary<string, string>? seenEnv = null;
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "test",
                    Resolve = (input, _) =>
                    {
                        seenEnv = input.Credential?.Env;
                        return Task.FromResult<AuthResult?>(
                            new AuthResult(
                                new ModelAuth { ApiKey = input.Credential?.Key },
                                input.Credential?.Env,
                                "resolver"));
                    },
                },
            }));

        var result = await models.GetAuthAsync("p1", new AuthResolutionOverrides
        {
            ApiKey = "override",
            Env = new Dictionary<string, string> { ["REGION"] = "cn" },
        });

        Assert.Equal("override", result!.Auth.ApiKey);
        Assert.Equal("cn", seenEnv!["REGION"]);
    }

    [Fact]
    public async Task GetAuth_Explicit_Empty_ApiKey_Override_Goes_Through_Resolve()
    {
        string? seenKey = null;
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "test",
                    Resolve = (input, _) =>
                    {
                        seenKey = input.Credential?.Key;
                        return Task.FromResult<AuthResult?>(
                            new AuthResult(
                                new ModelAuth { ApiKey = input.Credential?.Key },
                                input.Credential?.Env,
                                "resolver"));
                    },
                },
            }));

        // pi：overrides.apiKey !== undefined——空串是显式 override，仍走 provider Resolve。
        var result = await models.GetAuthAsync("p1", new AuthResolutionOverrides { ApiKey = "" });

        Assert.Equal(string.Empty, seenKey);
        Assert.Equal(string.Empty, result!.Auth.ApiKey);
    }

    [Fact]
    public async Task GetAuth_Override_Env_Empty_Value_Falls_Back_To_Base()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p1", _ => Task.FromResult<Credential?>(
            new ApiKeyCredential("stored-key")));
        var models = new Models(new CreateModelsOptions
        {
            Credentials = store,
            AuthContext = new StaticEnvAuthContext(
                new Dictionary<string, string> { ["REGION"] = "base-region" }),
        });
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "env",
                    Resolve = async (input, ct) => new AuthResult(
                        new ModelAuth { ApiKey = await input.Context.EnvAsync("REGION", ct) },
                        null,
                        "env"),
                },
            }));

        // pi overlayEnvAuthContext：env[name] || base.env(name)——override 空串回落 base。
        var result = await models.GetAuthAsync("p1", new AuthResolutionOverrides
        {
            Env = new Dictionary<string, string> { ["REGION"] = "" },
        });

        Assert.Equal("base-region", result!.Auth.ApiKey);
    }

    [Fact]
    public async Task GetAuth_Model_Headers_Are_Merged()
    {
        var model = ProviderTestSupport.CreateModel() with
        {
            Headers = new Dictionary<string, string> { ["x-model"] = "m" },
        };
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "test",
                    Resolve = (_, _) => Task.FromResult<AuthResult?>(
                        new AuthResult(
                            new ModelAuth
                            {
                                Headers = new Dictionary<string, string?> { ["x-auth"] = "a" },
                            },
                            null,
                            "resolver")),
                },
            }));

        var result = await models.GetAuthAsync(model);

        Assert.Equal("m", result!.Auth.Headers!["x-model"]);
        Assert.Equal("a", result.Auth.Headers["x-auth"]);
    }

    [Fact]
    public async Task CheckAuth_Stored_OAuth_Returns_OAuth_Type()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p1", _ => Task.FromResult<Credential?>(
            new OAuthCredential { RefreshToken = "r", AccessToken = "a", ExpiresAt = long.MaxValue }));
        var models = new Models(new CreateModelsOptions { Credentials = store });
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "ambient",
                    Resolve = (_, _) => Task.FromResult<AuthResult?>(
                        new AuthResult(new ModelAuth { ApiKey = "ambient" }, null, "ambient")),
                },
                OAuth = new OAuthAuth
                {
                    Name = "oauth",
                    Login = (_, _, _) => Task.FromResult(new OAuthCredential
                    {
                        RefreshToken = "r",
                        AccessToken = "a",
                        ExpiresAt = 1,
                    }),
                    Refresh = (credential, _) => Task.FromResult(credential),
                    ToAuth = (credential, _) => Task.FromResult(new ModelAuth { ApiKey = credential.AccessToken }),
                },
            }));

        var check = await models.CheckAuthAsync("p1");

        Assert.Equal(AuthType.OAuth, check!.Type);
    }

    [Fact]
    public async Task GetAuth_Unknown_Provider_Returns_Null()
    {
        var models = new Models();
        Assert.Null(await models.GetAuthAsync("missing"));
    }

    [Fact]
    public async Task Stream_Applies_Resolved_Api_Key()
    {
        string? appliedKey = null;
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth { ApiKey = Resolver("key-1") },
            stream: (model, context, options) =>
            {
                appliedKey = options?.ApiKey;
                return ProviderTestSupport.DoneStream(model, context, options);
            }));

        var stream = models.Stream(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() });
        await ProviderTestSupport.CollectAsync(stream);

        Assert.Equal("key-1", appliedKey);
    }

    [Fact]
    public async Task CheckAuth_And_GetAvailable_Filter_Configured_Providers()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            id: "p1",
            auth: new ProviderAuth { ApiKey = Resolver("key") }));
        models.SetProvider(ProviderTestSupport.CreateProvider(
            id: "p2",
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "missing",
                    Resolve = (_, _) => Task.FromResult<AuthResult?>(null),
                },
            }));

        var check = await models.CheckAuthAsync("p1");
        Assert.NotNull(check);
        Assert.Equal(AuthType.ApiKey, check.Type);
        Assert.Null(await models.CheckAuthAsync("p2"));

        var available = await models.GetAvailableAsync();
        Assert.Single(available);
        Assert.Equal("p1", available[0].Provider);
    }

    [Fact]
    public async Task Keyless_Auth_Provider_Is_Available()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider());
        Assert.True((await models.CheckAuthAsync("p1"))!.Type is AuthType.ApiKey);
        Assert.Single(await models.GetAvailableAsync());
    }

    [Fact]
    public async Task CheckAuth_Check_Is_Authoritative_Null_Does_Not_Fall_Back_To_Resolve()
    {
        var resolveCalled = false;
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p1", _ => Task.FromResult<Credential?>(
            new ApiKeyCredential("stored-key")));
        var models = new Models(new CreateModelsOptions { Credentials = store });
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "checked",
                    Check = (_, _) => Task.FromResult<AuthCheck?>(null),
                    Resolve = (_, _) =>
                    {
                        resolveCalled = true;
                        return Task.FromResult<AuthResult?>(
                            new AuthResult(new ModelAuth { ApiKey = "resolved" }, null, "resolver"));
                    },
                },
            }));

        // Check 存在时它就是权威：返回 null = 未配置，不回落 Resolve。
        var check = await models.CheckAuthAsync("p1");

        Assert.Null(check);
        Assert.False(resolveCalled);
    }

    [Fact]
    public async Task Logout_Deletes_Store_Credential()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p1", _ => Task.FromResult<Credential?>(
            new ApiKeyCredential("store-key")));
        var models = new Models(new CreateModelsOptions { Credentials = store });
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "none",
                    Resolve = (_, _) => Task.FromResult<AuthResult?>(null),
                },
            }));

        await models.LogoutAsync("p1");

        Assert.Null(await models.GetAuthAsync("p1"));
        Assert.Null(await store.ReadAsync("p1"));
    }

    [Fact]
    public async Task Logout_Unknown_Provider_Is_No_Op()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("ghost", _ => Task.FromResult<Credential?>(
            new ApiKeyCredential("ghost-key")));
        var models = new Models(new CreateModelsOptions { Credentials = store });

        // pi logout：只删 store，不校验 provider 是否注册——未存凭据/未注册也是安静删除。
        await models.LogoutAsync("ghost");

        Assert.Null(await store.ReadAsync("ghost"));
    }

    [Fact]
    public void RequireProvider_Throws_ModelsError_With_Provider_Code()
    {
        // 私有方法，公开面只有 LazyStream 终态 Error 消息（与旧 InvalidOperationException 文案相同）；
        // 用反射锁定审计 N1 的关键口径：错误类型是 ModelsError(Provider)，与 pi/pi.net 一致。
        var method = typeof(Models).GetMethod(
            "RequireProvider", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var wrapper = Assert.Throws<TargetInvocationException>(() =>
            method!.Invoke(new Models(), new object[] { "missing" }));
        var error = Assert.IsType<ModelsError>(wrapper.InnerException);

        Assert.Equal(ModelsErrorCode.Provider, error.Code);
        Assert.Contains("Unknown provider: missing", error.Message);
    }

    [Fact]
    public async Task Refresh_Returns_Empty_Result_Without_Throwing()
    {
        var models = new Models();
        var result = await models.RefreshAsync();

        Assert.False(result.Aborted);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Login_Is_Deferred_To_S5()
    {
        var models = new Models();
        var exception = Assert.Throws<NotSupportedException>(() =>
        {
            _ = models.LoginAsync("p1", AuthType.OAuth, new NoOpInteraction());
        });

        Assert.Contains("S5", exception.Message);
        Assert.Contains("D1", exception.Message);
    }

    private sealed class NoOpInteraction : AuthInteraction
    {
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

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
}
