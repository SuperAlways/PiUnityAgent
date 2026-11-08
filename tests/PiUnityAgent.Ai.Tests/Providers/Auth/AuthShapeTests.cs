using System.Text.Json;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Providers.Auth;

namespace PiUnityAgent.Ai.Tests.Providers.Auth;

public class AuthShapeTests
{
    [Fact]
    public async Task InMemoryCredentialStore_Reads_Lists_Modifies_And_Deletes()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p1", _ => Task.FromResult<Credential?>(
            new ApiKeyCredential("key-1") { Env = new Dictionary<string, string> { ["REGION"] = "cn" } }));

        var loaded = Assert.IsType<ApiKeyCredential>(await store.ReadAsync("p1"));
        Assert.Equal("key-1", loaded.Key);
        Assert.Equal("cn", loaded.Env!["REGION"]);

        var listed = await store.ListAsync();
        Assert.Single(listed);
        Assert.Equal("p1", listed[0].ProviderId);
        Assert.Equal(AuthType.ApiKey, listed[0].Type);

        await store.ModifyAsync("p1", current =>
        {
            var key = Assert.IsType<ApiKeyCredential>(current);
            return Task.FromResult<Credential?>(key with { Key = "key-2" });
        });
        Assert.Equal("key-2", Assert.IsType<ApiKeyCredential>(await store.ReadAsync("p1")).Key);

        await store.DeleteAsync("p1");
        Assert.Null(await store.ReadAsync("p1"));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Modify_With_Null_Leaves_Entry_And_Returns_Current()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p1", _ => Task.FromResult<Credential?>(
            new ApiKeyCredential("key-1")));

        var afterNull = await store.ModifyAsync("p1", _ => Task.FromResult<Credential?>(null));

        Assert.Equal("key-1", Assert.IsType<ApiKeyCredential>(afterNull).Key);
        Assert.Equal("key-1", Assert.IsType<ApiKeyCredential>(await store.ReadAsync("p1")).Key);
    }

    [Fact]
    public async Task Modify_Is_Serialized_Per_Provider()
    {
        var store = new InMemoryCredentialStore();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(() => store.ModifyAsync("p1", async _ =>
        {
            firstStarted.SetResult(true);
            await releaseFirst.Task;
            return (Credential?)new ApiKeyCredential("first");
        }));
        await firstStarted.Task;
        var second = Task.Run(() => store.ModifyAsync("p1", current =>
        {
            Assert.IsType<ApiKeyCredential>(current);
            return Task.FromResult<Credential?>(new ApiKeyCredential("second"));
        }));

        releaseFirst.SetResult(true);
        await first;
        await second;
        Assert.Equal("second", Assert.IsType<ApiKeyCredential>(await store.ReadAsync("p1")).Key);
    }

    [Fact]
    public void AuthResult_Carries_ModelAuth_Env_And_Source()
    {
        var result = new AuthResult(
            new ModelAuth { ApiKey = "k", BaseUrl = "https://example.test" },
            new Dictionary<string, string> { ["REGION"] = "cn" },
            "store");
        var check = new AuthCheck(AuthType.ApiKey, "store");

        Assert.Equal("k", result.Auth.ApiKey);
        Assert.Equal("cn", result.Env!["REGION"]);
        Assert.Equal("store", result.Source);
        Assert.Equal("store", check.Source);
        Assert.Equal(AuthType.ApiKey, check.Type);
    }

    [Fact]
    public async Task ApiKeyAuth_Resolve_And_Check_Are_Available()
    {
        var context = new TestAuthContext(new Dictionary<string, string> { ["KEY"] = "env-key" });
        var auth = new ApiKeyAuth
        {
            Name = "test",
            Resolve = (input, _) => Task.FromResult<AuthResult?>(new AuthResult(
                new ModelAuth { ApiKey = input.Credential?.Key ?? "resolved" },
                input.Credential?.Env,
                "resolver")),
            Check = (input, _) => Task.FromResult<AuthCheck?>(new AuthCheck(AuthType.ApiKey, "check")),
        };

        var resolved = await auth.Resolve(new ApiKeyResolveInput(context, new ApiKeyCredential("override")), default);
        var checkedAuth = await auth.Check!(new ApiKeyCheckInput(context, null), default);

        Assert.Equal("override", resolved!.Auth.ApiKey);
        Assert.Equal("resolver", resolved.Source);
        Assert.Equal("check", checkedAuth!.Source);
    }

    [Fact]
    public async Task OAuthAuth_Has_ToAuth_And_No_Logout()
    {
        var auth = new OAuthAuth
        {
            Name = "oauth",
            ToAuth = (credential, ct) => Task.FromResult(new ModelAuth { ApiKey = credential.AccessToken }),
            Login = (interaction, options, ct) => Task.FromResult(new OAuthCredential { RefreshToken = "r", AccessToken = "a", ExpiresAt = 0 }),
            Refresh = (credential, ct) => Task.FromResult(credential),
        };

        Assert.Equal("a", (await auth.ToAuth(new OAuthCredential { RefreshToken = "r", AccessToken = "a", ExpiresAt = 0 }, default)).ApiKey);
        Assert.NotNull(auth.Login);
    }

    [Fact]
    public async Task DefaultAuthContext_Reads_Environment_And_Files()
    {
        var context = new DefaultAuthContext();
        Environment.SetEnvironmentVariable("PI_TEST_ENV", "yes");
        try
        {
            Assert.Equal("yes", await context.EnvAsync("PI_TEST_ENV"));
            Assert.True(await context.FileExistsAsync(typeof(DefaultAuthContext).Assembly.Location));
            Assert.False(await context.FileExistsAsync("Z:/definitely/missing/file"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_TEST_ENV", null);
        }
    }

    private sealed class TestAuthContext : IAuthContext
    {
        private readonly IReadOnlyDictionary<string, string> _env;
        public TestAuthContext(IReadOnlyDictionary<string, string> env) => _env = env;
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(_env.TryGetValue(name, out var value) ? value : null);
        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
