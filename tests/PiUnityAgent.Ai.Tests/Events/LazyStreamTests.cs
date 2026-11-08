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

    [Fact]
    public async Task Observer_Receives_Events_And_Result()
    {
        var model = CreateModel();
        var events = new List<AssistantMessageEvent>();
        var results = new List<AssistantMessage?>();
        var observer = new RecordingObserver(events, results);

        var stream = LazyStream.Run(model, () =>
            Task.FromResult<IAssistantMessageEventStream>(DoneSource(model)), observer);
        await stream.WaitForDoneAsync();

        Assert.Contains(events, e => e is AssistantMessageEvent.Done);
        Assert.Single(results);
        Assert.Equal(StopReason.Stop, results[0]!.StopReason);
    }

    [Fact]
    public async Task Observer_Receives_Error_Message_On_Setup_Failure()
    {
        var model = CreateModel();
        var results = new List<AssistantMessage?>();
        var observer = new RecordingObserver(new List<AssistantMessageEvent>(), results);

        var stream = LazyStream.Run(model,
            () => throw new InvalidOperationException("setup failed"),
            observer);
        var done = await stream.WaitForDoneAsync();

        Assert.Single(results);
        Assert.Same(done, results[0]);
        Assert.Equal(StopReason.Error, done.StopReason);
    }

    [Fact]
    public async Task Observer_Receives_Error_Event_On_Setup_Failure()
    {
        var model = CreateModel();
        var events = new List<AssistantMessageEvent>();
        var results = new List<AssistantMessage?>();
        var observer = new RecordingObserver(events, results);

        var stream = LazyStream.Run(model,
            () => throw new InvalidOperationException("setup failed"),
            observer);
        var done = await stream.WaitForDoneAsync();

        // pi api/lazy.ts catch 顺序：outer.push → observer.onEvent → outer.end → observeResult。
        var errorEvent = Assert.Single(events);
        var error = Assert.IsType<AssistantMessageEvent.Error>(errorEvent);
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Same(done, error.Message);
        Assert.Single(results);
        Assert.Same(done, results[0]);
    }

    [Fact]
    public async Task Observer_OnResult_Is_Called_Exactly_Once_On_Success()
    {
        var model = CreateModel();
        var results = new List<AssistantMessage?>();
        var observer = new RecordingObserver(new List<AssistantMessageEvent>(), results);

        var stream = LazyStream.Run(model, () =>
            Task.FromResult<IAssistantMessageEventStream>(DoneSource(model)), observer);
        var done = await stream.WaitForDoneAsync();

        // 消费完流再等结果，OnResult 仍只发生一次（End 后置，不随消费次数重复）。
        await foreach (var _ in stream) { }
        Assert.Same(done, results.Single());
    }

    private sealed class RecordingObserver : ILazyStreamObserver
    {
        private readonly List<AssistantMessageEvent> _events;
        private readonly List<AssistantMessage?> _results;
        public RecordingObserver(List<AssistantMessageEvent> events, List<AssistantMessage?> results)
        {
            _events = events;
            _results = results;
        }
        public void OnEvent(AssistantMessageEvent @event) => _events.Add(@event);
        public void OnResult(AssistantMessage? result) => _results.Add(result);
    }
}
