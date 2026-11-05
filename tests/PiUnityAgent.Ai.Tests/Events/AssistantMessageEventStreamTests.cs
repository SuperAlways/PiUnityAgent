using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Events;

public class AssistantMessageEventStreamTests
{
    private static AssistantMessage Message(long timestamp = 1, int? durationMs = null)
        => new()
        {
            Content = Array.Empty<ContentBlock>(),
            Api = "test",
            Provider = "test",
            Model = "test",
            Usage = new Usage(),
            StopReason = StopReason.Stop,
            Timestamp = timestamp,
            DurationMs = durationMs,
        };

    [Fact]
    public async Task Push_Updates_Partial_And_Completes_With_Done()
    {
        var stream = new AssistantMessageEventStream();
        var start = Message();
        var final = Message();

        stream.Push(new AssistantMessageEvent.Start(start));
        stream.Push(new AssistantMessageEvent.TextDelta(0, "hi", start));
        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, final));

        Assert.Same(final, stream.Partial);
        var seen = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            seen.Add(@event);
        }

        Assert.Equal(3, seen.Count);
        Assert.IsType<AssistantMessageEvent.Done>(seen[^1]);
        Assert.Same(final, await stream.WaitForDoneAsync());
    }

    [Fact]
    public async Task Push_After_Terminal_Is_Ignored()
    {
        var stream = new AssistantMessageEventStream();
        var final = Message();
        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, final));
        stream.Push(new AssistantMessageEvent.Start(Message()));

        var seen = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            seen.Add(@event);
        }

        Assert.Single(seen);
        Assert.Same(final, stream.Partial);
    }

    [Fact]
    public async Task Error_Event_Is_Terminal()
    {
        var stream = new AssistantMessageEventStream();
        var message = Message();
        stream.Push(new AssistantMessageEvent.Error(StopReason.Aborted, message));

        Assert.Same(message, await stream.WaitForDoneAsync());
    }

    [Fact]
    public void Invalid_Done_Reason_Throws()
    {
        var stream = new AssistantMessageEventStream();
        Assert.Throws<ArgumentException>(() =>
            stream.Push(new AssistantMessageEvent.Done(StopReason.Error, Message())));
    }

    [Fact]
    public async Task Timing_Sets_DurationMs_For_New_Message()
    {
        var stream = new AssistantMessageEventStream();
        var message = Message(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));

        await stream.WaitForDoneAsync();
        Assert.NotNull(message.DurationMs);
        Assert.True(message.DurationMs >= 0);
    }

    [Fact]
    public async Task Timing_Does_Not_Override_Existing_Duration()
    {
        var stream = new AssistantMessageEventStream();
        var message = Message(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), durationMs: 123);
        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));

        await stream.WaitForDoneAsync();
        Assert.Equal(123, message.DurationMs);
    }

    [Fact]
    public async Task Timing_Skips_Old_Timestamp()
    {
        var stream = new AssistantMessageEventStream();
        var message = Message(timestamp: 1);
        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));

        await stream.WaitForDoneAsync();
        Assert.Null(message.DurationMs);
    }

    [Fact]
    public async Task End_Closes_Stream_And_Returns_Final()
    {
        var stream = new AssistantMessageEventStream();
        var final = Message(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.End(final);

        Assert.Same(final, stream.Partial);
        Assert.Same(final, await stream.WaitForDoneAsync());
        Assert.NotNull(final.DurationMs);
    }

    [Fact]
    public async Task WaitForDone_Cancels()
    {
        var stream = new AssistantMessageEventStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.WaitForDoneAsync(cts.Token));
    }
}
