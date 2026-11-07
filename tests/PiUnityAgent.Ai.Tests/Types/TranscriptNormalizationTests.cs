using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class TranscriptNormalizationTests
{
    private static UserMessage User(string text, long timestamp = 7)
        => new() { Timestamp = timestamp, Content = MessageContent.From(text) };

    private static Tool Tool(string name)
        => new()
        {
            Name = name,
            Description = name,
            Parameters = new JsonObject { ["type"] = "object" },
        };

    [Fact]
    public void Empty_Prompt_And_Tools_Preserve_Messages()
    {
        var message = User("hello");
        var context = new Context
        {
            SystemPrompt = null,
            Messages = new Message[] { message },
            Tools = null,
        };

        var transcript = TranscriptNormalization.NormalizeContext(context);

        Assert.Single(transcript.Messages);
        Assert.Same(message, transcript.Messages[0]);
    }

    [Fact]
    public void Prompt_Becomes_Leading_System_Message_With_Zero_Timestamp()
    {
        var context = new Context
        {
            SystemPrompt = "You are a helpful assistant.",
            Messages = new Message[] { User("hello") },
        };

        var transcript = TranscriptNormalization.NormalizeContext(context);

        Assert.Equal(2, transcript.Messages.Count);
        var system = Assert.IsType<SystemMessage>(transcript.Messages[0]);
        Assert.Equal(0, system.Timestamp);
        Assert.Equal("You are a helpful assistant.", Assert.IsType<MessageContent.Text>(system.Content).Value);
        Assert.Null(system.ToolsAdded);
        Assert.IsType<UserMessage>(transcript.Messages[1]);
    }

    [Fact]
    public void Tools_Only_Produce_System_Message_With_Empty_Content_And_ToolsAdded()
    {
        var context = new Context
        {
            Messages = new Message[] { User("hello") },
            Tools = new[] { Tool("query_hp") },
        };

        var transcript = TranscriptNormalization.NormalizeContext(context);

        var system = Assert.IsType<SystemMessage>(transcript.Messages[0]);
        Assert.Equal(string.Empty, Assert.IsType<MessageContent.Text>(system.Content).Value);
        Assert.Single(system.ToolsAdded!);
        Assert.Equal("query_hp", system.ToolsAdded![0].Name);
    }

    [Fact]
    public void Prompt_And_Tools_Are_Combined_In_One_Leading_System_Message()
    {
        var context = new Context
        {
            SystemPrompt = "sys",
            Messages = new Message[] { User("hello") },
            Tools = new[] { Tool("move"), Tool("pickup") },
        };

        var transcript = TranscriptNormalization.NormalizeContext(context);

        var system = Assert.IsType<SystemMessage>(transcript.Messages[0]);
        Assert.Equal("sys", Assert.IsType<MessageContent.Text>(system.Content).Value);
        Assert.Equal(2, system.ToolsAdded!.Count);
        Assert.Equal(0, system.Timestamp);
    }

    [Fact]
    public void Empty_String_Prompt_Does_Not_Create_System_Message()
    {
        var context = new Context
        {
            SystemPrompt = string.Empty,
            Messages = new Message[] { User("hello") },
            Tools = null,
        };

        var transcript = TranscriptNormalization.NormalizeContext(context);

        Assert.Single(transcript.Messages);
        Assert.IsType<UserMessage>(transcript.Messages[0]);
    }

    [Fact]
    public void Original_Message_Order_Is_Preserved()
    {
        var first = User("one", 1);
        var second = User("two", 2);
        var context = new Context
        {
            SystemPrompt = "sys",
            Messages = new Message[] { first, second },
        };

        var transcript = TranscriptNormalization.NormalizeContext(context);

        Assert.Equal(3, transcript.Messages.Count);
        Assert.Same(first, transcript.Messages[1]);
        Assert.Same(second, transcript.Messages[2]);
    }
}
