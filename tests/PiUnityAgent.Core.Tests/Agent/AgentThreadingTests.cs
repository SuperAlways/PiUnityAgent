using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;

namespace PiUnityAgent.Core.Tests.Agent;

// 命名空间段 Agent 与门面类型同名：本命名空间内裸名 Agent 会先命中子命名空间，
// 故在声明内用别名把 Agent 钉到类型（编译单元级 using 命中不了外层命名空间成员）。
using Agent = PiUnityAgent.Core.Agent.Agent;

public class AgentThreadingTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Steer_Abort_From_Other_Threads_Are_Safe_And_Effective()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Factory(async (_, options, _, _) =>
            {
                await Task.Delay(Timeout.Infinite, options?.CancellationToken ?? CancellationToken.None);
                throw new InvalidOperationException("unreachable");
            }),
        });
        var agent = new Agent(new AgentOptions
        {
            InitialState = new AgentInitialState { Model = faux.GetModel() },
            StreamFn = faux.Provider.Stream,
        });

        var running = agent.Prompt(new UserMessage
        {
            Content = MessageContent.From("p"),
            Timestamp = 1,
        });
        await WaitUntilAsync(() => agent.State.IsStreaming);

        await Task.Run(() => agent.Steer(new UserMessage
        {
            Content = MessageContent.From("s"),
            Timestamp = 2,
        }));
        await Task.Run(agent.Abort);

        await running;
        await agent.WaitForIdle();
        Assert.False(agent.State.IsStreaming);
        // 队列内容是否被排空取决于与初始 steering 轮询的竞争；本测试只冒烟锁/取消安全。
    }
}
