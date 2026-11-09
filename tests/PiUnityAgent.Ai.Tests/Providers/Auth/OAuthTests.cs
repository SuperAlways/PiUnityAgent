using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Tests.Fakes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers.Auth;

/// <summary>
/// S5 Task 6 / D1：OAuth 原语与两条手工流程（Anthropic copy-code、OpenRouter manual code）。
/// 期望值全部来自 pi auth/oauth/{anthropic,openrouter,pkce}.ts 与 pi.net Auth/OAuth/*.cs，不猜。
/// 全程 MockFetch，无真实网络、无浏览器回调（spec §10 C 偏离）。
/// </summary>
public class OAuthTests
{
    private const string AnthropicTokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string OpenRouterTokenUrl = "https://openrouter.ai/api/v1/auth/keys";
    private const long PermanentExpiresAt = 9007199254740991;

    // ------------------------------------------------------------------
    // Pkce
    // ------------------------------------------------------------------

    [Fact]
    public void Pkce_Generate_Has_Deterministic_Shape()
    {
        var pair = Pkce.Generate();

        // 32 随机字节 base64url → 43 字符，无填充。
        Assert.Equal(43, pair.Verifier.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", pair.Verifier);

        // challenge = SHA-256(verifier) 的 base64url（S256）。
        var expected = Pkce.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes(pair.Verifier)));
        Assert.Equal(expected, pair.Challenge);
        Assert.Equal(43, pair.Challenge.Length);
    }

    [Fact]
    public void Pkce_Generate_Is_Random_Per_Call()
    {
        var first = Pkce.Generate();
        var second = Pkce.Generate();

        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.NotEqual(first.Challenge, second.Challenge);
    }

    [Fact]
    public void Pkce_Base64UrlEncode_Uses_Url_Safe_Alphabet_Without_Padding()
    {
        // 0xfb 0xff 0xfc → "+//8" → "-__8"（+/- 与 //_ 替换）。
        Assert.Equal("-__8", Pkce.Base64UrlEncode(new byte[] { 0xfb, 0xff, 0xfc }));
        Assert.Equal("", Pkce.Base64UrlEncode(Array.Empty<byte>()));
        Assert.Equal("AA", Pkce.Base64UrlEncode(new byte[] { 0x00 }));
    }

    // ------------------------------------------------------------------
    // OAuthInput：粘贴授权输入解析（URL / code#state / 查询串 / 裸 code）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("https://platform.claude.com/oauth/code/callback?code=abc&state=xyz", "abc", "xyz")]
    [InlineData("abc#xyz", "abc", "xyz")]
    [InlineData("code=abc&state=xyz", "abc", "xyz")]
    [InlineData("bare-code", "bare-code", null)]
    [InlineData("abc#", "abc", null)]
    [InlineData("  ", null, null)]
    public void OAuthInput_ParseAuthorizationInput_Covers_All_Pastes(
        string input, string? expectedCode, string? expectedState)
    {
        var parsed = OAuthInput.ParseAuthorizationInput(input);

        Assert.Equal(expectedCode, parsed.Code);
        Assert.Equal(expectedState, parsed.State);
    }

    [Fact]
    public void OAuthInput_ParseAuthorizationInput_Decodes_Url_Escapes()
    {
        var parsed = OAuthInput.ParseAuthorizationInput(
            "https://platform.claude.com/oauth/code/callback?code=a%2Bb&state=s%21");

        Assert.Equal("a+b", parsed.Code);
        Assert.Equal("s!", parsed.State);
    }

    [Theory]
    [InlineData("http://localhost:53693/oauth/callback?code=manual-code", "manual-code")]
    [InlineData("  manual-code  ", "manual-code")]
    [InlineData("code=manual-code", "manual-code")]
    [InlineData("", null)]
    public void OAuthInput_ParseAuthorizationCode_OpenRouter_Semantics(string input, string? expected)
    {
        Assert.Equal(expected, OAuthInput.ParseAuthorizationCode(input));
    }

    // ------------------------------------------------------------------
    // OAuthHttp：FetchRequest/FetchOptions 映射 + 状态/解析错误
    // ------------------------------------------------------------------

    [Fact]
    public async Task OAuthHttp_PostJson_Sends_Post_Json_With_Headers_And_Returns_Body()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"ok\":true}");

        var body = await OAuthHttp.PostJsonAsync(
            mock.Fetch, "https://example.test/token", new JsonObject { ["grant_type"] = "authorization_code" },
            default);

        Assert.Equal("{\"ok\":true}", body);
        var request = Assert.Single(mock.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://example.test/token", request.Url);
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.Equal(
            "{\"grant_type\":\"authorization_code\"}",
            Encoding.UTF8.GetString(request.Body.Span));

        // pi 的 30s 每请求超时映射为 FetchOptions 三超时（B/D 偏离：宿主传输执行）。
        Assert.NotNull(request);
    }

    [Fact]
    public async Task OAuthHttp_PostJson_Passes_Timeout_Options()
    {
        var mock = new MockFetch();
        FetchOptions? seenOptions = null;
        mock.Enqueue((_, options, _) =>
        {
            seenOptions = options;
            return Task.FromResult<IFetchResponse>(new MockFetchResponse(200, "{}"u8.ToArray()));
        });

        await OAuthHttp.PostJsonAsync(
            mock.Fetch, "https://example.test/token", new JsonObject(), default, timeoutMs: 1234);

        Assert.NotNull(seenOptions);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), seenOptions!.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), seenOptions.FirstByteTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), seenOptions.IdleTimeout);
    }

    [Theory]
    [InlineData(400, "{\"error\":\"bad\"}")]
    [InlineData(500, "server exploded")]
    public async Task OAuthHttp_PostJson_Non_Success_Throws_With_Status_Url_Body(int status, string body)
    {
        var mock = new MockFetch();
        mock.EnqueueText(body, status);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OAuthHttp.PostJsonAsync(mock.Fetch, "https://example.test/token", new JsonObject(), default));

        Assert.Contains($"status={status}", error.Message);
        Assert.Contains("url=https://example.test/token", error.Message);
        Assert.Contains($"body={body}", error.Message);
    }

    [Fact]
    public void OAuthHttp_ParseJsonObject_Returns_Null_On_Empty_Or_Non_Object()
    {
        Assert.Null(OAuthHttp.ParseJsonObject(""));
        Assert.Null(OAuthHttp.ParseJsonObject("   "));
        Assert.Null(OAuthHttp.ParseJsonObject("not json"));
        Assert.Null(OAuthHttp.ParseJsonObject("[1,2]"));

        var parsed = OAuthHttp.ParseJsonObject("{\"a\":1}");
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!["a"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------
    // AnthropicOAuth：copy-code/manual
    // ------------------------------------------------------------------

    [Fact]
    public async Task Anthropic_CopyCode_Login_Prompts_And_Exchanges_Code()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"access_token\":\"at-1\",\"refresh_token\":\"rt-1\",\"expires_in\":36000}");
        long now = 1_000_000_000_000L;
        var oauth = new AnthropicOAuth(mock.Fetch) { Now = () => now };
        CannedInteraction interaction = null!;
        interaction = new CannedInteraction(prompt =>
        {
            if (prompt is AuthPrompt.Select) return "copy_code";
            if (prompt is AuthPrompt.ManualCode)
            {
                // state=verifier 由授权 URL 带出；粘贴 code#state。
                var url = interaction.AuthUrl!.Url;
                return $"auth-code#{OAuthInput.ParseAuthorizationInput(url).State}";
            }
            throw new InvalidOperationException($"unexpected prompt {prompt.GetType().Name}");
        });

        var credential = await oauth.ToOAuthAuth().Login(
            new ProviderAuthInteraction(interaction, default), null, default);

        // 凭据形状：expires = now + expires_in*1000 - 5min（pi 同式）。
        Assert.Equal("at-1", credential.AccessToken);
        Assert.Equal("rt-1", credential.RefreshToken);
        Assert.Equal(now + 36_000_000 - 5 * 60 * 1000, credential.ExpiresAt);

        // 提示序列：select → auth_url 事件 → manual_code → progress。
        Assert.Equal(2, interaction.Prompts.Count);
        Assert.IsType<AuthPrompt.Select>(interaction.Prompts[0]);
        var manual = Assert.IsType<AuthPrompt.ManualCode>(interaction.Prompts[1]);
        Assert.Equal("Paste the code Anthropic shows after you sign in:", manual.Message);
        Assert.Equal("code#state", manual.Placeholder);

        var authUrl = interaction.AuthUrl!;
        Assert.StartsWith("https://claude.ai/oauth/authorize?", authUrl.Url);
        Assert.Equal(
            "Complete login in your browser, then copy the code Anthropic shows and paste it here.",
            authUrl.Instructions);
        var url = authUrl.Url;
        Assert.Equal("true", QueryParam(url, "code"));
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", QueryParam(url, "client_id"));
        Assert.Equal("code", QueryParam(url, "response_type"));
        Assert.Equal("https://platform.claude.com/oauth/code/callback", QueryParam(url, "redirect_uri"));
        Assert.Equal(
            "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload",
            QueryParam(url, "scope"));
        Assert.Equal("S256", QueryParam(url, "code_challenge_method"));
        // challenge = S256(verifier)，verifier 即 state。
        var verifier = QueryParam(url, "state");
        Assert.Equal(
            Pkce.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier!))),
            QueryParam(url, "code_challenge"));
        Assert.Contains(new AuthEvent.Progress("Exchanging authorization code for tokens..."), interaction.Events);

        // 交换请求：POST token 端点，body 全键（pi 同形）。
        var request = Assert.Single(mock.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(AnthropicTokenUrl, request.Url);
        var body = JsonNode.Parse(Encoding.UTF8.GetString(request.Body.Span))!.AsObject();
        Assert.Equal("authorization_code", body["grant_type"]!.GetValue<string>());
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", body["client_id"]!.GetValue<string>());
        Assert.Equal("auth-code", body["code"]!.GetValue<string>());
        Assert.Equal(verifier, body["state"]!.GetValue<string>());
        Assert.Equal("https://platform.claude.com/oauth/code/callback", body["redirect_uri"]!.GetValue<string>());
        Assert.Equal(verifier, body["code_verifier"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_CopyCode_State_Mismatch_Throws_Without_Exchange()
    {
        var mock = new MockFetch();
        var oauth = new AnthropicOAuth(mock.Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.CopyCode("copy_code", "auth-code#wrong-state"), default),
                null, default));

        Assert.Equal("OAuth state mismatch", error.Message);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task Anthropic_CopyCode_Missing_Code_Throws_Without_Exchange()
    {
        var mock = new MockFetch();
        var oauth = new AnthropicOAuth(mock.Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.CopyCode("copy_code", "   "), default), null, default));

        Assert.Equal("Missing authorization code", error.Message);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task Anthropic_Browser_Method_Is_Not_Supported_In_S5()
    {
        var oauth = new AnthropicOAuth(new MockFetch().Fetch);

        // browser 分支依赖 loopback 回调服务器（D20，后置）——报 NotSupportedException。
        var browserError = await Assert.ThrowsAsync<NotSupportedException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.CopyCode("browser"), default), null, default));
        Assert.Contains("D20", browserError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_Unknown_Method_Throws_Pi_Message()
    {
        var oauth = new AnthropicOAuth(new MockFetch().Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.CopyCode("carrier-pigeon"), default),
                null, default));

        Assert.Equal("Unknown Anthropic login method: carrier-pigeon", error.Message);
    }

    [Fact]
    public async Task Anthropic_Refresh_Exchanges_Refresh_Token()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"access_token\":\"at-2\",\"refresh_token\":\"rt-2\",\"expires_in\":7200}");
        long now = 2_000_000_000_000L;
        var oauth = new AnthropicOAuth(mock.Fetch) { Now = () => now };
        var credential = new OAuthCredential
        {
            RefreshToken = "old-rt",
            AccessToken = "old-at",
            ExpiresAt = now - 1,
        };

        var refreshed = await oauth.ToOAuthAuth().Refresh(credential, default);

        Assert.Equal("at-2", refreshed.AccessToken);
        Assert.Equal("rt-2", refreshed.RefreshToken);
        Assert.Equal(now + 7_200_000 - 5 * 60 * 1000, refreshed.ExpiresAt);

        var request = Assert.Single(mock.Requests);
        Assert.Equal(AnthropicTokenUrl, request.Url);
        var body = JsonNode.Parse(Encoding.UTF8.GetString(request.Body.Span))!.AsObject();
        Assert.Equal("refresh_token", body["grant_type"]!.GetValue<string>());
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", body["client_id"]!.GetValue<string>());
        Assert.Equal("old-rt", body["refresh_token"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_ToAuth_Returns_Access_Token_As_ApiKey()
    {
        var oauth = new AnthropicOAuth(new MockFetch().Fetch);

        var auth = await oauth.ToOAuthAuth().ToAuth(
            new OAuthCredential { RefreshToken = "r", AccessToken = "access-9", ExpiresAt = 1 }, default);

        Assert.Equal("access-9", auth.ApiKey);
    }

    [Fact]
    public void Anthropic_OAuthAuth_Record_Carries_Pi_Metadata()
    {
        var auth = new AnthropicOAuth(new MockFetch().Fetch).ToOAuthAuth();

        Assert.Equal("Anthropic (Claude Pro/Max)", auth.Name);
        Assert.True(auth.IsSubscription);
        Assert.Null(auth.LoginLabel);
    }

    [Fact]
    public async Task Anthropic_Exchange_Http_Error_Wraps_Details()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"error\":\"invalid_grant\"}", 400);
        var oauth = new AnthropicOAuth(mock.Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.CopyCode("copy_code", "auth-code"), default), null, default));

        // pi：postJson 的 HTTP 错误再包一层 "Token exchange request failed. ... details=..."。
        Assert.StartsWith("Token exchange request failed.", error.Message);
        Assert.Contains($"url={AnthropicTokenUrl}", error.Message);
        Assert.Contains("details=InvalidOperationException:", error.Message);
        Assert.Contains("HTTP request failed. status=400", error.Message);
        Assert.Contains("{\"error\":\"invalid_grant\"}", error.Message);
    }

    [Theory]
    [InlineData("{\"access_token\":\"at\",\"refresh_token\":\"rt\"}")] // 缺 expires_in
    [InlineData("{\"refresh_token\":\"rt\",\"expires_in\":1}")]        // 缺 access_token
    [InlineData("{\"access_token\":\"at\",\"expires_in\":1}")]         // 缺 refresh_token
    public async Task Anthropic_Exchange_Missing_Field_Throws_Invalid_Json(string body)
    {
        var mock = new MockFetch();
        mock.EnqueueText(body);
        var oauth = new AnthropicOAuth(mock.Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.CopyCode("copy_code", "auth-code"), default), null, default));

        Assert.StartsWith("Token exchange returned invalid JSON.", error.Message);
        Assert.Contains($"url={AnthropicTokenUrl}", error.Message);
        Assert.Contains(body, error.Message);
    }

    [Fact]
    public async Task Anthropic_Refresh_Invalid_Json_Throws_Pi_Message()
    {
        var mock = new MockFetch();
        mock.EnqueueText("not-json");
        var oauth = new AnthropicOAuth(mock.Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Refresh(
                new OAuthCredential { RefreshToken = "r", AccessToken = "a", ExpiresAt = 1 }, default));

        Assert.StartsWith("Anthropic token refresh returned invalid JSON.", error.Message);
        Assert.Contains($"url={AnthropicTokenUrl}", error.Message);
        Assert.Contains("not-json", error.Message);
    }

    [Fact]
    public async Task Anthropic_Without_Fetch_Throws_Clear_Error_At_Exchange()
    {
        // 无 fetch：Ai 不引用 HttpClient，宿主必须注入——缺失时在交换点报清楚，绝不静默。
        var oauth = new AnthropicOAuth(null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.CopyCode("copy_code", "auth-code"), default), null, default));

        Assert.Contains("FetchFunction", error.Message);
    }

    // ------------------------------------------------------------------
    // OpenRouterOAuth：manual code → 永久 API key
    // ------------------------------------------------------------------

    [Fact]
    public async Task OpenRouter_Manual_Login_Exchanges_Code_For_Permanent_Key()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"key\":\"sk-or-test-1\"}");
        var oauth = new OpenRouterOAuth(mock.Fetch);

        // 粘贴串 = 授权页 callback_url + ?code=（pi 手动分支同形：用户从地址栏复制）。
        CannedInteraction interaction = null!;
        interaction = CannedInteraction.ManualOpenRouter(() =>
        {
            var url = interaction.AuthUrl!.Url;
            return $"{QueryParam(url, "callback_url")}?code=manual-code";
        });

        var credential = await oauth.ToOAuthAuth().Login(
            new ProviderAuthInteraction(interaction, default), null, default);

        // 永久 key：refresh 空、expires = JS MAX_SAFE_INTEGER（pi 同值）。
        Assert.Equal("sk-or-test-1", credential.AccessToken);
        Assert.Equal("", credential.RefreshToken);
        Assert.Equal(PermanentExpiresAt, credential.ExpiresAt);

        // 提示：manual_code，placeholder=redirect。
        var manual = Assert.IsType<AuthPrompt.ManualCode>(Assert.Single(interaction.Prompts));
        Assert.Equal(
            "Complete sign-in in your browser, or paste the authorization code / redirect URL here:",
            manual.Message);

        var url = interaction.AuthUrl!.Url;
        Assert.StartsWith("https://openrouter.ai/auth?", url);
        Assert.Equal(
            "Complete sign-in in your browser. If the browser is on another machine, paste the final redirect URL here.",
            interaction.AuthUrl!.Instructions);
        Assert.Equal("S256", QueryParam(url, "code_challenge_method"));
        var challenge = QueryParam(url, "code_challenge");

        // 交换：POST /api/v1/auth/keys，code_verifier 与授权页 challenge 配对。
        var request = Assert.Single(mock.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(OpenRouterTokenUrl, request.Url);
        var body = JsonNode.Parse(Encoding.UTF8.GetString(request.Body.Span))!.AsObject();
        Assert.Equal("manual-code", body["code"]!.GetValue<string>());
        Assert.Equal("S256", body["code_challenge_method"]!.GetValue<string>());
        Assert.Equal(
            challenge,
            Pkce.Base64UrlEncode(SHA256.HashData(
                Encoding.UTF8.GetBytes(body["code_verifier"]!.GetValue<string>()))));
        Assert.Contains(new AuthEvent.Progress("Exchanging authorization code for an API key..."), interaction.Events);
    }

    [Fact]
    public async Task OpenRouter_Manual_Login_Accepts_Bare_Code()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"key\":\"sk-or-manual\"}");
        var oauth = new OpenRouterOAuth(mock.Fetch);

        var credential = await oauth.ToOAuthAuth().Login(
            new ProviderAuthInteraction(CannedInteraction.ManualOpenRouter(() => "  bare-code  "), default),
            null, default);

        Assert.Equal("sk-or-manual", credential.AccessToken);
        var body = JsonNode.Parse(
            Encoding.UTF8.GetString(Assert.Single(mock.Requests).Body.Span))!.AsObject();
        Assert.Equal("bare-code", body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenRouter_Missing_Code_Throws_Without_Exchange()
    {
        var mock = new MockFetch();
        var oauth = new OpenRouterOAuth(mock.Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.ManualOpenRouter(() => "   "), default),
                null, default));

        Assert.Equal("Missing authorization code", error.Message);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task OpenRouter_Refresh_Is_NoOp_Without_Network()
    {
        var mock = new MockFetch();
        var oauth = new OpenRouterOAuth(mock.Fetch);
        var credential = new OAuthCredential
        {
            RefreshToken = "",
            AccessToken = "sk-or-1",
            ExpiresAt = PermanentExpiresAt,
        };

        var refreshed = await oauth.ToOAuthAuth().Refresh(credential, default);

        Assert.Equal(credential, refreshed);
        Assert.Equal(0, mock.CallCount);
    }

    [Fact]
    public async Task OpenRouter_ToAuth_Returns_Key_As_ApiKey()
    {
        var oauth = new OpenRouterOAuth(new MockFetch().Fetch);

        var auth = await oauth.ToOAuthAuth().ToAuth(
            new OAuthCredential { RefreshToken = "", AccessToken = "sk-or-2", ExpiresAt = 1 }, default);

        Assert.Equal("sk-or-2", auth.ApiKey);
    }

    [Fact]
    public void OpenRouter_OAuthAuth_Record_Carries_Pi_Metadata()
    {
        var auth = new OpenRouterOAuth(new MockFetch().Fetch).ToOAuthAuth();

        Assert.Equal("OpenRouter OAuth", auth.Name);
        Assert.False(auth.IsSubscription);
        Assert.Equal("Sign in with OpenRouter", auth.LoginLabel);
    }

    [Fact]
    public async Task OpenRouter_Key_Exchange_Error_Carries_Detail()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"error\":{\"message\":\"invalid code\"}}", 403);
        var oauth = new OpenRouterOAuth(mock.Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.ManualOpenRouter(() => "bad-code"), default),
                null, default));

        Assert.Equal("OpenRouter OAuth key exchange failed (HTTP 403): invalid code", error.Message);
    }

    [Fact]
    public async Task OpenRouter_Error_Detail_Prefers_Error_Description()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"error_description\":\"nope\",\"message\":\"ignored\"}", 400);
        var oauth = new OpenRouterOAuth(mock.Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.ManualOpenRouter(() => "bad-code"), default),
                null, default));

        Assert.Equal("OpenRouter OAuth key exchange failed (HTTP 400): nope", error.Message);
    }

    [Fact]
    public async Task OpenRouter_Success_Without_Key_Throws_Pi_Message()
    {
        var mock = new MockFetch();
        mock.EnqueueText("{\"user_id\":\"user-1\"}");
        var oauth = new OpenRouterOAuth(mock.Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.ManualOpenRouter(() => "some-code"), default),
                null, default));

        Assert.Equal("OpenRouter OAuth response carries no \"key\"", error.Message);
    }

    [Fact]
    public async Task OpenRouter_Success_With_Invalid_Json_Throws_Invalid_Json()
    {
        var mock = new MockFetch();
        mock.EnqueueText("not-json");
        var oauth = new OpenRouterOAuth(mock.Fetch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(CannedInteraction.ManualOpenRouter(() => "some-code"), default),
                null, default));

        Assert.Equal("OpenRouter OAuth returned invalid JSON", error.Message);
    }

    [Fact]
    public async Task OpenRouter_Cancelled_Prompt_Does_Not_Exchange()
    {
        var mock = new MockFetch();
        var oauth = new OpenRouterOAuth(mock.Fetch);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            oauth.ToOAuthAuth().Login(
                new ProviderAuthInteraction(new CancellingInteraction(), default), null, cts.Token));

        Assert.Equal(0, mock.CallCount);
    }

    // ------------------------------------------------------------------
    // 测试夹具
    // ------------------------------------------------------------------

    /// <summary>按提示类型回填答案的交互假体；记录全部提示与事件。</summary>
    private sealed class CannedInteraction(Func<AuthPrompt, string> answer) : AuthInteraction
    {
        private readonly Func<AuthPrompt, string> _answer = answer;

        public List<AuthPrompt> Prompts { get; } = new();
        public List<AuthEvent> Events { get; } = new();
        public AuthEvent.AuthUrl? AuthUrl => Events.OfType<AuthEvent.AuthUrl>().FirstOrDefault();

        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(_answer(prompt));
        }

        public void Notify(AuthEvent @event) => Events.Add(@event);

        /// <summary>Anthropic copy-code 交互：select 答案由调用序推进（第一问=登录方式）。</summary>
        public static CannedInteraction CopyCode(string firstAnswer, string? manualAnswer = null)
        {
            var selectAnswer = firstAnswer;
            return new CannedInteraction(prompt => prompt switch
            {
                AuthPrompt.Select => selectAnswer,
                AuthPrompt.ManualCode => manualAnswer ?? selectAnswer,
                _ => throw new InvalidOperationException($"unexpected prompt {prompt.GetType().Name}"),
            });
        }

        /// <summary>OpenRouter manual 交互：单次 manual_code 提示，答案由工厂给出。</summary>
        public static CannedInteraction ManualOpenRouter(Func<string> code)
            => new(_ => code());
    }

    /// <summary>提示一律抛取消的交互假体。</summary>
    private sealed class CancellingInteraction : AuthInteraction
    {
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
            => Task.FromException<string>(new OperationCanceledException(cancellationToken));

        public void Notify(AuthEvent @event)
        {
        }
    }

    private static string? QueryParam(string url, string name)
    {
        var query = url[(url.IndexOf('?') + 1)..];
        foreach (var pair in query.Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0) continue;
            if (Uri.UnescapeDataString(pair[..separator]) == name)
                return Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }
        return null;
    }
}
