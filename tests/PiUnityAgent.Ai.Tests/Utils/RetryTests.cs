using PiUnityAgent.Ai.Types;
using PiUnityAgent.Ai.Utils;

namespace PiUnityAgent.Ai.Tests.Utils;

public class RetryTests
{
    private static AssistantMessage Error(string message) => new()
    {
        Content = Array.Empty<ContentBlock>(),
        Api = "faux",
        Provider = "faux",
        Model = "faux-1",
        Usage = new Usage(),
        StopReason = StopReason.Error,
        ErrorMessage = message,
        Timestamp = 1,
    };

    private static AssistantMessage Stop(string text = "ok") => new()
    {
        Content = new ContentBlock[] { new TextContent { Text = text } },
        Api = "faux",
        Provider = "faux",
        Model = "faux-1",
        Usage = new Usage(),
        StopReason = StopReason.Stop,
        Timestamp = 1,
    };

    [Theory]
    [InlineData("GoUsageLimitError", false)]
    [InlineData("insufficient_quota", false)]
    [InlineData("quota exceeded", false)]
    [InlineData("subscription_sharing_usage_limit_exceeded", false)]
    [InlineData("overloaded", true)]
    [InlineData("HTTP 429 Too Many Requests", true)]
    [InlineData("server error", true)]
    [InlineData("connection refused", true)]
    [InlineData("stream ended before message_stop", true)]
    [InlineData("something else", false)]
    public void Classification_Match_Pi_Patterns(string message, bool expected)
        => Assert.Equal(expected, Retry.IsRetryableAssistantError(Error(message)));

    [Fact]
    public void Non_Error_And_Empty_Message_Are_Not_Retryable()
    {
        Assert.False(Retry.IsRetryableAssistantError(Stop()));
        Assert.False(Retry.IsRetryableAssistantError(Error(string.Empty)));
    }

    [Fact]
    public void Delay_Is_Exponential_And_Capped_At_Sixty_Seconds()
    {
        Assert.Equal(1_000, Retry.RetryDelayMs(1_000, null, 0));
        Assert.Equal(1_000, Retry.RetryDelayMs(1_000, null, 1));
        Assert.Equal(2_000, Retry.RetryDelayMs(1_000, null, 2));
        Assert.Equal(4_000, Retry.RetryDelayMs(1_000, null, 3));
        Assert.Equal(60_000, Retry.RetryDelayMs(1_000, null, 10));
        Assert.Equal(5_000, Retry.RetryDelayMs(1_000, 5_000, 10));
    }

    [Fact]
    public void Negative_Cap_Clamps_To_Zero()
    {
        // JS setTimeout 对负延迟自动钳 0；.NET Task.Delay(< -1) 抛 ArgumentOutOfRange，
        // 恰好 -1 表示无限等待。负 cap 必须钳成 0，绝不能透传给 Task.Delay。
        Assert.Equal(0, Retry.RetryDelayMs(1_000, -5_000, 1));
        Assert.Equal(0, Retry.RetryDelayMs(1_000, -1, 1));
    }

    [Fact]
    public async Task RetryAssistantCall_Returns_Success_And_Emits_Callbacks_In_Order()
    {
        var calls = new List<string>();
        var attempts = 0;
        var callbacks = new RetryCallbacks
        {
            OnRetryScheduled = (attempt, max, delay, error) =>
            {
                calls.Add($"scheduled:{attempt}:{max}:{delay}:{error}");
                return Task.CompletedTask;
            },
            OnRetryAttemptStart = () =>
            {
                calls.Add("start");
                return Task.CompletedTask;
            },
            OnRetryFinished = (success, attempt, error) =>
            {
                calls.Add($"finished:{success}:{attempt}:{error}");
                return Task.CompletedTask;
            },
        };

        var result = await Retry.RetryAssistantCall(
            () => Task.FromResult(++attempts <= 2 ? Error("overloaded") : Stop()),
            new RetryPolicy
            {
                Enabled = true,
                MaxRetries = 2,
                BaseDelayMs = 0,
            },
            CancellationToken.None,
            callbacks);

        Assert.Equal(StopReason.Stop, result.StopReason);
        Assert.Equal(3, attempts);
        Assert.Equal(
            new[]
            {
                "scheduled:1:2:0:overloaded",
                "start",
                "scheduled:2:2:0:overloaded",
                "start",
                "finished:True:2:",
            },
            calls);
    }

    [Fact]
    public async Task RetryAssistantCall_Exhausted_Returns_Final_Error()
    {
        var result = await Retry.RetryAssistantCall(
            () => Task.FromResult(Error("overloaded")),
            new RetryPolicy { Enabled = true, MaxRetries = 1, BaseDelayMs = 0 },
            CancellationToken.None);

        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Equal("overloaded", result.ErrorMessage);
    }

    [Fact]
    public async Task RetryAssistantCall_Non_Retryable_Fails_Fast()
    {
        var attempts = 0;
        var result = await Retry.RetryAssistantCall(
            () =>
            {
                attempts++;
                return Task.FromResult(Error("insufficient_quota"));
            },
            new RetryPolicy { Enabled = true, MaxRetries = 5, BaseDelayMs = 0 },
            CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(StopReason.Error, result.StopReason);
    }

    [Fact]
    public async Task RetryAssistantCall_Cancelled_Backoff_Returns_Aborted_Without_Error()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await Retry.RetryAssistantCall(
            () => Task.FromResult(Error("overloaded")),
            new RetryPolicy { Enabled = true, MaxRetries = 3, BaseDelayMs = 10 },
            cts.Token);

        Assert.Equal(StopReason.Aborted, result.StopReason);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Disabled_Policy_Returns_First_Response_Unchanged()
    {
        var attempts = 0;
        var result = await Retry.RetryAssistantCall(
            () =>
            {
                attempts++;
                return Task.FromResult(Error("overloaded"));
            },
            new RetryPolicy { Enabled = false, MaxRetries = 5, BaseDelayMs = 0 },
            CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal("overloaded", result.ErrorMessage);
    }
}
