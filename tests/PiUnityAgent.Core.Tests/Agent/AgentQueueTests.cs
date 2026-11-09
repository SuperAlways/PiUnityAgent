using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;

namespace PiUnityAgent.Core.Tests.Agent;

// 命名空间段 Agent 与门面类型同名：本命名空间内裸名 Agent 会先命中子命名空间，
// 故在声明内用别名把 Agent 钉到类型（编译单元级 using 命中不了外层命名空间成员）。
using Agent = PiUnityAgent.Core.Agent.Agent;

public class AgentQueueTests
{
    private static Message M(string text)
        => new UserMessage { Content = MessageContent.From(text), Timestamp = 1 };

    [Fact]
    public void OneAtATime_Drains_One_Per_Call()
    {
        var queue = new PendingMessageQueue(QueueMode.OneAtATime);
        queue.Enqueue(M("a"));
        queue.Enqueue(M("b"));

        var first = queue.Drain();
        Assert.Single(first);
        Assert.Equal("a", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(first[0])
            .Content.ToBlocks()[0]).Text);

        var second = queue.Drain();
        Assert.Single(second);
        Assert.Equal("b", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(second[0])
            .Content.ToBlocks()[0]).Text);
    }

    [Fact]
    public void All_Drains_Everything_In_Insertion_Order()
    {
        var queue = new PendingMessageQueue(QueueMode.All);
        queue.Enqueue(M("a"));
        queue.Enqueue(M("b"));

        var drained = queue.Drain();
        Assert.Equal(2, drained.Count);
        Assert.Equal("a", Assert.IsType<TextContent>(
            Assert.IsType<UserMessage>(drained[0]).Content.ToBlocks()[0]).Text);
        Assert.Equal("b", Assert.IsType<TextContent>(
            Assert.IsType<UserMessage>(drained[1]).Content.ToBlocks()[0]).Text);
        Assert.False(queue.HasItems());
    }

    [Fact]
    public void Prepend_Restores_Drained_Messages_In_Order()
    {
        var queue = new PendingMessageQueue(QueueMode.All);
        queue.Enqueue(M("later"));
        queue.Prepend(new[] { M("a"), M("b") });

        var drained = queue.Drain();
        Assert.Equal(3, drained.Count);
        Assert.Equal("a", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(drained[0]).Content.ToBlocks()[0]).Text);
        Assert.Equal("b", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(drained[1]).Content.ToBlocks()[0]).Text);
        Assert.Equal("later", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(drained[2]).Content.ToBlocks()[0]).Text);
    }

    [Fact]
    public void Peek_Does_Not_Consume_And_Steering_Priority_Is_Caller_Side()
    {
        var queue = new PendingMessageQueue(QueueMode.OneAtATime);
        queue.Enqueue(M("a"));
        queue.Enqueue(M("b"));

        var peeked = queue.Peek();
        Assert.Single(peeked);
        Assert.True(queue.HasItems());
        Assert.Single(queue.Drain()); // 预览未消费；Drain 只取首条
        Assert.True(queue.HasItems());
    }

    [Fact]
    public void Clear_Empties_And_Mode_Can_Change()
    {
        var queue = new PendingMessageQueue(QueueMode.OneAtATime);
        queue.Enqueue(M("a"));
        queue.Enqueue(M("b"));
        queue.Clear();
        Assert.False(queue.HasItems());

        queue.Enqueue(M("a"));
        queue.Enqueue(M("b"));
        queue.Mode = QueueMode.All;
        Assert.Equal(2, queue.Drain().Count);
    }
}

public class AgentQueueIntegrationTests
{
    private static Agent NewAgent(FauxProvider faux, QueueMode mode)
        => new(new AgentOptions
        {
            InitialState = new AgentInitialState { Model = faux.GetModel() },
            StreamFn = faux.Provider.Stream,
            SteeringMode = mode,
            FollowUpMode = mode,
        });

    private static Message M(string text)
        => new UserMessage { Content = MessageContent.From(text), Timestamp = 1 };

    [Fact]
    public async Task OneAtATime_Injects_One_Steering_Message_Per_Drain()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))),
        });
        var agent = NewAgent(faux, QueueMode.OneAtATime);
        agent.Steer(M("s1"));
        agent.Steer(M("s2"));

        await agent.Prompt(M("p"));

        var userTexts = agent.State.Messages.OfType<UserMessage>()
            .Select(message => Assert.IsType<TextContent>(message.Content.ToBlocks()[0]).Text)
            .ToList();
        Assert.Equal(new[] { "p", "s1", "s2" }, userTexts);
        Assert.Equal(2, agent.State.Messages.Count(message => message is AssistantMessage));
        Assert.False(agent.HasQueuedMessages());
    }

    [Fact]
    public async Task All_Injects_Every_Steering_Message_In_One_Drain()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
        });
        var agent = NewAgent(faux, QueueMode.All);
        agent.Steer(M("s1"));
        agent.Steer(M("s2"));

        await agent.Prompt(M("p"));

        var userTexts = agent.State.Messages.OfType<UserMessage>()
            .Select(message => Assert.IsType<TextContent>(message.Content.ToBlocks()[0]).Text)
            .ToList();
        Assert.Equal(new[] { "p", "s1", "s2" }, userTexts);
        Assert.Single(agent.State.Messages.OfType<AssistantMessage>());
    }

    [Fact]
    public async Task FollowUp_Runs_Only_After_Agent_Would_Stop()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))),
        });
        var agent = NewAgent(faux, QueueMode.OneAtATime);
        agent.FollowUp(M("f1"));

        await agent.Prompt(M("p"));

        var assistantCount = agent.State.Messages.Count(message => message is AssistantMessage);
        Assert.Equal(2, assistantCount);
        Assert.Contains(agent.State.Messages.OfType<UserMessage>(),
            message => Assert.IsType<TextContent>(message.Content.ToBlocks()[0]).Text == "f1");
    }

    [Fact]
    public async Task Continue_From_Assistant_Drains_Steering_First()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
        });
        var agent = NewAgent(faux, QueueMode.OneAtATime);
        await agent.Prompt(M("p"));
        agent.Steer(M("s"));

        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))),
        });
        await agent.Continue();

        var userTexts = agent.State.Messages.OfType<UserMessage>()
            .Select(message => Assert.IsType<TextContent>(message.Content.ToBlocks()[0]).Text)
            .ToList();
        Assert.Equal(new[] { "p", "s" }, userTexts);
    }

    [Fact]
    public async Task Continue_From_Assistant_Without_Queues_Throws_Precise_Message()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
        });
        var agent = NewAgent(faux, QueueMode.OneAtATime);
        await agent.Prompt(M("p"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Continue());
        Assert.Equal("Cannot continue from message role: assistant", error.Message);
    }

    [Fact]
    public void PeekQueuedMessages_Prefers_Steering()
    {
        var agent = NewAgent(new FauxProvider(), QueueMode.OneAtATime);
        agent.FollowUp(M("f"));
        agent.Steer(M("s"));
        var peeked = Assert.Single(agent.PeekQueuedMessages());
        Assert.Equal("s", Assert.IsType<TextContent>(
            Assert.IsType<UserMessage>(peeked).Content.ToBlocks()[0]).Text);

        agent.ClearAllQueues();
        Assert.False(agent.HasQueuedMessages());
    }
}
