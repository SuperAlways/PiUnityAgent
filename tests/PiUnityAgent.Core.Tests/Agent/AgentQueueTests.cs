using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;

namespace PiUnityAgent.Core.Tests.Agent;

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
