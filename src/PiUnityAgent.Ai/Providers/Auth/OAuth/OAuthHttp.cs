using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers.Auth;

/// <summary>
/// OAuth 流共享 HTTP 辅助：pi 全局 fetch / pi.net HttpClient 的 C# 等价物——
/// 全部走注入的 <see cref="FetchFunction"/>（Ai 不引用 HttpClient/UnityLlmTransport，
/// B/D 偏离，宿主传输负责实际超时）。pi 的 30s 每请求超时映射为 FetchOptions 三超时
/// （与 Api 层 BuildTimeoutOptions 同一约定）。
/// </summary>
public static class OAuthHttp
{
    /// <summary>默认请求超时（毫秒）。对应各流程的 AbortSignal.timeout(30_000)。</summary>
    public const int DefaultTimeoutMs = 30_000;

    /// <summary>
    /// POST JSON 并读响应文本；非 2xx 抛 pi 同文案错误
    /// （<c>HTTP request failed. status=...; url=...; body=...</c>）。
    /// </summary>
    public static async Task<string> PostJsonAsync(
        FetchFunction fetch,
        string url,
        JsonObject body,
        CancellationToken cancellationToken,
        int timeoutMs = DefaultTimeoutMs)
    {
        var (statusCode, text) = await SendJsonAsync(fetch, url, body, cancellationToken, timeoutMs)
            .ConfigureAwait(false);
        if (statusCode < 200 || statusCode >= 300)
        {
            throw new InvalidOperationException(
                $"HTTP request failed. status={statusCode}; url={url}; body={text}");
        }
        return text;
    }

    /// <summary>POST JSON 并返回 (状态码, 响应文本)——OpenRouter 的宽容解析（先读体再判状态）用。</summary>
    public static async Task<(int StatusCode, string Body)> SendJsonAsync(
        FetchFunction fetch,
        string url,
        JsonObject body,
        CancellationToken cancellationToken,
        int timeoutMs = DefaultTimeoutMs)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new FetchRequest(
            "POST",
            url,
            new FetchHeaders
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json",
            },
            Encoding.UTF8.GetBytes(body.ToJsonString()));

        await using var response = await fetch(request, TimeoutOptions(timeoutMs), cancellationToken)
            .ConfigureAwait(false);
        var text = await ReadTextAsync(response).ConfigureAwait(false);
        return (response.StatusCode, text);
    }

    /// <summary>解析 JSON 对象；空串/非对象/解析失败返回 null。对应各流程的 readJson。</summary>
    public static JsonObject? ParseJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读响应体全文（流式读取，消费方负责 dispose 响应）。</summary>
    public static async Task<string> ReadTextAsync(IFetchResponse response)
    {
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static FetchOptions TimeoutOptions(int timeoutMs)
    {
        var span = TimeSpan.FromMilliseconds(timeoutMs);
        return new FetchOptions
        {
            ConnectTimeout = span,
            FirstByteTimeout = span,
            IdleTimeout = span,
        };
    }
}
