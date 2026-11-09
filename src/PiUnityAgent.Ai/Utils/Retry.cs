using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Utils;

/// <summary>pi retry.ts: 有界重试策略；maxRetries 不含首次调用。</summary>
public sealed record RetryPolicy
{
    public bool Enabled { get; init; }
    public int MaxRetries { get; init; }
    public int BaseDelayMs { get; init; }
    public int? MaxAgentDelayMs { get; init; }
}

/// <summary>pi retry.ts RetryCallbacks；回调允许 async（Task）。</summary>
public sealed record RetryCallbacks
{
    public Func<int, int, int, string, Task>? OnRetryScheduled { get; init; }
    public Func<Task>? OnRetryAttemptStart { get; init; }
    public Func<bool, int, string?, Task>? OnRetryFinished { get; init; }
}

public static class Retry
{
    public const int DefaultMaxAgentRetryDelayMs = 60_000;

    // pi retry.ts:7-35：配额/计费/订阅限制——重试也不会成功，必须快速失败。
    private static readonly string[] NonRetryablePatterns =
    {
        "GoUsageLimitError",
        "FreeUsageLimitError",
        "Monthly usage limit reached",
        "available balance",
        "insufficient_quota",
        "out of budget",
        "quota exceeded",
        "billing",
        "subscription_sharing_usage_limit_exceeded",
        "subscription_sharing_workplace_individual_usage_limit_exceeded",
        "\"code\"\\s*:\\s*\"?1308(?![0-9])",
    };

    // pi retry.ts:37-125：瞬态 provider/传输错误。
    private static readonly string[] RetryablePatterns =
    {
        "overloaded",
        "server_busy",
        "servers are currently busy",
        "currently experiencing high demand",
        "model is at capacity",
        "rate.?limit",
        "too many requests",
        "(?<![a-z0-9])(?:429|500|502|503|504|520|524)(?![a-z0-9])",
        "service.?(?:is.?)?(?:temporarily.?)?unavailable",
        "server.?error",
        "internal.?error",
        "provider.?returned.?(?:an.?)?error",
        "exceeded request buffer limit while retrying upstream",
        "unable to forward upstream",
        "upstream.?request.?failed",
        "upstream_stream_read_error",
        "stream was interrupted",
        "http/?2 stream failed",
        "network.?error",
        "connection.?error",
        "connection.?refused",
        "connection.?lost",
        "other side closed",
        "fetch failed",
        "request to \\S+ failed, reason:",
        "getaddrinfo",
        "ENOTFOUND",
        "EAI_AGAIN",
        "upstream.?connect",
        "reset before headers",
        "socket hang up",
        "socket connection was closed",
        "timed? out",
        "timeout",
        "terminated",
        "websocket.?closed",
        "websocket.?error",
        "ended without",
        "stream ended before message_stop",
        "stream ended before a terminal response event",
        "http2 request did not get a response",
        "stream disconnected before completion",
        "pending stream has been canceled",
        "retry delay",
        "you can retry your request",
        "try your request again",
        "please retry your request",
        "ResourceExhausted",
        "subscription_sharing_usage_unavailable",
        "subscription_sharing_user_unavailable",
    };

    private static readonly Regex NonRetryablePattern = Build(NonRetryablePatterns);
    private static readonly Regex RetryablePattern = Build(RetryablePatterns);

    public static int RetryDelayMs(int baseDelayMs, int? maxAgentDelayMs, int attempt)
    {
        var exponent = Math.Max(0, attempt - 1);
        var computed = Math.Max(0, baseDelayMs) * Math.Pow(2, exponent);
        var safe = double.IsInfinity(computed) || computed > int.MaxValue
            ? int.MaxValue
            : (int)Math.Round(computed);
        // JS setTimeout 对负延迟自动钳 0；.NET Task.Delay(< -1) 抛异常、-1 是无限等待，
        // 所以负 cap 必须钳成 0，不能透传。
        var cap = Math.Max(0, maxAgentDelayMs ?? DefaultMaxAgentRetryDelayMs);
        return Math.Min(safe, cap);
    }

    public static int RetryDelayMs(RetryPolicy policy, int attempt)
        => RetryDelayMs(policy.BaseDelayMs, policy.MaxAgentDelayMs, attempt);

    public static bool IsRetryableAssistantError(AssistantMessage message)
    {
        if (message.StopReason != StopReason.Error
            || string.IsNullOrEmpty(message.ErrorMessage))
        {
            return false;
        }

        if (NonRetryablePattern.IsMatch(message.ErrorMessage)) return false;
        return RetryablePattern.IsMatch(message.ErrorMessage);
    }

    public static async Task<AssistantMessage> RetryAssistantCall(
        Func<Task<AssistantMessage>> produce,
        RetryPolicy? policy,
        CancellationToken signal,
        RetryCallbacks? callbacks = null)
    {
        var maxAttempts = policy?.Enabled == true ? policy.MaxRetries : 0;
        var attempt = 0;
        var lastRetryAttempt = 0;
        var lastRetryError = string.Empty;
        var retried = false;

        while (true)
        {
            var response = await produce().ConfigureAwait(false);

            if (response.StopReason == StopReason.Aborted)
            {
                if (retried && callbacks?.OnRetryFinished is not null)
                {
                    await callbacks.OnRetryFinished(false, lastRetryAttempt, null)
                        .ConfigureAwait(false);
                }
                return response;
            }

            if (response.StopReason != StopReason.Error)
            {
                if (retried && callbacks?.OnRetryFinished is not null)
                {
                    await callbacks.OnRetryFinished(true, lastRetryAttempt, null)
                        .ConfigureAwait(false);
                }
                return response;
            }

            if (attempt >= maxAttempts || !IsRetryableAssistantError(response))
            {
                if (retried && callbacks?.OnRetryFinished is not null)
                {
                    await callbacks.OnRetryFinished(
                        false,
                        lastRetryAttempt,
                        response.ErrorMessage).ConfigureAwait(false);
                }
                return response;
            }

            attempt++;
            retried = true;
            lastRetryAttempt = attempt;
            lastRetryError = string.IsNullOrEmpty(response.ErrorMessage)
                ? "Unknown error"
                : response.ErrorMessage;
            var delayMs = RetryDelayMs(policy!, attempt);

            if (callbacks?.OnRetryScheduled is not null)
            {
                await callbacks.OnRetryScheduled(
                    attempt,
                    maxAttempts,
                    delayMs,
                    lastRetryError).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(delayMs, signal).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (callbacks?.OnRetryFinished is not null)
                {
                    await callbacks.OnRetryFinished(false, attempt, lastRetryError)
                        .ConfigureAwait(false);
                }
                return response with
                {
                    StopReason = StopReason.Aborted,
                    ErrorMessage = null,
                };
            }

            if (callbacks?.OnRetryAttemptStart is not null)
            {
                await callbacks.OnRetryAttemptStart().ConfigureAwait(false);
            }
        }
    }

    private static Regex Build(IReadOnlyList<string> patterns)
        => new Regex(
            string.Join("|", patterns),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
