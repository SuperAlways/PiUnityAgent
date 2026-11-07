using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Events;

public class LazyStreamTests
{
    private static Model CreateModel()
        => new()
        {
            Id = "m1",
            Name = "m1",
            Api = "faux",
            Provider = "faux",
            BaseUrl = "http://localhost:0",
        };

    private static AssistantMessageEventStream DoneSource(Model model)
    {
        var stream = new AssistantMessageEventStream();
        var message = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = StopReason.Stop,
            Timestamp = 1,
        };
        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
        stream.End(message);
        return stream;
    }

    [Fact]
    public async Task Factory_Starts_Immediately_When_Run_Returns()
    {
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var model = CreateModel();
        var stream = LazyStream.Run(model, () =>
        {
            started.TrySetResult(true);
            return Task.FromResult<IAssistantMessageEventStream>(DoneSource(model));
        });

        var completed = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(started.Task, completed);
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task Factory_Runs_Once_Across_Wait_And_Iteration()
    {
        var calls = 0;
        var model = CreateModel();
        var stream = LazyStream.Run(model, () =>
        {
            calls++;
            return Task.FromResult<IAssistantMessageEventStream>(DoneSource(model));
        });

        var done = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Stop, done.StopReason);
        Assert.Equal(1, calls);

        await foreach (var _ in stream) { }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WaitForDone_Before_Iteration_Returns_Result()
    {
        var model = CreateModel();
        var stream = LazyStream.Run(model, () =>
            Task.FromResult<IAssistantMessageEventStream>(DoneSource(model)));

        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Stop, done.StopReason);
        Assert.Equal(model.Provider, done.Provider);
        Assert.Equal(model.Id, done.Model);
    }

    [Fact]
    public async Task Factory_Exception_Becomes_Error_With_Message()
    {
        var model = CreateModel();
        var stream = LazyStream.Run(model, () =>
            Task.FromException<IAssistantMessageEventStream>(
                new InvalidOperationException("boom")));

        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Contains("boom", done.ErrorMessage);
    }

    [Fact]
    public async Task Factory_OperationCanceled_Becomes_Error_Like_Pi()
    {
        var model = CreateModel();
        var stream = LazyStream.Run(model, () =>
            Task.FromException<IAssistantMessageEventStream>(
                new OperationCanceledException()));

        var done = await stream.WaitForDoneAsync();

        // pi/pi.net 的 lazyStream 不在 setup 异常里区分 OCE；Aborted 由 provider 内层流负责。
        Assert.Equal(StopReason.Error, done.StopReason);
    }

    [Fact]
    public async Task Inner_Aborted_Event_Is_Forwarded_As_Aborted()
    {
        var model = CreateModel();
        var source = new AssistantMessageEventStream();
        var message = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = StopReason.Aborted,
            ErrorMessage = "Request was aborted",
            Timestamp = 1,
        };
        source.Push(new AssistantMessageEvent.Error(StopReason.Aborted, message));
        source.End(message);

        var stream = LazyStream.Run(model, () =>
            Task.FromResult<IAssistantMessageEventStream>(source));
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Aborted, done.StopReason);
        Assert.Equal("Request was aborted", done.ErrorMessage);
        Assert.Equal(StopReason.Aborted, stream.Partial!.StopReason);
    }

    [Fact]
    public async Task Null_Source_Becomes_Error()
    {
        var model = CreateModel();
        var stream = LazyStream.Run(model, () =>
            Task.FromResult<IAssistantMessageEventStream>(null!));

        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Contains("null", done.ErrorMessage);
    }
}
