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
/// Anthropic OAuth 流（Claude Pro/Max）。对应 pi auth/oauth/anthropic.ts / pi.net AnthropicOAuth.cs。
/// S5 只实现 copy-code/manual 分支——browser 分支依赖 loopback 回调服务器（spec §10 C 偏离，
/// 后置；选中即报 NotSupportedException）；token 交换走注入的 <see cref="FetchFunction"/>
/// （B/D 偏离：pi 全局 fetch / pi.net HttpClient）。
/// </summary>
public sealed class AnthropicOAuth
{
    // atob("OWQxYzI1MGEtZTYxYi00NGQ5LTg4ZWQtNTk0NGQxOTYyZjVl")，保持与源码相同的编码形态。
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string CopyCodeRedirectUri = "https://platform.claude.com/oauth/code/callback";
    private const string BrowserLoginMethod = "browser";
    private const string CopyCodeLoginMethod = "copy_code";
    private const string Scopes =
        "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    /// <summary>expires_in 扣掉的 5 分钟余量（pi 同式）。</summary>
    private const long ExpiresSlackMs = 5 * 60 * 1000;

    private readonly FetchFunction? _fetch;

    public AnthropicOAuth(FetchFunction? fetch = null) => _fetch = fetch;

    /// <summary>时钟（测试注入）。pi 用 Date.now()。</summary>
    public Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public string Name => "Anthropic (Claude Pro/Max)";

    public bool IsSubscription => true;

    /// <summary>装配成 provider 声明用的 OAuthAuth 记录（pi 的 anthropicOAuth 对象）。</summary>
    public OAuthAuth ToOAuthAuth() => new()
    {
        Name = Name,
        IsSubscription = IsSubscription,
        Login = (interaction, options, cancellationToken) => LoginAsync(interaction, options, cancellationToken),
        Refresh = RefreshAsync,
        ToAuth = ToAuthAsync,
    };

    public async Task<OAuthCredential> LoginAsync(
        ProviderAuthInteraction interaction,
        LoginOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var method = await interaction.PromptAsync(
            new AuthPrompt.Select
            {
                Message = "Select Anthropic login method:",
                Options = new[]
                {
                    new AuthPromptOption(BrowserLoginMethod, "Browser login (default)"),
                    new AuthPromptOption(CopyCodeLoginMethod, "Copy code login (headless)"),
                },
                CancellationToken = cancellationToken,
            },
            cancellationToken).ConfigureAwait(false);

        if (method == CopyCodeLoginMethod)
        {
            return await LoginByCopyCodeAsync(interaction, cancellationToken).ConfigureAwait(false);
        }

        // browser 分支的 loopback 回调服务器按 spec §10 C 偏离后置；未知方式沿用 pi 文案。
        if (method == BrowserLoginMethod)
        {
            throw new NotSupportedException(
                "Anthropic browser login requires the loopback callback server, which is deferred; use copy_code login.");
        }
        throw new InvalidOperationException($"Unknown Anthropic login method: {method}");
    }

    /// <summary>刷新 Anthropic OAuth 令牌。对应 TS <c>refreshAnthropicToken</c>。</summary>
    public async Task<OAuthCredential> RefreshAsync(
        OAuthCredential credential,
        CancellationToken cancellationToken = default)
    {
        string responseBody;
        try
        {
            responseBody = await PostTokenJsonAsync(new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = credential.RefreshToken,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Anthropic token refresh request failed. url={TokenUrl}; details={FormatErrorDetails(error)}");
        }

        if (!TryReadTokenResponse(responseBody, out var access, out var refresh, out var expiresIn))
        {
            throw new InvalidOperationException(
                $"Anthropic token refresh returned invalid JSON. url={TokenUrl}; body={responseBody}");
        }

        return new OAuthCredential
        {
            RefreshToken = refresh,
            AccessToken = access,
            ExpiresAt = Now() + (long)(expiresIn * 1000) - ExpiresSlackMs,
        };
    }

    public Task<ModelAuth> ToAuthAsync(OAuthCredential credential, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModelAuth { ApiKey = credential.AccessToken });
    }

