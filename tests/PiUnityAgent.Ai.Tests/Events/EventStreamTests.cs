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
    public async Task WaitForResult_Cancels_While_Waiting()
    {
        var stream = Create();
        using var cts = new CancellationTokenSource();

        var wait = stream.WaitForResultAsync(cts.Token);
        Assert.False(wait.IsCompleted);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task Fail_Surfaces_On_Result()
    {
        var stream = Create();
        stream.Fail(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => stream.WaitForResultAsync());
    }

    [Fact]
    public async Task Fail_During_Iteration_Surfaces_Original_Exception()
    {
        var stream = Create();
        stream.Push(1);

        await using var enumerator = stream.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, enumerator.Current);

        stream.Fail(new InvalidOperationException("boom"));
        Assert.True(stream.IsEnded);

        // Channels 把通道完成时的异常原样抛给读取者（框架实现 AsyncOperation.GetResult
        // 走 ExceptionDispatchInfo.Throw()，不包 ChannelClosedException），
        // 因此迭代侧与 WaitForResultAsync 一样拿到原始异常。
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await enumerator.MoveNextAsync());
        Assert.Equal("boom", thrown.Message);
    }

    [Fact]
    public async Task Fail_Null_Throws_And_Leaves_Stream_Open()
    {
        var stream = Create();
        Assert.Throws<ArgumentNullException>(() => stream.Fail(null!));
        Assert.False(stream.IsEnded);

        stream.Push(3);
        Assert.Equal("done", await stream.WaitForResultAsync());
    }

    [Fact]
    public async Task Push_Completion_Predicate_Throw_Fails_Stream()
    {
        var stream = new EventStream<int, string>(
            isComplete: _ => throw new InvalidOperationException("pred"),
            extractResult: _ => "done");

        stream.Push(1);

        Assert.True(stream.IsEnded);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => stream.WaitForResultAsync());

        stream.Push(2);
        Assert.True(stream.IsEnded);
    }

    [Fact]
    public async Task Push_Result_Extractor_Throw_Fails_Stream_Consistently()
    {
        var stream = new EventStream<int, string>(
            isComplete: value => value == 3,
            extractResult: _ => throw new InvalidOperationException("extract"));

        stream.Push(1);
        stream.Push(3);

        var seen = new List<int>();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var value in stream)
            {
                seen.Add(value);
            }
        });

        // 终态事件在结果提取前已入队，消费者先看到它，随后通道以原异常收场
        // （Channels 不包 ChannelClosedException，读取者直接拿到原始异常）。
        Assert.Equal(new[] { 1, 3 }, seen);
        Assert.Equal("extract", thrown.Message);
        Assert.True(stream.IsEnded);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => stream.WaitForResultAsync());
    }
}
