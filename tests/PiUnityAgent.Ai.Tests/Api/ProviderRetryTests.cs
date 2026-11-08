using System;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Types;
using Xunit;

namespace PiUnityAgent.Ai.Tests.Api;

public class ProviderRetryTests
{
    private static FetchException Error(
        int? status = null,
        string? xShouldRetry = null,
        bool isRetryable = false,
        string? retryAfter = null,
        string? retryAfterMs = null)
    {
        var headers = new FetchHeaders();
        if (xShouldRetry is not null) headers["x-should-retry"] = xShouldRetry;
        if (retryAfter is not null) headers["retry-after"] = retryAfter;
        if (retryAfterMs is not null) headers["retry-after-ms"] = retryAfterMs;
        return new FetchException(FetchErrorKind.Protocol, "test error")
        {
            StatusCode = status,
            Headers = headers,
            IsRetryable = isRetryable,
        };
    }

    private static Random Exact(double value) => new SeededRandom(value);

    private sealed class SeededRandom : Random
    {
        private readonly double _value;
        public SeededRandom(double value) => _value = value;
        public override double NextDouble() => _value;
    }

    // ---- ShouldRetry ----

    [Theory]
    [InlineData(408, true)]
    [InlineData(409, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(302, false)]
    public void ShouldRetry_Classifies_Http_Status(int status, bool expected)
    {
        Assert.Equal(expected, ProviderRetry.ShouldRetry(Error(status: status)));
    }

    [Fact]
    public void ShouldRetry_Missing_Status_Is_Retryable()
    {
        Assert.True(ProviderRetry.ShouldRetry(Error()));
    }

    [Fact]
    public void ShouldRetry_Header_True_Overrides_Status()
    {
        Assert.True(ProviderRetry.ShouldRetry(Error(status: 400, xShouldRetry: "true")));
    }

    [Fact]
    public void ShouldRetry_Header_False_Disables_Retry()
    {
        Assert.False(ProviderRetry.ShouldRetry(Error(status: 500, xShouldRetry: "false")));
    }

    [Fact]
    public void ShouldRetry_Transport_Flag_Marks_Connection_Errors()
    {
        Assert.True(ProviderRetry.ShouldRetry(Error(isRetryable: true)));
    }

    [Fact]
    public void ShouldRetry_Header_Is_Case_Insensitive_And_Trimmed_Names()
    {
        // FetchHeaders is case-insensitive by construction; assert the header wins over a 400.
        Assert.True(ProviderRetry.ShouldRetry(Error(status: 400, xShouldRetry: "TRUE")));
    }

    // ---- DelayMs ----

    [Fact]
    public void DelayMs_Exponential_Base_Caps_At_Eight_Seconds()
    {
        var noJitter = Exact(0.0);
        Assert.Equal(500, ProviderRetry.DelayMs(Error(), 0, random: noJitter));
        Assert.Equal(1000, ProviderRetry.DelayMs(Error(), 1, random: noJitter));
        Assert.Equal(2000, ProviderRetry.DelayMs(Error(), 2, random: noJitter));
        Assert.Equal(4000, ProviderRetry.DelayMs(Error(), 3, random: noJitter));
        Assert.Equal(8000, ProviderRetry.DelayMs(Error(), 4, random: noJitter));
        Assert.Equal(8000, ProviderRetry.DelayMs(Error(), 10, random: noJitter));
    }

    [Fact]
    public void DelayMs_Jitter_Shorts_Delay_By_Up_To_25_Percent()
    {
        // pi: exponential * (1 - random * 0.25); random=0.5 → 500 * 0.875 = 437.5 → 437
        Assert.Equal(437, ProviderRetry.DelayMs(Error(), 0, random: Exact(0.5)));
        Assert.Equal(8000, ProviderRetry.DelayMs(Error(), 4, random: Exact(0.0)));
        Assert.Equal(6000, ProviderRetry.DelayMs(Error(), 4, random: Exact(0.9999)));
    }

    [Fact]
    public void DelayMs_Prefers_Retry_After_Ms()
    {
        Assert.Equal(250, ProviderRetry.DelayMs(Error(retryAfterMs: "250", retryAfter: "9"), 0));
    }

    [Fact]
    public void DelayMs_Parses_Retry_After_Seconds()
    {
        Assert.Equal(2000, ProviderRetry.DelayMs(Error(retryAfter: "2"), 0));
    }

    [Fact]
    public void DelayMs_Parses_Retry_After_Http_Date()
    {
        var future = DateTimeOffset.UtcNow.AddSeconds(30);
        var delay = ProviderRetry.DelayMs(Error(retryAfter: future.ToString("R")), 0);
        Assert.InRange(delay, 25_000, 30_000);
    }

    [Fact]
    public void DelayMs_Falls_Back_To_Exponential_When_Headers_Unparseable()
    {
        Assert.Equal(500, ProviderRetry.DelayMs(Error(retryAfter: "not-a-date", retryAfterMs: "abc"), 0, random: Exact(0.0)));
    }

    [Fact]
    public void DelayMs_Throws_When_Server_Delay_Exceeds_Max()
    {
        Assert.Throws<FetchException>(() => ProviderRetry.DelayMs(Error(retryAfterMs: "70000"), 0, maxRetryDelayMs: 60_000));
    }

    [Fact]
    public void DelayMs_Zero_Max_Disables_Limit()
    {
        Assert.Equal(70_000, ProviderRetry.DelayMs(Error(retryAfterMs: "70000"), 0, maxRetryDelayMs: 0));
    }

    // ---- RunAsync ----

    [Fact]
    public async Task RunAsync_Returns_First_Success_Without_Retrying()
    {
        var calls = 0;
        var result = await ProviderRetry.RunAsync<int>(
            _ => { calls++; return Task.FromResult(7); },
            new ProviderRetryOptions { MaxRetries = 3, SleepAsync = NoSleep });

        Assert.Equal(7, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_Retries_Retryable_Error_Then_Succeeds()
    {
        var calls = 0;
        var sleeps = new List<long>();
        var result = await ProviderRetry.RunAsync<int>(
            _ =>
            {
                calls++;
                return calls == 1
                    ? throw Error(status: 429)
                    : Task.FromResult(42);
            },
            new ProviderRetryOptions
            {
                MaxRetries = 2,
                SleepAsync = (ms, _) => { sleeps.Add(ms); return Task.CompletedTask; },
            });

        Assert.Equal(42, result);
        Assert.Equal(2, calls);
        var delay = Assert.Single(sleeps);
        Assert.InRange(delay, 375, 500); // first backoff: 500ms jittered by (1 - 0.25*random)
    }

    [Fact]
    public async Task RunAsync_Gives_Up_After_Max_Retries_And_Throws_Last_Error()
    {
        var calls = 0;
        var last = await Assert.ThrowsAsync<FetchException>(() =>
            ProviderRetry.RunAsync<int>(
                _ => { calls++; throw Error(status: 500); },
                new ProviderRetryOptions { MaxRetries = 2, SleepAsync = NoSleep }));

        Assert.Equal(3, calls);
        Assert.Equal(500, last.StatusCode);
    }

    [Fact]
    public async Task RunAsync_Does_Not_Retry_Non_Retryable_Status()
    {
        var calls = 0;
        await Assert.ThrowsAsync<FetchException>(() =>
            ProviderRetry.RunAsync<int>(
                _ => { calls++; throw Error(status: 400); },
                new ProviderRetryOptions { MaxRetries = 5, SleepAsync = NoSleep }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_Respects_NoRetryStatuses()
    {
        var calls = 0;
        await Assert.ThrowsAsync<FetchException>(() =>
            ProviderRetry.RunAsync<int>(
                _ => { calls++; throw Error(status: 429); },
                new ProviderRetryOptions { MaxRetries = 3, NoRetryStatuses = new[] { 429 }, SleepAsync = NoSleep }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_Default_MaxRetries_Zero_Does_Not_Retry()
    {
        var calls = 0;
        await Assert.ThrowsAsync<FetchException>(() =>
            ProviderRetry.RunAsync<int>(
                _ => { calls++; throw Error(status: 500); },
                new ProviderRetryOptions { SleepAsync = NoSleep }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_Throws_OperationCanceled_When_Token_Already_Cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProviderRetry.RunAsync<int>(
                _ => throw Error(status: 429),
                new ProviderRetryOptions { MaxRetries = 3, SleepAsync = NoSleep },
                cts.Token));
    }

    [Fact]
    public async Task RunAsync_Propagates_Cancellation_Thrown_By_Sleep()
    {
        var calls = 0;
        // 请求令牌保持存活（catch 里的取消预检不触发）；sleep 任务本身以取消告终。
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProviderRetry.RunAsync<int>(
                _ => { calls++; throw Error(status: 429); },
                new ProviderRetryOptions
                {
                    MaxRetries = 3,
                    SleepAsync = (_, _) => Task.FromCanceled(cancelled.Token),
                }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_Uses_Real_Delay_With_Server_Provided_Retry_After_Ms()
    {
        // 走真实 Task.Delay 分支：retry-after-ms: 1 → 1ms sleep 后第二次成功。
        var calls = 0;
        var result = await ProviderRetry.RunAsync<int>(
            _ =>
            {
                calls++;
                return calls == 1 ? throw Error(status: 429, retryAfterMs: "1") : Task.FromResult(5);
            },
            new ProviderRetryOptions { MaxRetries = 1 });

        Assert.Equal(5, result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RunAsync_Propagates_Non_Fetch_Exceptions_Without_Retry()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProviderRetry.RunAsync<int>(
                _ => { calls++; throw new InvalidOperationException("bug"); },
                new ProviderRetryOptions { MaxRetries = 5, SleepAsync = NoSleep }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_Passes_Token_To_Request()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;
        await ProviderRetry.RunAsync<int>(
            token => { seen = token; return Task.FromResult(1); },
            new ProviderRetryOptions { SleepAsync = NoSleep },
            cts.Token);
        Assert.Equal(cts.Token, seen);
    }

    private static Task NoSleep(long ms, CancellationToken token) => Task.CompletedTask;
}
