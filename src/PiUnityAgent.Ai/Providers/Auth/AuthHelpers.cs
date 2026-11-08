using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Providers.Auth;

/// <summary>
/// pi auth/helpers.ts 的 C# 移植：标准 api-key 认证（EnvApiKeyAuth）与 OAuth 懒加载包装（LazyOAuth）。
/// </summary>
public static class AuthHelpers
{
    /// <summary>
    /// pi envApiKeyAuth：标准 api-key 认证——stored credential key 优先，否则按序取第一个非空 env。
    /// 含提示输入 key 的 login。解析非标准（provider env、环境文件、IAM）的 provider 自行编写 ApiKeyAuth。
    /// </summary>
    public static ApiKeyAuth EnvApiKeyAuth(string name, IReadOnlyList<string> envVars)
    {
        return new ApiKeyAuth
        {
            Name = name,
            Login = async (interaction, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var key = await interaction.PromptAsync(
                    new AuthPrompt.Secret { Message = $"Enter {name}", CancellationToken = ct },
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

                foreach (var envVar in envVars)
                {
                    ct.ThrowIfCancellationRequested();
                    var value = await input.Context.EnvAsync(envVar, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(value))
                    {
                        return new AuthResult(new ModelAuth { ApiKey = value }, null, envVar);
                    }
                }
                return null;
            },
        };
    }

    /// <summary>
    /// pi lazyOAuth：包装动态加载的 OAuthAuth，provider 定义可在不引用实现的情况下声明 OAuth 能力；
    /// 流程在首次 login/refresh/toAuth 时加载且只加载一次。
    /// </summary>
    public static OAuthAuth LazyOAuth(
        string name,
        Func<Task<OAuthAuth>> load,
        bool isSubscription = false,
        string? loginLabel = null)
    {
        if (load is null) throw new ArgumentNullException(nameof(load));
        var gate = new object();
        Task<OAuthAuth>? loaded = null;
        Task<OAuthAuth> Load()
        {
            lock (gate)
            {
                return loaded ??= load();
            }
        }

        return new OAuthAuth
        {
            Name = name,
            IsSubscription = isSubscription,
            LoginLabel = loginLabel,
            Login = async (interaction, options, ct) =>
                await (await Load().ConfigureAwait(false)).Login(interaction, options, ct).ConfigureAwait(false),
            Refresh = async (credential, ct) =>
                await (await Load().ConfigureAwait(false)).Refresh(credential, ct).ConfigureAwait(false),
            ToAuth = async (credential, ct) =>
                await (await Load().ConfigureAwait(false)).ToAuth(credential, ct).ConfigureAwait(false),
        };
    }
}
