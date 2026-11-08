using System.Text;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Tests.Fakes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

/// <summary>
/// S5 Task 6 / D1+D7 集成：Models.LoginAsync/LogoutAsync 接 FileCredentialStore、
/// 过期 OAuth 经 provider Refresh 轮换并写回 store、profile OAuth 边界（有 fetch 才挂 OAuth）；
/// Task 7 补 D10 可用性锚点（stored OAuth 凭据让 profile 进可用列表）。
/// 全程 MockFetch + 临时目录，无真实网络/浏览器。
/// </summary>
public sealed class S5AuthIntegrationTests : IDisposable
{
    private const string AnthropicClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private readonly string _dir;
    private readonly string _path;

    public S5AuthIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "s5-oauth-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "auth.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------
    // Models.LoginAsync / LogoutAsync × FileCredentialStore
    // ------------------------------------------------------------------

    [Fact]
    public async Task LoginAsync_OAuth_Stores_Credential_In_FileStore_And_Survives_Restart()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"access_token\":\"at-1\",\"refresh_token\":\"rt-1\",\"expires_in\":36000}");
        var store = CreateStore();
        var models = CreateModels(store, AnthropicProvider.Create(mock.Fetch));
        var interaction = AnthropicCopyCodeInteraction();

        var credential = Assert.IsType<OAuthCredential>(
            await models.LoginAsync("anthropic", AuthType.OAuth, interaction));
        Assert.Equal("at-1", credential.AccessToken);

        var stored = Assert.IsType<OAuthCredential>(await store.ReadAsync("anthropic"));
        Assert.Equal("at-1", stored.AccessToken);
        Assert.Equal("rt-1", stored.RefreshToken);
        Assert.Equal(credential.ExpiresAt, stored.ExpiresAt);

        // 原子落盘：重启（新 FileCredentialStore 同路径）后凭据还在。
        var reloaded = await new FileCredentialStore(_path).ReadAsync("anthropic");
        Assert.Equal("at-1", Assert.IsType<OAuthCredential>(reloaded).AccessToken);
        Assert.Contains("\"oauth\"", File.ReadAllText(_path));
    }

    [Fact]
    public async Task LoginAsync_ApiKey_Stores_ApiKeyCredential()
    {
        var store = CreateStore();
        var models = CreateModels(store, AnthropicProvider.Create());
        var interaction = new CannedInteraction(_ => "sk-ant-typed");

        var credential = await models.LoginAsync("anthropic", AuthType.ApiKey, interaction);

        Assert.Equal("sk-ant-typed", Assert.IsType<ApiKeyCredential>(credential).Key);
        Assert.Equal("sk-ant-typed", Assert.IsType<ApiKeyCredential>(await store.ReadAsync("anthropic")).Key);
    }

    [Fact]
    public async Task LoginAsync_Unknown_Provider_Throws_ModelsError_Provider()
    {
        var models = new Models();

        var error = await Assert.ThrowsAsync<ModelsError>(() =>
            models.LoginAsync("nope", AuthType.OAuth, new CannedInteraction(_ => "x")));

        Assert.Equal(ModelsErrorCode.Provider, error.Code);
        Assert.Contains("Unknown provider: nope", error.Message);
    }

    [Fact]
    public async Task LoginAsync_OAuth_Without_OAuth_Handler_Throws_ModelsError_Auth()
    {
        // 无 fetch 构造的 profile 只有 api-key 面——请求 OAuth 登录即 ModelsError(Auth)。
        var models = CreateModels(CreateStore(), AnthropicProvider.Create());

        var error = await Assert.ThrowsAsync<ModelsError>(() =>
            models.LoginAsync("anthropic", AuthType.OAuth, AnthropicCopyCodeInteraction()));

        Assert.Equal(ModelsErrorCode.Auth, error.Code);
        Assert.Contains("Anthropic does not support oauth login", error.Message);
    }

    [Fact]
    public async Task LoginAsync_Store_Failure_Wraps_ModelsError_Auth()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"access_token\":\"at-1\",\"refresh_token\":\"rt-1\",\"expires_in\":36000}");
        var models = CreateModels(new ExplodingStore(), AnthropicProvider.Create(mock.Fetch));

        var error = await Assert.ThrowsAsync<ModelsError>(() =>
            models.LoginAsync("anthropic", AuthType.OAuth, AnthropicCopyCodeInteraction()));

        Assert.Equal(ModelsErrorCode.Auth, error.Code);
        Assert.Contains("Credential store modify failed for anthropic", error.Message);
    }

    [Fact]
    public async Task LogoutAsync_Deletes_Stored_OAuth_Credential()
    {
        var store = CreateStore();
        var models = CreateModels(store, AnthropicProvider.Create(new MockFetch().Fetch));
        await store.ModifyAsync("anthropic", _ => Task.FromResult<Credential?>(new OAuthCredential
        {
            RefreshToken = "r",
            AccessToken = "a",
            ExpiresAt = long.MaxValue,
        }));

        await models.LogoutAsync("anthropic");

        Assert.Null(await store.ReadAsync("anthropic"));
        Assert.Null(await new FileCredentialStore(_path).ReadAsync("anthropic"));
    }

    // ------------------------------------------------------------------
    // 过期 OAuth 的刷新与写回（GetAuth 解析路径 + D2 refresh 路径）
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAuth_Expired_OAuth_Refreshes_And_Persists_Rotation()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"access_token\":\"new-a\",\"refresh_token\":\"new-r\",\"expires_in\":36000}");
        var store = CreateStore();
        await StoreOAuth(store, "anthropic", "old-a", "old-r", DateTimeOffset.Now.ToUnixTimeMilliseconds() - 1000);
        var models = CreateModels(store, AnthropicProvider.Create(mock.Fetch));

        var auth = await models.GetAuthAsync("anthropic");

        Assert.Equal("new-a", auth!.Auth.ApiKey);
        Assert.Equal("OAuth", auth.Source);

        // 轮换结果写回 store：下一次解析直接看到新凭据。
        var rotated = Assert.IsType<OAuthCredential>(await store.ReadAsync("anthropic"));
        Assert.Equal("new-a", rotated.AccessToken);
        Assert.Equal("new-r", rotated.RefreshToken);

        // 刷新请求带 refresh_token grant（pi refreshAnthropicToken 同形）。
        var request = Assert.Single(mock.Requests);
        var body = JsonNode.Parse(Encoding.UTF8.GetString(request.Body.Span))!.AsObject();
        Assert.Equal("refresh_token", body["grant_type"]!.GetValue<string>());
        Assert.Equal(AnthropicClientId, body["client_id"]!.GetValue<string>());
        Assert.Equal("old-r", body["refresh_token"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetAuth_Valid_OAuth_Does_Not_Call_Network()
    {
        var mock = new MockFetch();
        var store = CreateStore();
        await StoreOAuth(store, "anthropic", "old-a", "old-r", DateTimeOffset.Now.ToUnixTimeMilliseconds() + 30 * 60 * 1000);
        var models = CreateModels(store, AnthropicProvider.Create(mock.Fetch));

        var auth = await models.GetAuthAsync("anthropic");

        Assert.Equal("old-a", auth!.Auth.ApiKey);
        Assert.Equal("OAuth", auth.Source);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task GetAuth_Expiring_Soon_OAuth_Refreshes_Before_Five_Minute_Window()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"access_token\":\"fresh-a\",\"refresh_token\":\"fresh-r\",\"expires_in\":36000}");
        var store = CreateStore();
        // 剩余 2 分钟 < pi 默认五分钟最小有效期 → 触发刷新。
        await StoreOAuth(store, "anthropic", "old-a", "old-r", DateTimeOffset.Now.ToUnixTimeMilliseconds() + 2 * 60 * 1000);
        var models = CreateModels(store, AnthropicProvider.Create(mock.Fetch));

        var auth = await models.GetAuthAsync("anthropic");

        Assert.Equal("fresh-a", auth!.Auth.ApiKey);
        Assert.Equal(1, mock.CallCount);
    }

    [Fact]
    public async Task GetAuth_OAuth_Refresh_Failure_Wraps_ModelsError_OAuth()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"error\":\"invalid_grant\"}", 400);
        var store = CreateStore();
        await StoreOAuth(store, "anthropic", "old-a", "old-r", DateTimeOffset.Now.ToUnixTimeMilliseconds() - 1000);
        var models = CreateModels(store, AnthropicProvider.Create(mock.Fetch));

        var error = await Assert.ThrowsAsync<ModelsError>(() => models.GetAuthAsync("anthropic"));

        Assert.Equal(ModelsErrorCode.OAuth, error.Code);
        Assert.Contains("OAuth refresh failed for anthropic", error.Message);
    }

    [Fact]
    public async Task ModelsRefresh_Rotates_Expired_OAuth_And_Passes_Fresh_Credential_To_FetchModels()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"access_token\":\"new-a\",\"refresh_token\":\"new-r\",\"expires_in\":36000}");
        Credential? fetchModelsCredential = null;
        var store = CreateStore();
        await StoreOAuth(store, "rf", "old-a", "old-r", DateTimeOffset.Now.ToUnixTimeMilliseconds() - 1000);
        var models = CreateModels(store, RefreshableOAuthProvider(mock.Fetch,
            context =>
            {
                fetchModelsCredential = context.Credential;
                return Task.FromResult<IReadOnlyList<ProviderModel>>(new List<ProviderModel>());
            }));

        var result = await models.RefreshAsync();

        Assert.False(result.Aborted);
        Assert.Empty(result.Errors);
        Assert.Equal("new-a", Assert.IsType<OAuthCredential>(fetchModelsCredential).AccessToken);
        var rotated = Assert.IsType<OAuthCredential>(await store.ReadAsync("rf"));
        Assert.Equal("new-r", rotated.RefreshToken);
    }

    [Fact]
    public async Task GetAuth_CallerCancel_During_Refresh_Still_Persists_Rotated_Token()
    {
        // pi refreshStoredOAuthCredential（resolve.ts:110-114）：刷新一旦开始就只受 15s 超时
        // 约束——refresh token 是单次使用的，调用方在途取消若把轮换结果丢弃，凭据即死。
        var mock = new MockFetch();
        var refreshStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        mock.Enqueue(async (_, _, _) =>
        {
            refreshStarted.TrySetResult(true);
            await releaseRefresh.Task;
            return (IFetchResponse)new MockFetchResponse(200, Encoding.UTF8.GetBytes(
                "{\"access_token\":\"new-a\",\"refresh_token\":\"new-r\",\"expires_in\":36000}"));
        });
        var store = CreateStore();
        await StoreOAuth(store, "anthropic", "old-a", "old-r", DateTimeOffset.Now.ToUnixTimeMilliseconds() - 1000);
        var models = CreateModels(store, AnthropicProvider.Create(mock.Fetch));

        using var callerCts = new CancellationTokenSource();
        var task = models.GetAuthAsync("anthropic", new AuthResolutionOverrides { Signal = callerCts.Token });
        await refreshStarted.Task;
        callerCts.Cancel();               // 刷新 HTTP 在途时调用方放弃
        releaseRefresh.TrySetResult(true); // 刷新照常完成并轮换

        // 调用方最终仍看到取消（ToAuth 阶段观察 signal），但轮换凭据必须已落库。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var reloaded = Assert.IsType<OAuthCredential>(
            await new FileCredentialStore(_path).ReadAsync("anthropic"));
        Assert.Equal("new-a", reloaded.AccessToken);
        Assert.Equal("new-r", reloaded.RefreshToken);
    }

    [Fact]
    public async Task GetAuth_CallerCancel_While_Waiting_For_Store_Lock_No_Refresh_No_Write()
    {
        // 锁等待随调用方取消（pi：signal 只取消锁等待）：持锁者未放行时取消 →
        // 刷新未开始（零网络）、store 零写入。
        var mock = new MockFetch();
        var store = CreateStore();
        await StoreOAuth(store, "anthropic", "old-a", "old-r", DateTimeOffset.Now.ToUnixTimeMilliseconds() - 1000);
        var models = CreateModels(store, AnthropicProvider.Create(mock.Fetch));

        var lockEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = store.ModifyAsync("anthropic", async _ =>
        {
            lockEntered.TrySetResult(true);
            await releaseLock.Task;
            return (Credential?)null; // 不变更，只为占住 per-provider 锁
        });
        await lockEntered.Task;

        using var callerCts = new CancellationTokenSource();
        var task = models.GetAuthAsync("anthropic", new AuthResolutionOverrides { Signal = callerCts.Token });
        callerCts.Cancel(); // 持锁者仍占着锁——取消落在锁等待上

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        releaseLock.TrySetResult(true);
        await holder;

        Assert.Equal(0, mock.CallCount); // 刷新从未开始
        Assert.Equal("old-r",
            Assert.IsType<OAuthCredential>(await store.ReadAsync("anthropic")).RefreshToken);
    }

    // ------------------------------------------------------------------
    // profile OAuth 边界 + D10 可用性：有 fetch 才挂 OAuth；stored OAuth 进可用列表
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAvailable_Stored_OAuth_Credential_Makes_Profile_Available_Without_Network()
    {
        // pi filterModels/getAvailable：可用性判定走 CheckAuthAsync——stored OAuthCredential
        // 只看 auth.OAuth 是否存在（Models.Auth.cs），有效凭据不触发刷新网络。
        var mock = new MockFetch();
        var store = CreateStore();
        await StoreOAuth(store, "anthropic", "at-1", "rt-1",
            DateTimeOffset.Now.ToUnixTimeMilliseconds() + 30 * 60 * 1000);
        var models = CreateModels(store, AnthropicProvider.Create(mock.Fetch));

        var available = await models.GetAvailableAsync("anthropic");

        Assert.NotEmpty(available);
        Assert.All(available, model => Assert.Equal("anthropic", model.Provider));
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public void Profile_Without_Fetch_Keeps_ApiKey_Only_Behavior()
    {
        Assert.Null(AnthropicProvider.Create().Auth.OAuth);
        Assert.Null(OpenRouterProvider.Create().Auth.OAuth);
    }

    [Fact]
    public void Profile_With_Fetch_Attaches_Lazy_OAuth()
    {
        var anthropic = AnthropicProvider.Create(new MockFetch().Fetch).Auth.OAuth!;
        Assert.Equal("Anthropic (Claude Pro/Max)", anthropic.Name);
        Assert.True(anthropic.IsSubscription);
        Assert.Null(anthropic.LoginLabel);

        var openrouter = OpenRouterProvider.Create(new MockFetch().Fetch).Auth.OAuth!;
        Assert.Equal("OpenRouter OAuth", openrouter.Name);
        Assert.False(openrouter.IsSubscription);
        Assert.Equal("Sign in with OpenRouter", openrouter.LoginLabel);
    }

    // ------------------------------------------------------------------
    // 夹具
    // ------------------------------------------------------------------

    private FileCredentialStore CreateStore() => new(_path);

    private static Models CreateModels(CredentialStore store, IProvider provider)
    {
        var models = new Models(new CreateModelsOptions { Credentials = store });
        models.SetProvider(provider);
        return models;
    }

    private static Task StoreOAuth(
        CredentialStore store, string providerId, string access, string refresh, long expires)
        => store.ModifyAsync(providerId, _ => Task.FromResult<Credential?>(new OAuthCredential
        {
            RefreshToken = refresh,
            AccessToken = access,
            ExpiresAt = expires,
        }));

    /// <summary>Anthropic copy-code 交互：select→copy_code，manual→"auth-code"（state 缺省=verifier）。</summary>
    private static CannedInteraction AnthropicCopyCodeInteraction() => new(prompt => prompt switch
    {
        AuthPrompt.Select => "copy_code",
        AuthPrompt.ManualCode => "auth-code",
        _ => throw new InvalidOperationException($"unexpected prompt {prompt.GetType().Name}"),
    });

    /// <summary>带 OAuth + FetchModels 的可刷新 provider（走 Models.RefreshAsync 的凭据解析路径）。</summary>
    private static IProvider RefreshableOAuthProvider(
        FetchFunction fetch,
        Func<RefreshModelsContext, Task<IReadOnlyList<ProviderModel>>> fetchModels)
        => ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "rf",
            Name = "Refreshable",
            Auth = new ProviderAuth
            {
                // Anthropic 流的 Refresh 真交换 refresh token——OpenRouter 是 no-op，验不了轮换。
                OAuth = new AnthropicOAuth(fetch).ToOAuthAuth(),
            },
            Models = new[] { ProviderTestSupport.CreateModel("m1", "rf") },
            FetchModels = fetchModels,
            Api = new ProviderStreams
            {
                Stream = ProviderTestSupport.DoneStream,
                StreamSimple = (_, _, _) => throw new NotSupportedException("not exercised"),
            },
        });

    /// <summary>按提示类型回填答案的交互假体。</summary>
    private sealed class CannedInteraction(Func<AuthPrompt, string> answer) : AuthInteraction
    {
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(answer(prompt));

        public void Notify(AuthEvent @event)
        {
        }
    }

    /// <summary>读写一律抛错的 store：验证 Models 把 store 失败包成 ModelsError(Auth)。</summary>
    private sealed class ExplodingStore : CredentialStore
    {
        public Task<Credential?> ReadAsync(string providerId, AuthOperationOptions? options = null)
            => throw new InvalidOperationException("store exploded");

        public Task<IReadOnlyList<CredentialInfo>> ListAsync(AuthOperationOptions? options = null)
            => throw new InvalidOperationException("store exploded");

        public Task<Credential?> ModifyAsync(
            string providerId,
            Func<Credential?, Task<Credential?>> modify,
            AuthOperationOptions? options = null)
            => throw new InvalidOperationException("store exploded");

        public Task DeleteAsync(string providerId, AuthOperationOptions? options = null)
            => throw new InvalidOperationException("store exploded");
    }
}
