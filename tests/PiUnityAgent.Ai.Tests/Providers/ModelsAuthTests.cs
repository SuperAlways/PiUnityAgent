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
            Resolve = (context, options, ct) => Task.FromResult(key),
        };

    private static ApiKeyAuth EnvResolver()
        => new()
        {
            Name = "env",
            Resolve = (context, options, ct) =>
                Task.FromResult<string?>(context.Env is not null
                    && context.Env.TryGetValue("KEY", out var value) ? value : null),
        };

    [Fact]
    public async Task Auth_Options_Win_Over_Store_And_Provider()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("p1", new ApiKeyCredential("store-key"));
        var models = new Models(store, new AuthContext
        {
            Env = new Dictionary<string, string> { ["KEY"] = "env-key" },
        });
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth { ApiKey = EnvResolver() }));

        var result = await models.GetAuthAsync(
            "p1",
            new AuthResolutionOverrides { ApiKey = "options-key" });

        Assert.Equal("options-key", Assert.IsType<ApiKeyCredential>(result!.Credential).Key);
        Assert.Equal("options", result.Source);
    }

    [Fact]
    public async Task Store_Wins_Over_Provider()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("p1", new ApiKeyCredential("store-key"));
        var models = new Models(store);
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth { ApiKey = EnvResolver() }));

        var result = await models.GetAuthAsync("p1");

        Assert.Equal("store-key", Assert.IsType<ApiKeyCredential>(result!.Credential).Key);
        Assert.Equal("store", result.Source);
    }

    [Fact]
    public async Task Provider_Resolver_Reads_Auth_Context_Env()
    {
        var models = new Models(authContext: new AuthContext
        {
            Env = new Dictionary<string, string> { ["KEY"] = "env-key" },
        });
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth { ApiKey = EnvResolver() }));

        var result = await models.GetAuthAsync("p1");

        Assert.Equal("env-key", Assert.IsType<ApiKeyCredential>(result!.Credential).Key);
        Assert.Equal("provider", result.Source);
    }

    [Fact]
    public async Task GetAuth_Unknown_Provider_Returns_Null()
    {
        var models = new Models();
        Assert.Null(await models.GetAuthAsync("missing"));
    }

    [Fact]
    public async Task Missing_Auth_Becomes_Error_On_Stream()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth { ApiKey = new ApiKeyAuth() }));

        var stream = models.Stream(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() });
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Contains("Authentication not configured", done.ErrorMessage);
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
            auth: new ProviderAuth { ApiKey = new ApiKeyAuth() }));

        var check = await models.CheckAuthAsync("p1");
        Assert.True(check!.Configured);
        Assert.Equal(AuthType.ApiKey, check.Type);

        var available = await models.GetAvailableAsync();
        Assert.Single(available);
        Assert.Equal("p1", available[0].Provider);
    }

    [Fact]
    public async Task Auth_Less_Provider_Is_Available()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider());

        Assert.True((await models.CheckAuthAsync("p1"))!.Configured);
        Assert.Single(await models.GetAvailableAsync());
    }

    [Fact]
    public async Task Logout_Deletes_Store_Credential()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("p1", new ApiKeyCredential("store-key"));
        var models = new Models(store);
        models.SetProvider(ProviderTestSupport.CreateProvider(
            auth: new ProviderAuth { ApiKey = new ApiKeyAuth() }));

        await models.LogoutAsync("p1");

        Assert.Null(await models.GetAuthAsync("p1"));
        Assert.Null(await store.GetAsync("p1"));
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
