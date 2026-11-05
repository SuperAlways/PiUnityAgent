using PiUnityAgent.Ai.Events;

namespace PiUnityAgent.Ai.Tests.Events;

public class EventStreamTests
{
    private static EventStream<int, string> Create()
        => new(isComplete: value => value == 3, extractResult: _ => "done");

    [Fact]
    public async Task Push_Before_Consumer_Iterates_In_Order_And_Completes()
    {
        var stream = Create();
        stream.Push(1);
        stream.Push(2);
        stream.Push(3);

        var seen = new List<int>();
        await foreach (var value in stream)
        {
            seen.Add(value);
        }

        Assert.Equal(new[] { 1, 2, 3 }, seen);
        Assert.Equal("done", await stream.WaitForResultAsync());
    }

    [Fact]
    public async Task Push_After_Terminal_Is_Ignored()
    {
        var stream = Create();
        stream.Push(1);
        stream.Push(3);
        stream.Push(4);

        var seen = new List<int>();
        await foreach (var value in stream)
        {
            seen.Add(value);
        }

        Assert.Equal(new[] { 1, 3 }, seen);
    }

    [Fact]
    public async Task End_Closes_Without_Terminal_Event()
    {
        var stream = Create();
        stream.Push(1);
        stream.End("ended");

        var seen = new List<int>();
        await foreach (var value in stream)
        {
            seen.Add(value);
        }

        Assert.Equal(new[] { 1 }, seen);
        Assert.Equal("ended", await stream.WaitForResultAsync());
    }

    [Fact]
    public async Task WaitForResult_Cancels()
    {
        var stream = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.WaitForResultAsync(cts.Token));
    }

    [Fact]
    public async Task Fail_Surfaces_On_Result()
    {
        var stream = Create();
        stream.Fail(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => stream.WaitForResultAsync());
    }
}
