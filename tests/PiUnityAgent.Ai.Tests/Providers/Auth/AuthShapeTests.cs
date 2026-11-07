using System.Text.Json;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Providers.Auth;

namespace PiUnityAgent.Ai.Tests.Providers.Auth;

public class AuthShapeTests
{
    [Fact]
    public async Task InMemoryCredentialStore_RoundTrips_And_Deletes()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("p1", new ApiKeyCredential("key-1"));
        var loaded = await store.GetAsync("p1");

        var apiKey = Assert.IsType<ApiKeyCredential>(loaded);
        Assert.Equal("key-1", apiKey.Key);

        await store.DeleteAsync("p1");
        Assert.Null(await store.GetAsync("p1"));
    }

    [Fact]
    public void Credential_Variants_And_AuthResults_Are_Present()
    {
        var apiKey = new ApiKeyCredential("key");
        var oauth = new OAuthCredential
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = 123456789,
        };
        var result = new AuthResult(oauth, "OAuth");
        var check = new AuthCheck(true, AuthType.OAuth);
        var info = new CredentialInfo(AuthType.ApiKey, "env");

        Assert.Equal("key", apiKey.Key);
        Assert.Equal("access", oauth.AccessToken);
        Assert.Same(oauth, result.Credential);
        Assert.Equal("OAuth", result.Source);
        Assert.True(check.Configured);
        Assert.Equal(AuthType.OAuth, check.Type);
        Assert.Equal(AuthType.ApiKey, info.Type);
    }

    [Fact]
    public void AuthPrompt_And_AuthEvent_Variants_Are_Present()
    {
        var text = new AuthPrompt.Text { Message = "Enter key" };
        var secret = new AuthPrompt.Secret { Message = "Secret" };
        var select = new AuthPrompt.Select
        {
            Message = "Choose",
            Options = new[]
            {
                new AuthPromptOption("api_key", "API key"),
            },
        };
        var manual = new AuthPrompt.ManualCode { Message = "Code" };
        var info = new AuthEvent.Info("hello", new[] { new AuthInfoLink("https://example.test") });
        var url = new AuthEvent.AuthUrl("https://example.test/auth");
        var device = new AuthEvent.DeviceCode("user-code", "https://example.test/device");
        var progress = new AuthEvent.Progress("working");

        Assert.Equal("Enter key", text.Message);
        Assert.Equal("Choose", select.Message);
        Assert.Single(select.Options);
        Assert.Equal("Code", manual.Message);
        Assert.Single(info.Links!);
        Assert.Equal("https://example.test/auth", url.Url);
        Assert.Equal("user-code", device.UserCode);
        Assert.Equal("working", progress.Message);
        Assert.Equal("Secret", secret.Message);
    }

    [Fact]
    public void ProviderAuth_And_Delegates_Are_Present()
    {
        var api = new ApiKeyAuth
        {
            Name = "API key",
            Resolve = (context, options, ct) =>
                Task.FromResult<string?>(context.Env?["KEY"]),
        };
        var oauth = new OAuthAuth
        {
            Name = "OAuth",
            IsSubscription = true,
            LoginLabel = "Sign in",
            Login = (options, interaction, ct) =>
                Task.FromResult<Credential>(new OAuthCredential { AccessToken = "a" }),
            Refresh = (credential, ct) => Task.FromResult(credential),
            Logout = (providerId, ct) => Task.CompletedTask,
        };
        var auth = new ProviderAuth { ApiKey = api, OAuth = oauth };

        Assert.Same(api, auth.ApiKey);
        Assert.Same(oauth, auth.OAuth);
        Assert.NotNull(api.Resolve);
        Assert.NotNull(oauth.Login);
    }

    [Fact]
    public async Task AuthInteraction_And_ProviderWrapper_Are_Present()
    {
        var interaction = new RecordingInteraction();
        var wrapper = new ProviderAuthInteraction(interaction, CancellationToken.None);
        var prompt = new AuthPrompt.Text { Message = "Enter" };

        var answer = await wrapper.PromptAsync(prompt);
        Assert.Equal("answer", answer);

        var evt = new AuthEvent.Progress("ok");
        wrapper.Notify(evt);
        Assert.Same(evt, interaction.LastEvent);
    }

    [Fact]
    public void ModelsStore_And_Refresh_Types_Are_Present()
    {
        var entry = new ModelsStoreEntry
        {
            Provider = "p1",
            Data = new JsonObject { ["models"] = new JsonArray() },
            UpdatedAt = 1,
        };
        var publication = new ModelsPublication
        {
            Persist = entry,
            Update = () => { },
        };
        var context = new RefreshModelsContext
        {
            Credential = new ApiKeyCredential("key"),
            Stored = entry,
            BaseUrl = "https://example.test",
            Publish = _ => Task.FromResult(true),
            AllowNetwork = true,
            Force = true,
            CancellationToken = CancellationToken.None,
        };
        var options = new ModelsRefreshOptions
        {
            AllowNetwork = false,
            Providers = new[] { "p1" },
            Force = false,
            CancellationToken = CancellationToken.None,
        };
        var result = new ModelsRefreshResult(false, new Dictionary<string, Exception>());

        Assert.Equal("p1", entry.Provider);
        Assert.Same(entry, publication.Persist);
        Assert.NotNull(context.Publish);
        Assert.NotNull(options.Providers);
        Assert.False(result.Aborted);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(AuthType.ApiKey, "\"api_key\"")]
    [InlineData(AuthType.OAuth, "\"oauth\"")]
    public void AuthType_Uses_Pi_Wire_Names(AuthType value, string wire)
    {
        Assert.Equal(wire, JsonSerializer.Serialize(value));
        Assert.Equal(value, JsonSerializer.Deserialize<AuthType>(wire));
    }

    private sealed class RecordingInteraction : AuthInteraction
    {
        public AuthEvent? LastEvent { get; private set; }

        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
            => Task.FromResult("answer");

        public void Notify(AuthEvent @event) => LastEvent = @event;
    }
}
