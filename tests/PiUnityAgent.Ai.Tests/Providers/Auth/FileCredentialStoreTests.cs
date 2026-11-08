using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;

namespace PiUnityAgent.Ai.Tests.Providers.Auth;

public sealed class FileCredentialStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public FileCredentialStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s5-fcs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "auth.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Write_Then_Read_RoundTrips_ApiKey_And_OAuth()
    {
        var store = new FileCredentialStore(_path);
        await store.ModifyAsync("anthropic", _ => Task.FromResult<Credential?>(
            new ApiKeyCredential("sk-ant-1") { Env = new Dictionary<string, string> { ["REGION"] = "us" } }));
        await store.ModifyAsync("openrouter", _ => Task.FromResult<Credential?>(
            new OAuthCredential
            {
                RefreshToken = "refresh-1",
                AccessToken = "access-1",
                ExpiresAt = 1760000000000L,
                Extra = new Dictionary<string, JsonNode?> { ["accountId"] = "acc-7" },
            }));

        var reloaded = new FileCredentialStore(_path);

        var apiKey = Assert.IsType<ApiKeyCredential>(await reloaded.ReadAsync("anthropic"));
        Assert.Equal("sk-ant-1", apiKey.Key);
        Assert.Equal("us", apiKey.Env!["REGION"]);

        var oauth = Assert.IsType<OAuthCredential>(await reloaded.ReadAsync("openrouter"));
        Assert.Equal("refresh-1", oauth.RefreshToken);
        Assert.Equal("access-1", oauth.AccessToken);
        Assert.Equal(1760000000000L, oauth.ExpiresAt);
        Assert.Equal("acc-7", oauth.Extra!["accountId"]!.GetValue<string>());

        var listed = await reloaded.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, info => info.ProviderId == "anthropic" && info.Type == AuthType.ApiKey);
        Assert.Contains(listed, info => info.ProviderId == "openrouter" && info.Type == AuthType.OAuth);
    }

    [Fact]
    public async Task Modify_Null_Leaves_Entry_Unchanged()
    {
        var store = new FileCredentialStore(_path);
        await store.ModifyAsync("anthropic", _ => Task.FromResult<Credential?>(
            new ApiKeyCredential("key-1")));

        var returned = await store.ModifyAsync("anthropic", _ => Task.FromResult<Credential?>(null));

        Assert.Equal("key-1", Assert.IsType<ApiKeyCredential>(returned).Key);
        Assert.Equal("key-1", Assert.IsType<ApiKeyCredential>(await store.ReadAsync("anthropic")).Key);

        var reloaded = new FileCredentialStore(_path);
        Assert.Equal("key-1", Assert.IsType<ApiKeyCredential>(await reloaded.ReadAsync("anthropic")).Key);
    }

    [Fact]
    public async Task Modify_Is_Serialized_Per_Provider()
    {
        var store = new FileCredentialStore(_path);
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
            // 只有 per-provider 串行成立时，second 才可能看到 first 的写入。
            Assert.IsType<ApiKeyCredential>(current);
            return Task.FromResult<Credential?>(new ApiKeyCredential("second"));
        }));

        // 给 second 一次进入回调的机会；若串行被破坏，上面的 current 断言会失败。
        await Task.Delay(50);
        releaseFirst.SetResult(true);
        await first;
        await second;

        Assert.Equal("second", Assert.IsType<ApiKeyCredential>(await store.ReadAsync("p1")).Key);
    }

    [Fact]
    public async Task Missing_File_Reads_Null()
    {
        var store = new FileCredentialStore(_path);
        Assert.Null(await store.ReadAsync("anthropic"));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public void Corrupt_Json_Throws_ModelsError()
    {
        File.WriteAllText(_path, "{ not valid json");

        var error = Assert.Throws<ModelsError>(() => new FileCredentialStore(_path));
        Assert.Equal(ModelsErrorCode.Auth, error.Code);
    }

    [Fact]
    public void Invalid_Structure_Throws_ModelsError()
    {
        File.WriteAllText(_path, """{"credentials": {"x": {"type": "bogus"}}}""");

        var error = Assert.Throws<ModelsError>(() => new FileCredentialStore(_path));
        Assert.Equal(ModelsErrorCode.Auth, error.Code);
    }

    [Fact]
    public void Unsupported_Version_Throws_ModelsError()
    {
        File.WriteAllText(_path, """{"version": 99, "credentials": {}}""");

        var error = Assert.Throws<ModelsError>(() => new FileCredentialStore(_path));
        Assert.Equal(ModelsErrorCode.Auth, error.Code);
    }

    [Fact]
    public async Task Write_Is_Atomic_And_Leaves_No_Temp_File()
    {
        File.WriteAllText(_path + ".tmp", "stale temp from a crashed run");
        var store = new FileCredentialStore(_path);

        await store.ModifyAsync("anthropic", _ => Task.FromResult<Credential?>(
            new ApiKeyCredential("key-1")));
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.True(File.Exists(_path));

        // 目标已存在 → 走 File.Replace 路径。
        await store.ModifyAsync("anthropic", current =>
        {
            var key = Assert.IsType<ApiKeyCredential>(current);
            return Task.FromResult<Credential?>(key with { Key = "key-2" });
        });

        Assert.False(File.Exists(_path + ".tmp"));
        var root = JsonNode.Parse(await File.ReadAllTextAsync(_path))!.AsObject();
        Assert.Equal(1, root["version"]!.GetValue<int>());
        var entry = root["credentials"]!["anthropic"]!;
        Assert.Equal("api_key", entry["type"]!.GetValue<string>());
        Assert.Equal("key-2", entry["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task OAuth_Credential_Serializes_Refresh_Access_Expires()
    {
        var store = new FileCredentialStore(_path);
        await store.ModifyAsync("openrouter", _ => Task.FromResult<Credential?>(
            new OAuthCredential { RefreshToken = "r-1", AccessToken = "a-1", ExpiresAt = 1760000000000L }));

        var root = JsonNode.Parse(await File.ReadAllTextAsync(_path))!.AsObject();
        var entry = root["credentials"]!["openrouter"]!;
        Assert.Equal("oauth", entry["type"]!.GetValue<string>());
        Assert.Equal("r-1", entry["refresh"]!.GetValue<string>());
        Assert.Equal("a-1", entry["access"]!.GetValue<string>());
        Assert.Equal(1760000000000L, entry["expires"]!.GetValue<long>());
    }

    [Fact]
    public async Task Delete_Removes_Entry_And_Persists()
    {
        var store = new FileCredentialStore(_path);
        await store.ModifyAsync("p1", _ => Task.FromResult<Credential?>(new ApiKeyCredential("k1")));
        await store.ModifyAsync("p2", _ => Task.FromResult<Credential?>(new ApiKeyCredential("k2")));

        await store.DeleteAsync("p1");

        Assert.Null(await store.ReadAsync("p1"));
        var reloaded = new FileCredentialStore(_path);
        Assert.Null(await reloaded.ReadAsync("p1"));
        Assert.Equal("k2", Assert.IsType<ApiKeyCredential>(await reloaded.ReadAsync("p2")).Key);
        Assert.Single(await reloaded.ListAsync());
    }

    [Fact]
    public async Task Missing_Directory_Is_Created()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "auth.json");
        var store = new FileCredentialStore(path);
        await store.ModifyAsync("p1", _ => Task.FromResult<Credential?>(new ApiKeyCredential("k1")));

        var reloaded = new FileCredentialStore(path);
        Assert.Equal("k1", Assert.IsType<ApiKeyCredential>(await reloaded.ReadAsync("p1")).Key);
    }
}
