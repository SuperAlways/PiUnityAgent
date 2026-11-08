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
        Assert.Contains("Authentication not configured", done.ErrorMessage);
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
}
