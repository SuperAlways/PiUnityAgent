using PiUnityAgent.Ai.Providers.Auth;

namespace PiUnityAgent.Ai.Tests.Providers.Auth;

public class AuthHelpersTests
{
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

    [Fact]
    public async Task EnvApiKeyAuth_Stored_Key_Wins_And_Env_Is_Ordered()
    {
        var auth = AuthHelpers.EnvApiKeyAuth("Test API key", new[] { "PRIMARY", "SECONDARY" });
        var stored = new ApiKeyCredential("stored");
        var result = await auth.Resolve(
            new ApiKeyResolveInput(new StaticEnvAuthContext(new Dictionary<string, string>()), stored), default);
        Assert.Equal("stored", result!.Auth.ApiKey);

        var ambient = await auth.Resolve(
            new ApiKeyResolveInput(
                new StaticEnvAuthContext(new Dictionary<string, string> { ["SECONDARY"] = "env-key" }), null),
            default);
        Assert.Equal("env-key", ambient!.Auth.ApiKey);
        Assert.Equal("SECONDARY", ambient.Source);
    }

    [Fact]
    public async Task EnvApiKeyAuth_Without_Stored_Or_Env_Returns_Null()
    {
        var auth = AuthHelpers.EnvApiKeyAuth("Test provider", new[] { "PRIMARY", "SECONDARY" });
        var ctx = new StaticEnvAuthContext(new Dictionary<string, string>());

        var resolved = await auth.Resolve(new ApiKeyResolveInput(ctx, null), default);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task EnvApiKeyAuth_Empty_Stored_Key_Falls_Back_To_First_Set_Env()
    {
        var auth = AuthHelpers.EnvApiKeyAuth("Test provider", new[] { "PRIMARY", "SECONDARY" });
        var ctx = new StaticEnvAuthContext(new Dictionary<string, string>
        {
            ["PRIMARY"] = "",
            ["SECONDARY"] = "env-key",
        });

        var resolved = await auth.Resolve(new ApiKeyResolveInput(ctx, new ApiKeyCredential("")), default);

        Assert.Equal("env-key", resolved!.Auth.ApiKey);
        Assert.Equal("SECONDARY", resolved.Source);
    }

    [Fact]
    public async Task EnvApiKeyAuth_Stored_Result_Carries_Credential_Env()
    {
        var auth = AuthHelpers.EnvApiKeyAuth("Test provider", new[] { "PRIMARY" });
        var credentialEnv = new Dictionary<string, string> { ["REGION"] = "cn" };
        var ctx = new StaticEnvAuthContext(new Dictionary<string, string>());

        var resolved = await auth.Resolve(
            new ApiKeyResolveInput(ctx, new ApiKeyCredential("stored") { Env = credentialEnv }), default);

        Assert.Same(credentialEnv, resolved!.Env);
        Assert.Equal("stored credential", resolved.Source);
    }

    [Fact]
    public async Task EnvApiKeyAuth_Login_Prompts_For_Secret()
    {
        var interaction = new RecordingInteraction("typed-key");
        var auth = AuthHelpers.EnvApiKeyAuth("Test provider", new[] { "PRIMARY" });

        var credential = await auth.Login!(
            new ProviderAuthInteraction(interaction, default), default);

        Assert.Equal("typed-key", credential.Key);
        var prompt = Assert.IsType<AuthPrompt.Secret>(Assert.Single(interaction.Prompts));
        Assert.Equal("Enter Test provider", prompt.Message);
    }

    [Fact]
    public async Task EnvApiKeyAuth_Cancelled_Resolve_Throws_OperationCanceled()
    {
        var auth = AuthHelpers.EnvApiKeyAuth("Test provider", new[] { "PRIMARY" });
        var ctx = new StaticEnvAuthContext(new Dictionary<string, string>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => auth.Resolve(new ApiKeyResolveInput(ctx, null), cts.Token));
    }

    [Fact]
    public async Task LazyOAuth_Loads_On_First_Login_Refresh_Or_ToAuth_And_Only_Once()
    {
        var loaded = new OAuthAuth
        {
            Name = "loaded",
            Login = (_, _, _) => Task.FromResult(new OAuthCredential
            {
                RefreshToken = "refresh",
                AccessToken = "access",
                ExpiresAt = 1,
            }),
            Refresh = (credential, _) => Task.FromResult(credential with { AccessToken = "refreshed" }),
            ToAuth = (credential, _) => Task.FromResult(new ModelAuth { ApiKey = credential.AccessToken }),
        };
        var loads = 0;
        var lazy = AuthHelpers.LazyOAuth("lazy", () =>
        {
            loads++;
            return Task.FromResult(loaded);
        });

        Assert.Equal(0, loads);
        Assert.Equal("lazy", lazy.Name);

        var credential = await lazy.Login(
            new ProviderAuthInteraction(new RecordingInteraction(""), default), null, default);
        Assert.Equal("access", credential.AccessToken);

        var refreshed = await lazy.Refresh(credential, default);
        Assert.Equal("refreshed", refreshed.AccessToken);

        var auth = await lazy.ToAuth(refreshed, default);
        Assert.Equal("refreshed", auth.ApiKey);

        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task LazyOAuth_Concurrent_Calls_Load_Once()
    {
        var loads = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lazy = AuthHelpers.LazyOAuth("lazy", async () =>
        {
            loads++;
            await release.Task;
            return new OAuthAuth
            {
                Name = "loaded",
                Login = (_, _, _) => Task.FromResult(new OAuthCredential
                {
                    RefreshToken = "r",
                    AccessToken = "a",
                    ExpiresAt = 1,
                }),
                Refresh = (credential, _) => Task.FromResult(credential),
                ToAuth = (_, _) => Task.FromResult(new ModelAuth()),
            };
        });

        var first = lazy.ToAuth(new OAuthCredential { RefreshToken = "r", AccessToken = "a", ExpiresAt = 1 }, default);
        var second = lazy.ToAuth(new OAuthCredential { RefreshToken = "r", AccessToken = "a", ExpiresAt = 1 }, default);

        Assert.Equal(1, loads);
        release.SetResult(true);
        await Task.WhenAll(first, second);
        Assert.Equal(1, loads);
    }

    [Fact]
    public void LazyOAuth_Carries_Subscription_And_Label_Flags()
    {
        var lazy = AuthHelpers.LazyOAuth(
            "lazy", () => Task.FromResult(new OAuthAuth
            {
                Name = "loaded",
                Login = (_, _, _) => throw new NotSupportedException(),
                Refresh = (_, _) => throw new NotSupportedException(),
                ToAuth = (_, _) => throw new NotSupportedException(),
            }),
            isSubscription: true,
            loginLabel: "Sign in");

        Assert.True(lazy.IsSubscription);
        Assert.Equal("Sign in", lazy.LoginLabel);
        Assert.Equal("lazy", lazy.Name);
    }

    [Fact]
    public void LazyOAuth_Null_Load_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AuthHelpers.LazyOAuth("lazy", null!));
    }
}
