using System;
using System.Collections.Generic;
using System.Net;

namespace PiUnityAgent.Ai.Providers.Auth;

/// <summary>
/// 回调地址解析辅助（多家流程共用）。对应 pi anthropic.ts <c>parseAuthorizationInput</c>
/// （code+state 语义）与 openrouter.ts <c>parseAuthorizationInput</c>（只取 code 语义）。
/// netstandard2.1 无 System.Web.HttpUtility，查询串解析手写 + WebUtility.UrlDecode。
/// </summary>
public static class OAuthInput
{
    /// <summary>
    /// 解析用户粘贴的授权输入：完整重定向 URL、<c>code#state</c>、查询串或裸 code。
    /// </summary>
    public static (string? Code, string? State) ParseAuthorizationInput(string input)
    {
        var value = input.Trim();
        if (value.Length == 0) return (null, null);

        if (Uri.TryCreate(value, UriKind.Absolute, out var url))
        {
            var query = ParseQueryPairs(url.Query);
            return (QueryValue(query, "code"), QueryValue(query, "state"));
        }

        var hashIndex = value.IndexOf('#');
        if (hashIndex >= 0)
        {
            var code = value[..hashIndex];
            var state = value[(hashIndex + 1)..];
            return (code.Length > 0 ? code : null, state.Length > 0 ? state : null);
        }

        if (value.Contains("code=", StringComparison.Ordinal))
        {
            var query = ParseQueryPairs(value);
            return (QueryValue(query, "code"), QueryValue(query, "state"));
        }

        return (value, null);
    }

    /// <summary>OpenRouter 语义：从粘贴输入提取 code（URL/查询串/裸 code），无 state。</summary>
    public static string? ParseAuthorizationCode(string input)
    {
        var value = input.Trim();
        if (value.Length == 0) return null;

        if (Uri.TryCreate(value, UriKind.Absolute, out var url))
            return QueryValue(ParseQueryPairs(url.Query), "code");

        if (value.Contains("code=", StringComparison.Ordinal))
            return QueryValue(ParseQueryPairs(value), "code");

        return value;
    }

    /// <summary>解析 <c>?a=1&amp;b=2</c> 形查询串（容忍前导 '?'、缺 '=' 的键）；值 URL 解码。</summary>
    internal static Dictionary<string, string> ParseQueryPairs(string query)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = query.StartsWith('?') ? query[1..] : query;
        if (text.Length == 0) return pairs;

        foreach (var segment in text.Split('&'))
        {
            if (segment.Length == 0) continue;
            var separator = segment.IndexOf('=');
            var key = separator < 0 ? segment : segment[..separator];
            var rawValue = separator < 0 ? string.Empty : segment[(separator + 1)..];
            pairs[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(rawValue);
        }
        return pairs;
    }

    private static string? QueryValue(Dictionary<string, string> pairs, string key)
        => pairs.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
}
