using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;

namespace PiUnityAgent.Core.Tests.Agent;

// 命名空间段 Agent 与门面类型同名：本命名空间内裸名 Agent 会先命中子命名空间，
// 故在声明内用别名把 Agent 钉到类型（编译单元级 using 命中不了外层命名空间成员）。
using Agent = PiUnityAgent.Core.Agent.Agent;

public class AgentLifecycleTests
{
    private static Agent NewAgent(FauxProvider faux, AgentOptions? options = null)
    {
        var initial = new AgentInitialState
        {
            Model = faux.GetModel(),
            Messages = new Message[]
            {
                new UserMessage { Content = MessageContent.From("hi"), Timestamp = 1 },
            },
        };
        return new Agent(new AgentOptions
        {
            InitialState = initial,
            StreamFn = faux.Provider.Stream,
            ConvertToLlm = options?.ConvertToLlm,
            TransformContext = options?.TransformContext,
            GetApiKey = options?.GetApiKey,
            SessionId = options?.SessionId,
            ThinkingBudgets = options?.ThinkingBudgets,
            Transport = options?.Transport,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            ToolExecution = options?.ToolExecution,
            Identity = options?.Identity,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
            SteeringMode = options?.SteeringMode ?? QueueMode.OneAtATime,
            FollowUpMode = options?.FollowUpMode ?? QueueMode.OneAtATime,
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Prompt_Emits_Full_Event_Sequence_And_Updates_State()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[] { new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("hello"))) });
        var agent = NewAgent(faux);

        var events = new List<AgentEvent>();
        agent.Subscribe((@event, _) =>
        {
            lock (events) events.Add(@event);
            return Task.CompletedTask;
        });

        await agent.Prompt("go");

        lock (events)
        {
            Assert.IsType<AgentEvent.AgentStart>(events[0]);
            Assert.IsType<AgentEvent.TurnStart>(events[1]);
            Assert.IsType<AgentEvent.AgentEnd>(events[^1]);
        }

