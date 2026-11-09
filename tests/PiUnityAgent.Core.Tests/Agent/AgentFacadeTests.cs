using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;

namespace PiUnityAgent.Core.Tests.Agent;

// 命名空间段 Agent 与门面类型同名：本命名空间内裸名 Agent 会先命中子命名空间，
// 故在声明内用别名把 Agent 钉到类型（编译单元级 using 命中不了外层命名空间成员）。
using Agent = PiUnityAgent.Core.Agent.Agent;

public class AgentFacadeTests
{
    private static Agent NewAgent(FauxProvider faux, AgentOptions? options = null)
        => new(options is null
            ? new AgentOptions
            {
                InitialState = new AgentInitialState { Model = faux.GetModel() },
                StreamFn = faux.Provider.Stream,
            }
            : options with
            {
                InitialState = options.InitialState ?? new AgentInitialState { Model = faux.GetModel() },
                StreamFn = options.StreamFn ?? faux.Provider.Stream,
            });

    [Fact]
    public async Task PrepareNextTurnWithContext_Receives_Context_And_Can_Update_Thinking()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))),
        });
        PrepareNextTurnContext? seen = null;
        CancellationToken seenSignal = default;
        int seenNewMessageCount = 0;

        var agent = NewAgent(faux, new AgentOptions
        {
            InitialState = new AgentInitialState { Model = faux.GetModel() },
            StreamFn = faux.Provider.Stream,
            PrepareNextTurnWithContext = (context, signal) =>
            {
                seen = context;
                seenSignal = signal;
                // NewMessages 是 loop 层 live 累积列表（pi 同）：回调过后还会追加
                // follow-up 与第二次响应，故回调当拍快照数量，不能事后断言。
                seenNewMessageCount = context.NewMessages.Count;
                return Task.FromResult<AgentLoopTurnUpdate?>(new AgentLoopTurnUpdate
                {
                    ThinkingLevel = ModelThinkingLevel.Low,
                });
            },
        });
        agent.FollowUp(new UserMessage { Content = MessageContent.From("f"), Timestamp = 1 });

        await agent.Prompt("p");

        Assert.NotNull(seen);
        Assert.Equal(2, seenNewMessageCount);
        Assert.NotEqual(default, seenSignal);
    }

    [Fact]
    public async Task PrepareNextTurn_Signal_Overload_Is_Called_With_Run_Token()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))),
        });
        CancellationToken seen = default;
        var agent = NewAgent(faux, new AgentOptions
        {
            InitialState = new AgentInitialState { Model = faux.GetModel() },
            StreamFn = faux.Provider.Stream,
            PrepareNextTurn = signal =>
            {
                seen = signal;
                return Task.FromResult<AgentLoopTurnUpdate?>(null);
            },
        });
        agent.FollowUp(new UserMessage { Content = MessageContent.From("f"), Timestamp = 1 });

        await agent.Prompt("p");

        Assert.NotEqual(default, seen);
    }

    [Fact]
    public async Task Omitted_StreamFn_Falls_Back_To_DefaultStreamFn()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("ok"))),
        });
        DefaultStreamFn.Set(faux.Provider.Stream);
        try
        {
            var agent = new Agent(new AgentOptions
            {
                InitialState = new AgentInitialState { Model = faux.GetModel() },
            });
            await agent.Prompt("p");
            Assert.Equal(StopReason.Stop,
                Assert.IsType<AssistantMessage>(agent.State.Messages[^1]).StopReason);
        }
        finally
        {
            DefaultStreamFn.Set(null);
        }
    }

    [Fact]
    public void Missing_Default_StreamFn_Throws_Pi_Text()
    {
        DefaultStreamFn.Set(null);
        var error = Assert.Throws<InvalidOperationException>(
            () => new Agent(new AgentOptions()));
        Assert.Equal(
            "No default stream function configured. Pass streamFn explicitly or call setDefaultStreamFn().",
            error.Message);
    }
}
