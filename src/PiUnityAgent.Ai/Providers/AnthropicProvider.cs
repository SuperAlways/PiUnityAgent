using System;
using System.Collections.Generic;
using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>
/// Anthropic profile（pi providers/anthropic.ts 的 api-key 面）。
/// 认证链：stored key → <c>ANTHROPIC_AUTH_TOKEN</c>（以 <c>Authorization: Bearer</c> 传递）
/// → <c>ANTHROPIC_OAUTH_TOKEN</c>/<c>ANTHROPIC_API_KEY</c>（按序）。
/// 工作负载身份联合（federation env 兜底）后置 <c>TODO(S5,D1)</c>；
/// OAuth（Claude Pro/Max 订阅）在 S5 Task 6 经 LazyOAuth 接入。
/// </summary>
public static class AnthropicProvider
{
    private const string AuthTokenEnv = "ANTHROPIC_AUTH_TOKEN";
    private const string OAuthTokenEnv = "ANTHROPIC_OAUTH_TOKEN";
    private const string ApiKeyEnv = "ANTHROPIC_API_KEY";

    /// <summary>
    /// <paramref name="oauthFetch"/> 非空时挂 Anthropic OAuth（Claude Pro/Max）——经
    /// <see cref="AuthHelpers.LazyOAuth"/> 对齐 pi 的 loadAnthropicOAuth 形状；为空保持
    /// API-key-only（Ai 不引用 HttpClient，宿主负责注入 fetch，B/D 偏离）。
    /// </summary>
    public static IProvider Create(Types.FetchFunction? oauthFetch = null) => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "anthropic",
        Name = "Anthropic",
        BaseUrl = "https://api.anthropic.com",
        Auth = new ProviderAuth
        {
            ApiKey = CreateApiKeyAuth(),
            OAuth = oauthFetch is null
                ? null
                : AuthHelpers.LazyOAuth(
                    "Anthropic (Claude Pro/Max)",
                    () => System.Threading.Tasks.Task.FromResult<OAuthAuth>(new AnthropicOAuth(oauthFetch).ToOAuthAuth()),
                    isSubscription: true),
        },
        Models = ModelCatalog.GetChatModels("anthropic"),
        Api = AnthropicMessages.Streams(),
    });

    private static ApiKeyAuth CreateApiKeyAuth() => new()
    {
        Name = "Anthropic API key",
        Login = async (interaction, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var key = await interaction.PromptAsync(
                new AuthPrompt.Secret { Message = "Enter Anthropic API key", CancellationToken = ct },
                ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ApiKeyCredential(key);
        },
        Resolve = async (input, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (input.Credential?.Key is { Length: > 0 } storedKey)
            {
                return new AuthResult(
                    new ModelAuth { ApiKey = storedKey },
                    input.Credential.Env,
                    "stored credential");
            }

            async Task<string?> EnvAsync(string name)
            {
                ct.ThrowIfCancellationRequested();
                return await input.Context.EnvAsync(name, ct).ConfigureAwait(false);
            }

            var authToken = await EnvAsync(AuthTokenEnv).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(authToken))
            {
                return new AuthResult(
                    new ModelAuth
                    {
                        Headers = new Dictionary<string, string?> { ["Authorization"] = $"Bearer {authToken}" },
                    },
                    null,
                    AuthTokenEnv);
            }

            // TODO(S5,D1): 工作负载身份联合按 pi anthropic.ts:53-69 后置——三必需 env
            // （federation rule/organization/identity token file）+ 两可选（service account/workspace）
            // 收进 AuthResult.Env，source="workload identity federation"；OAuth 走 Task 6 的 LazyOAuth。
            foreach (var envVar in new[] { OAuthTokenEnv, ApiKeyEnv })
            {
                var apiKey = await EnvAsync(envVar).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(apiKey))
                {
                    return new AuthResult(new ModelAuth { ApiKey = apiKey }, null, envVar);
                }
            }

            return null;
        },
    };
}