    private async Task<OAuthCredential> LoginByCopyCodeAsync(
        ProviderAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        var pkce = Pkce.Generate();
        var authorizeUrl = BuildAuthorizeUrl(CopyCodeRedirectUri, pkce.Challenge, pkce.Verifier);
        interaction.Notify(new AuthEvent.AuthUrl(
            authorizeUrl,
            "Complete login in your browser, then copy the code Anthropic shows and paste it here."));

        var input = await interaction.PromptAsync(
            new AuthPrompt.ManualCode
            {
                Message = "Paste the code Anthropic shows after you sign in:",
                Placeholder = "code#state",
                CancellationToken = cancellationToken,
            },
            cancellationToken).ConfigureAwait(false);

        var parsed = OAuthInput.ParseAuthorizationInput(input);
        if (parsed.State is { } pastedState && pastedState != pkce.Verifier)
        {
            throw new InvalidOperationException("OAuth state mismatch");
        }
        if (string.IsNullOrEmpty(parsed.Code))
        {
            throw new InvalidOperationException("Missing authorization code");
        }

        interaction.Notify(new AuthEvent.Progress("Exchanging authorization code for tokens..."));
        return await ExchangeAuthorizationCodeAsync(
            parsed.Code!, parsed.State ?? pkce.Verifier, pkce.Verifier, CopyCodeRedirectUri, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OAuthCredential> ExchangeAuthorizationCodeAsync(
        string code,
        string state,
        string verifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        string responseBody;
        try
        {
            responseBody = await PostTokenJsonAsync(new JsonObject
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["state"] = state,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Token exchange request failed. url={TokenUrl}; redirect_uri={redirectUri}; "
                + $"response_type=authorization_code; details={FormatErrorDetails(error)}");
        }

        if (!TryReadTokenResponse(responseBody, out var access, out var refresh, out var expiresIn))
        {
            throw new InvalidOperationException(
                $"Token exchange returned invalid JSON. url={TokenUrl}; body={responseBody}");
        }

        return new OAuthCredential
        {
            RefreshToken = refresh,
            AccessToken = access,
            ExpiresAt = Now() + (long)(expiresIn * 1000) - ExpiresSlackMs,
        };
    }

    private async Task<string> PostTokenJsonAsync(JsonObject body, CancellationToken cancellationToken)
        => await OAuthHttp.PostJsonAsync(RequireFetch(), TokenUrl, body, cancellationToken).ConfigureAwait(false);

    private FetchFunction RequireFetch() => _fetch ?? throw new InvalidOperationException(
        $"{Name} OAuth needs an injected FetchFunction (Ai carries no HttpClient); "
        + "construct AnthropicOAuth with a fetch delegate.");

    /// <summary>access_token/refresh_token/expires_in 三键齐全才算有效（pi.net 同口径的显式校验）。</summary>
    private static bool TryReadTokenResponse(string responseBody, out string access, out string refresh, out double expiresIn)
    {
        access = string.Empty;
        refresh = string.Empty;
        expiresIn = 0;
        var json = OAuthHttp.ParseJsonObject(responseBody);
        if (json is null) return false;
        if (json["access_token"] is not JsonValue accessValue
            || !accessValue.TryGetValue(out string? accessText) || accessText is null) return false;
        if (json["refresh_token"] is not JsonValue refreshValue
            || !refreshValue.TryGetValue(out string? refreshText) || refreshText is null) return false;
        if (json["expires_in"] is not JsonValue expiresValue || !expiresValue.TryGetValue(out expiresIn)) return false;
        access = accessText;
        refresh = refreshText;
        return true;
    }

    private static string BuildAuthorizeUrl(string redirectUri, string challenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["code"] = "true",
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        // URLSearchParams 语义：application/x-www-form-urlencoded（空格→+）。
        var queryString = string.Join("&", query.Select(pair =>
            $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));
        return $"{AuthorizeUrl}?{queryString}";
    }

    /// <summary>错误细节格式化（TS <c>formatErrorDetails</c> 的等价：类型名 + 消息 + 内层 cause）。</summary>
    private static string FormatErrorDetails(Exception error)
    {
        var details = new List<string> { $"{error.GetType().Name}: {error.Message}" };
        if (error.InnerException is { } inner) details.Add($"cause={FormatErrorDetails(inner)}");
        return string.Join("; ", details);
    }
}
