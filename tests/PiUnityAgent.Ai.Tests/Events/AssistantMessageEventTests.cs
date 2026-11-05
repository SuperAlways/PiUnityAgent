using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Events;

public class AssistantMessageEventTests
{
    private static AssistantMessage Message(long timestamp = 1, int? durationMs = null)
        => new()
        {
            Content = Array.Empty<ContentBlock>(),
            Api = "test",
            Provider = "test",
            Model = "test",
            Usage = new Usage(),
            StopReason = StopReason.Stop,
            Timestamp = timestamp,
            DurationMs = durationMs,
        };

    [Fact]
    public void Event_Types_Use_Wire_Names()
    {
        var partial = Message();
        Assert.Equal("start", new AssistantMessageEvent.Start(partial).Type);
        Assert.Equal("text_start", new AssistantMessageEvent.TextStart(0, partial).Type);
        Assert.Equal("text_delta", new AssistantMessageEvent.TextDelta(0, "x", partial).Type);
        Assert.Equal("text_end", new AssistantMessageEvent.TextEnd(0, "x", partial).Type);
        Assert.Equal("thinking_start", new AssistantMessageEvent.ThinkingStart(0, partial).Type);
        Assert.Equal("thinking_delta", new AssistantMessageEvent.ThinkingDelta(0, "x", partial).Type);
        Assert.Equal("thinking_end", new AssistantMessageEvent.ThinkingEnd(0, "x", partial).Type);
        Assert.Equal("toolcall_start", new AssistantMessageEvent.ToolCallStart(0, partial).Type);
        Assert.Equal("toolcall_delta", new AssistantMessageEvent.ToolCallDelta(0, "{}", partial).Type);
        Assert.Equal("toolcall_end", new AssistantMessageEvent.ToolCallEnd(0, new ToolCall(), partial).Type);
        Assert.Equal("done", new AssistantMessageEvent.Done(StopReason.Stop, partial).Type);
        Assert.Equal("error", new AssistantMessageEvent.Error(StopReason.Error, partial).Type);
    }

    [Fact]
    public void Only_Done_And_Error_Are_Terminal()
    {
        var partial = Message();
        Assert.False(new AssistantMessageEvent.Start(partial).IsTerminal);
        Assert.False(new AssistantMessageEvent.TextDelta(0, "x", partial).IsTerminal);
        Assert.True(new AssistantMessageEvent.Done(StopReason.Stop, partial).IsTerminal);
        Assert.True(new AssistantMessageEvent.Error(StopReason.Error, partial).IsTerminal);
    }

    [Fact]
    public void Done_Rejects_Error_And_Aborted()
    {
        var message = Message();
        Assert.Throws<ArgumentException>(() =>
            AssistantMessageEventValidation.Validate(
                new AssistantMessageEvent.Done(StopReason.Error, message)));
        Assert.Throws<ArgumentException>(() =>
            AssistantMessageEventValidation.Validate(
                new AssistantMessageEvent.Done(StopReason.Aborted, message)));
    }

    [Fact]
    public void Error_Rejects_Non_Error_Reason()
    {
        var message = Message();
        Assert.Throws<ArgumentException>(() =>
            AssistantMessageEventValidation.Validate(
                new AssistantMessageEvent.Error(StopReason.Stop, message)));

        AssistantMessageEventValidation.Validate(
            new AssistantMessageEvent.Error(StopReason.Aborted, message));
    }

    [Fact]
    public void Valid_Terminal_Events_Pass()
    {
        var message = Message();
        AssistantMessageEventValidation.Validate(
            new AssistantMessageEvent.Done(StopReason.ToolUse, message));
        AssistantMessageEventValidation.Validate(
            new AssistantMessageEvent.Done(StopReason.Deferred, message));
    }
}
