using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Api;

// 上层助手轮次重试模块（pi utils/retry.ts）在 Utils/Retry.cs（S8/D17）；本文件只做请求级重试。
/// <summary>pi <c>ProviderRetryOptions</c> 的 C# 形状。</summary>
internal sealed class ProviderRetryOptions
{
    /// <summary>pi 默认 0：调用方显式选择重试次数（SDK 请求本身 maxRetries: 0）。</summary>
    public int MaxRetries { get; init; }

    /// <summary>provider 请求的重试延迟上限；0 = 不设限。默认 60 秒。</summary>
    public long MaxRetryDelayMs { get; init; } = ProviderRetry.DefaultMaxRetryDelayMs;

    /// <summary>命中这些 HTTP 状态码时即使默认策略可重试也立即失败（pi noRetryStatuses）。</summary>
    public int[]? NoRetryStatuses { get; init; }

    /// <summary>抖动随机源；null 时现场 new 一个。测试可注入确定值。</summary>
    public Random? Random { get; init; }

    /// <summary>测试注入的 sleep 替身；null 时用 <see cref="Task.Delay"/>。</summary>
    internal Func<long, CancellationToken, Task>? SleepAsync { get; init; }
}

/// <summary>
/// 复刻 OpenAI/Anthropic SDK 的 provider 重试策略（pi <c>utils/provider-retry.ts</c> 语义），
/// 但 backoff sleep 可被取消令牌中止。只在<b>请求建立阶段</b>（首字节前）调用
/// <see cref="RunAsync"/>；流开始后绝不重试——那由协议适配器转为终态 Error。
/// provider 请求的延迟超过 <see cref="ProviderRetryOptions.MaxRetryDelayMs"/> 时立即失败。
/// </summary>
internal static class ProviderRetry
{
    public const long DefaultMaxRetryDelayMs = 60_000;

    /// <summary>
    /// 判定一条 <see cref="FetchException"/> 是否可重试：x-should-retry 头最权威
    /// （true 强制重试、false 禁止）；随后看传输层 <c>IsRetryable</c> 标记；
    /// 无状态码仅在连接建立失败（<see cref="FetchErrorKind.Connect"/>）时重试
    /// （TLS/代理/协议等无状态错误是永久性配置问题，不重试）；
    /// 408/409/429/5xx 重试；其余不重试。
    /// </summary>
    public static bool ShouldRetry(FetchException error)
    {
        if (error is null) throw new ArgumentNullException(nameof(error));

        if (error.Headers is not null && error.Headers.TryGetValue("x-should-retry", out var flag))
        {
            if (string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (error.IsRetryable) return true;
        if (error.StatusCode is null) return error.Kind == FetchErrorKind.Connect;

        var status = error.StatusCode.Value;
        return status is 408 or 409 or 429 || status >= 500;
    }

    /// <summary>
    /// 本次重试等待的毫秒数：retry-after-ms → retry-after（秒 / HTTP 日期）→
    /// 指数退避 <c>min(0.5 * 2^retryIndex, 8) * 1000 * (1 - 0.25 * random)</c>。
    /// provider 给的延迟超过 <paramref name="maxRetryDelayMs"/>（&gt;0 时）抛
    /// <see cref="FetchException"/>。
    /// </summary>
    public static long DelayMs(
        FetchException error,
        int retryIndex,
        long maxRetryDelayMs = DefaultMaxRetryDelayMs,
        Random? random = null)
    {
        if (error is null) throw new ArgumentNullException(nameof(error));

        var headers = error.Headers;
        if (headers is not null)
        {
            if (headers.TryGetValue("retry-after-ms", out var msText) && !string.IsNullOrEmpty(msText)
                && double.TryParse(msText, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                && double.IsFinite(ms))
            {
                return Math.Max(0, ValidateServerDelayMs((long)ms, maxRetryDelayMs, error.Message));
            }

            if (headers.TryGetValue("retry-after", out var afterText) && !string.IsNullOrEmpty(afterText))
            {
                // pi：先按秒数解析；不是数字则按 HTTP 日期（Date.parse）算差值。
                if (double.TryParse(afterText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && double.IsFinite(seconds))
                {
                    return Math.Max(0, ValidateServerDelayMs((long)(seconds * 1000), maxRetryDelayMs, error.Message));
                }

                if (DateTimeOffset.TryParse(
                        afterText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                        out var when))
                {
                    var delay = (long)(when - DateTimeOffset.UtcNow).TotalMilliseconds;
                    return Math.Max(0, ValidateServerDelayMs(delay, maxRetryDelayMs, error.Message));
                }
            }
        }

        // 指数退避 + 抖动：500ms 起、8s 封顶，抖动最多削 25%（pi 同式）。
        var exponential = Math.Min(0.5 * Math.Pow(2, retryIndex), 8) * 1000.0;
        var jitter = 1.0 - 0.25 * (random ?? new Random()).NextDouble();
        return (long)Math.Max(0, exponential * jitter);
    }

    /// <summary>
    /// 以 provider 重试策略执行请求：可重试失败按 <see cref="DelayMs"/> 退避后重试，
    /// 取消令牌在每次重试判定前与 sleep 中即时中止（抛 <see cref="OperationCanceledException"/>）；
    /// 非 <see cref="FetchException"/> 异常一律不重试直接上抛。
    /// </summary>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> request,
        ProviderRetryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        options ??= new ProviderRetryOptions();
        var retriesRemaining = options.MaxRetries;

        while (true)
        {
            try
            {
                return await request(cancellationToken).ConfigureAwait(false);
            }
            catch (FetchException error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retriesRemaining <= 0 || !ShouldRetry(error)) throw;
                if (error.StatusCode is int status
                    && options.NoRetryStatuses is { } noRetryStatuses
                    && Array.IndexOf(noRetryStatuses, status) >= 0)
                {
                    throw;
                }

                var retryIndex = options.MaxRetries - retriesRemaining;
                retriesRemaining--;
                var delayMs = DelayMs(error, retryIndex, options.MaxRetryDelayMs, options.Random);
                if (delayMs > 0)
                {
                    var sleep = options.SleepAsync
                                ?? ((ms, token) => Task.Delay(TimeSpan.FromMilliseconds(ms), token));
                    await sleep(delayMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static long ValidateServerDelayMs(long delayMs, long maxRetryDelayMs, string providerErrorMessage)
    {
        if (maxRetryDelayMs > 0 && delayMs > maxRetryDelayMs)
        {
            throw new FetchException(
                FetchErrorKind.Protocol,
                $"Server requested {Math.Ceiling(delayMs / 1000.0)}s retry delay "
                + $"(max: {Math.Ceiling(maxRetryDelayMs / 1000.0)}s). {providerErrorMessage}");
        }

        return delayMs;
    }
}
