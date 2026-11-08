using System;
using System.Collections.Generic;

namespace PiUnityAgent.Ai.Types;

/// <summary>pi ClientIdentityTarget：请求发往的 provider 与 endpoint。</summary>
public sealed record ClientIdentityTarget(string Provider, string BaseUrl);

/// <summary>
/// pi utils/identity.ts 的 C# 移植：client identity → 请求头（User-Agent / attribution / OpenCode session）。
/// API 层在 model、provider、request 头之下应用这些默认头，因此已配置的头可覆盖它们。
/// </summary>
public static class ClientIdentityHelpers
{
    private const string NvidiaNimHost = "integrate.api.nvidia.com";
    private const string OpenCodeHost = "opencode.ai";

    /// <summary>pi PI_ATTRIBUTION：pi 自身的归属标识（pi-agent-core / pi-durable / coding agent 使用）。</summary>
    public static ClientAttribution PiAttribution { get; } =
        new("pi", "https://pi.dev", new[] { "cli-agent" });

    /// <summary>
    /// pi getClientVersion：identity.version 优先；缺省用 Ai 程序集版本
    /// （pi 用 PI_AI_VERSION 常量，C# 移植没有该常量，按 brief 以程序集版本替代）。
    /// </summary>
    public static string GetClientVersion(ClientIdentity? identity = null)
    {
        if (identity?.Version is { Length: > 0 } version) return version;
        var assemblyVersion = typeof(ClientIdentityHelpers).Assembly.GetName().Version;
        if (assemblyVersion is null) return "0.0.0";
        return assemblyVersion.Build >= 0
            ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}";
    }

    /// <summary>
    /// pi formatUserAgent：Custom 原样；Short 为 "pi"；Detailed 为 `pi/&lt;version&gt; (&lt;platform&gt;; &lt;runtime&gt;; &lt;arch&gt;)`。
    /// Unity/netstandard2.1 无 Node/Bun runtime 可探测（pi 的 runtimeDescription 只看 process.versions），
    /// 走 pi 的非 Node 分支输出 `pi/&lt;version&gt; (browser)`。
    /// </summary>
    public static string FormatUserAgent(ClientIdentity? identity = null)
    {
        var mode = identity?.UserAgent ?? UserAgentMode.Detailed;
        if (mode.CustomValue is { } custom) return custom;
        if (mode.IsShort) return "pi";
        return $"pi/{GetClientVersion(identity)} (browser)";
    }

    /// <summary>
    /// pi getAttributionHeaders：identity 的 provider 归属头按请求目标映射；归属关闭（attribution 为 null）或目标不匹配时为空。
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetAttributionHeaders(
        ClientIdentityTarget target,
        ClientIdentity? identity = null)
    {
        var attribution = identity?.Attribution;
        if (attribution is null) return new Dictionary<string, string>();

        if (IsOpenRouter(target))
        {
            var openRouter = new Dictionary<string, string>
            {
                ["HTTP-Referer"] = attribution.Url,
                ["X-OpenRouter-Title"] = attribution.Name,
            };
            if (attribution.Categories is { Count: > 0 })
            {
                openRouter["X-OpenRouter-Categories"] = string.Join(",", attribution.Categories);
            }
            return openRouter;
        }

        // NVIDIA 期望首字母大写的 origin，例如 `Pi`。
        if (IsNvidiaNim(target))
        {
            return new Dictionary<string, string>
            {
                ["X-BILLING-INVOKE-ORIGIN"] = Capitalize(attribution.Name),
            };
        }

        if (IsOpenCode(target))
        {
            return new Dictionary<string, string>
            {
                ["x-opencode-client"] = attribution.Name,
            };
        }

        return new Dictionary<string, string>();
    }

    /// <summary>
    /// pi getOpenCodeSessionHeaders：OpenCode 的按会话路由头（含指向 OpenCode 的自定义 provider）；
    /// 无会话或非 OpenCode 目标时为 null。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? GetOpenCodeSessionHeaders(
        ClientIdentityTarget target,
        string? sessionId)
        => !string.IsNullOrEmpty(sessionId) && IsOpenCode(target)
            ? new Dictionary<string, string> { ["x-opencode-session"] = sessionId }
            : null;

    /// <summary>pi getIdentityHeaders：请求默认头 = User-Agent + provider attribution。</summary>
    public static IReadOnlyDictionary<string, string> GetIdentityHeaders(
        ClientIdentityTarget target,
        ClientIdentity? identity = null)
    {
        var headers = new Dictionary<string, string> { ["User-Agent"] = FormatUserAgent(identity) };
        foreach (var entry in GetAttributionHeaders(target, identity))
        {
            headers[entry.Key] = entry.Value;
        }
        return headers;
    }

    private static bool MatchesHost(string baseUrl, string expectedHost)
        => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenRouter(ClientIdentityTarget target)
        => target.Provider == "openrouter"
            || target.BaseUrl.Contains("openrouter.ai", StringComparison.Ordinal);

    private static bool IsNvidiaNim(ClientIdentityTarget target)
        => target.Provider == "nvidia" || MatchesHost(target.BaseUrl, NvidiaNimHost);

    private static bool IsOpenCode(ClientIdentityTarget target)
        => target.Provider == "opencode"
            || target.Provider == "opencode-go"
            || MatchesHost(target.BaseUrl, OpenCodeHost);

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
