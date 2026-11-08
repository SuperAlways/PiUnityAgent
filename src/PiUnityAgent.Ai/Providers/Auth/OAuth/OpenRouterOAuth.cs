using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers.Auth;

/// <summary>
/// OpenRouter OAuth PKCE 流。对应 pi auth/oauth/openrouter.ts / pi.net OpenRouterOAuth.cs：
/// 授权码换取永久用户受控 API key（而非 access/refresh 对），Refresh no-op。
/// S5 只实现 manual 粘贴分支——loopback 回调服务器按 spec §10 C 偏离后置：pi 绑定临时端口
/// 并把 <c>callback.redirectUri</c> 传给授权页；这里改用固定回环 redirect（可注入覆盖），
/// 浏览器跳转失败时用户仍可从地址栏复制 URL / 粘贴 code。
/// </summary>
public sealed class OpenRouterOAuth
{
    private const string AuthorizeUrl = "https://openrouter.ai/auth";
    private const string TokenUrl = "https://openrouter.ai/api/v1/auth/keys";

    private const int TokenExchangeTimeoutMs = 30_000;

    /// <summary>JS Number.MAX_SAFE_INTEGER：永久 key 用 JS 安全整数上界表达"不过期"（pi 同值）。</summary>
    public const long PermanentExpiresAt = 9007199254740991;

    /// <summary>manual-only 的回环 redirect（C 偏离；宿主可用 redirectUri 参数覆盖）。</summary>
    public const string DefaultRedirectUri = "http://localhost:53693/oauth/callback";

    private readonly FetchFunction? _fetch;
    private readonly string _redirectUri;

    public OpenRouterOAuth(FetchFunction? fetch = null, string? redirectUri = null)
    {
        _fetch = fetch;
        _redirectUri = redirectUri ?? DefaultRedirectUri;
    }

    public string Name => "OpenRouter OAuth";

    public bool IsSubscription => false;

    public string LoginLabel => "Sign in with OpenRouter";

    /// <summary>装配成 provider 声明用的 OAuthAuth 记录（pi 的 openRouterOAuth 对象）。</summary>
    public OAuthAuth ToOAuthAuth() => new()
    {
        Name = Name,
        IsSubscription = IsSubscription,
        LoginLabel = LoginLabel,
        Login = (interaction, options, cancellationToken) => LoginAsync(interaction, options, cancellationToken),
        // 永久 API key：无可刷新之物。
        Refresh = (credential, _) => Task.FromResult(credential),
        ToAuth = (credential, _) => Task.FromResult(new ModelAuth { ApiKey = credential.AccessToken }),
    };

    public async Task<OAuthCredential> LoginAsync(
        ProviderAuthInteraction interaction,
        LoginOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pkce = Pkce.Generate();
        var authorizeUrl = $"{AuthorizeUrl}?{FormEncode(new Dictionary<string, string>
        {
            ["callback_url"] = _redirectUri,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
        })}";

        interaction.Notify(new AuthEvent.AuthUrl(
            authorizeUrl,
            "Complete sign-in in your browser. If the browser is on another machine, paste the final redirect URL here."));

        var input = await interaction.PromptAsync(
            new AuthPrompt.ManualCode
            {
                Message = "Complete sign-in in your browser, or paste the authorization code / redirect URL here:",
                Placeholder = _redirectUri,
                CancellationToken = cancellationToken,
            },
            cancellationToken).ConfigureAwait(false);

        var code = OAuthInput.ParseAuthorizationCode(input);
        if (code is null)
        {
            throw new InvalidOperationException("Missing authorization code");
        }

        interaction.Notify(new AuthEvent.Progress("Exchanging authorization code for an API key..."));
        return await ExchangeAuthorizationCodeAsync(code, pkce.Verifier, cancellationToken).ConfigureAwait(false);
    }

    public Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 永久 API key：无可刷新之物。
        return Task.FromResult(credential);
    }

    public Task<ModelAuth> ToAuthAsync(OAuthCredential credential, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModelAuth { ApiKey = credential.AccessToken });
    }

    private async Task<OAuthCredential> ExchangeAuthorizationCodeAsync(
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        var (statusCode, text) = await OAuthHttp.SendJsonAsync(
            RequireFetch(),
            TokenUrl,
            new JsonObject
            {
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["code_challenge_method"] = "S256",
            },
            cancellationToken,
            TokenExchangeTimeoutMs).ConfigureAwait(false);

        // pi 的宽容解析：先读体；成功响应解析不出对象才算 invalid JSON。
        var body = OAuthHttp.ParseJsonObject(text);
        if (body is null && statusCode is >= 200 and < 300)
        {
            throw new InvalidOperationException("OpenRouter OAuth returned invalid JSON");
        }

        if (statusCode is < 200 or >= 300)
        {
            var detail = ErrorDetail(body);
            throw new InvalidOperationException(
                $"OpenRouter OAuth key exchange failed (HTTP {statusCode}){(detail is not null ? $": {detail}" : "")}");
        }

        if (body is null
            || body["key"] is not JsonValue keyValue
            || !keyValue.TryGetValue(out string? key)
            || string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException("OpenRouter OAuth response carries no \"key\"");
        }

        return new OAuthCredential
        {
            RefreshToken = "",
            AccessToken = key,
            ExpiresAt = PermanentExpiresAt,
        };
    }

    private FetchFunction RequireFetch() => _fetch ?? throw new InvalidOperationException(
        $"{Name} OAuth needs an injected FetchFunction (Ai carries no HttpClient); "
        + "construct OpenRouterOAuth with a fetch delegate.");

    /// <summary>错误详情：error_description → message → error → error.message（pi 同序）。</summary>
    private static string? ErrorDetail(JsonObject? body)
    {
        if (body is null) return null;
        if (body["error_description"] is JsonValue description && description.TryGetValue(out string? text))
        {
            return text;
        }
        if (body["message"] is JsonValue message && message.TryGetValue(out text))
        {
            return text;
        }
        if (body["error"] is JsonValue error && error.TryGetValue(out text))
        {
            return text;
        }
        if (body["error"] is JsonObject errorObject
            && errorObject["message"] is JsonValue nested
            && nested.TryGetValue(out text))
        {
            return text;
        }
        return null;
    }

    private static string FormEncode(IReadOnlyDictionary<string, string> fields)
        => string.Join("&", fields.Select(pair =>
            $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));
}