        Assert.False(agent.State.IsStreaming);
        var assistant = Assert.IsType<AssistantMessage>(agent.State.Messages[^1]);
        Assert.Equal(StopReason.Stop, assistant.StopReason);
        Assert.Equal("hello", Assert.IsType<TextContent>(assistant.Content[0]).Text);
    }

    [Fact]
    public async Task Prompt_While_Running_Throws_Precise_Message()
    {
        var gate = new TaskCompletionSource<AssistantMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faux = new FauxProvider();
        faux.SetResponses(new[] { new FauxResponseStep.Factory((_, _, _, _) => gate.Task) });
        var agent = NewAgent(faux);

        var running = agent.Prompt("one");
        await WaitUntilAsync(() => agent.State.IsStreaming);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.Prompt("two"));
        Assert.Equal(
            "Agent is already processing a prompt. Use steer() or followUp() to queue messages, or wait for completion.",
            error.Message);

        gate.SetResult(Faux.AssistantMessage(Faux.Text("done")));
        await running;
        await agent.WaitForIdle();
        Assert.False(agent.State.IsStreaming);
    }

    [Fact]
    public async Task Abort_Cancels_Run_And_Leaves_Idle()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[] { new FauxResponseStep.Factory(async (_, options, _, _) =>
        {
            await Task.Delay(Timeout.Infinite, options?.CancellationToken ?? CancellationToken.None);
            throw new InvalidOperationException("unreachable");
        }) });
        var agent = NewAgent(faux);

        var running = agent.Prompt("one");
        await WaitUntilAsync(() => agent.State.IsStreaming);
        agent.Abort();

        await running;
        await agent.WaitForIdle();
        Assert.False(agent.State.IsStreaming);

        var assistant = Assert.IsType<AssistantMessage>(agent.State.Messages[^1]);
        Assert.Equal(StopReason.Aborted, assistant.StopReason);
    }

    [Fact]
    public async Task Executor_Failure_Emits_Failure_Message_And_Four_Events()
    {
        var faux = new FauxProvider();
        var agent = new Agent(new AgentOptions
        {
            InitialState = new AgentInitialState { Model = faux.GetModel() },
            StreamFn = (_, _, _) => throw new InvalidOperationException("boom"),
        });

        var events = new List<AgentEvent>();
        agent.Subscribe((@event, _) =>
        {
            lock (events) events.Add(@event);
            return Task.CompletedTask;
        });

        await agent.Prompt("go");

        lock (events)
        {
            Assert.IsType<AgentEvent.MessageStart>(events[^4]);
            Assert.IsType<AgentEvent.MessageEnd>(events[^3]);
            Assert.IsType<AgentEvent.TurnEnd>(events[^2]);
            Assert.IsType<AgentEvent.AgentEnd>(events[^1]);
        }

        var failure = Assert.IsType<AssistantMessage>(agent.State.Messages[^1]);
        Assert.Equal(StopReason.Error, failure.StopReason);
        Assert.Equal("boom", failure.ErrorMessage);
        Assert.Equal("boom", agent.State.ErrorMessage);
    }

    [Fact]
    public async Task Reset_Keeps_Single_System_Baseline_And_Rejects_While_Running()
    {
        var gate = new TaskCompletionSource<AssistantMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Factory((_, _, _, _) => gate.Task),
        });
        var agent = new Agent(new AgentOptions
        {
            InitialState = new AgentInitialState
            {
                SystemPrompt = "base",
                Model = faux.GetModel(),
            },
            StreamFn = faux.Provider.Stream,
        });

        await agent.Prompt("first");
        Assert.True(agent.State.Messages.Count > 1);

        agent.Reset();
        var baseline = Assert.IsType<SystemMessage>(Assert.Single(agent.State.Messages));
        Assert.Equal(0, baseline.Timestamp);
        Assert.False(agent.State.IsStreaming);

        var running = agent.Prompt("second");
        await WaitUntilAsync(() => agent.State.IsStreaming);
        var error = Assert.Throws<InvalidOperationException>(() => agent.Reset());
        Assert.Equal(
            "Agent is already processing. Wait for completion before resetting.",
            error.Message);
        gate.SetResult(Faux.AssistantMessage(Faux.Text("two")));
        await running;
    }

    [Fact]
    public async Task Subscribe_Awaits_In_Order_And_Unsubscribe_Stops_Delivery()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[] { new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("ok"))) });
        var agent = NewAgent(faux);
        var calls = new List<string>();

        var first = agent.Subscribe((_, _) =>
        {
            lock (calls) calls.Add("first");
            return Task.CompletedTask;
        });
        agent.Subscribe((_, _) =>
        {
            lock (calls) calls.Add("second");
            return Task.CompletedTask;
        });
        await agent.Prompt("go");
        Assert.Equal(new[] { "first", "second" }, calls.Take(2));

        first.Dispose();
        calls.Clear();
        faux.SetResponses(new[] { new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("again"))) });
        await agent.Prompt("again");
        Assert.DoesNotContain("first", calls);
        Assert.Contains("second", calls);
    }

    [Fact]
    public async Task WaitForIdle_Waits_Until_AgentEnd_Listener_Settles()
    {
        var gate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faux = new FauxProvider();
        faux.SetResponses(new[] { new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("ok"))) });
        var agent = NewAgent(faux);
        agent.Subscribe(async (@event, _) =>
        {
            if (@event is AgentEvent.AgentEnd)
            {
                await gate.Task;
            }
        });

        var running = agent.Prompt("go");
        await WaitUntilAsync(() => agent.State.IsStreaming);
        var idle = agent.WaitForIdle();
        var completed = await Task.WhenAny(idle, Task.Delay(200));
        Assert.NotSame(idle, completed);

        gate.SetResult(true);
        await running;
        await idle;
    }
}
