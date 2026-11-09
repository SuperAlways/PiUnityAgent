using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

public class AgentShapeTests
{
    private static AgentTool Echo()
        => new()
        {
            Name = "echo",
            Description = "Echo",
            Label = "Echo",
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = (_, args, _, _) => Task.FromResult(new AgentToolResult
            {
                Content = new ContentBlock[] { new TextContent { Text = args?.ToJsonString() ?? "{}" } },
            }),
        };

    [Fact]
    public void Event_Variants_Carry_The_Shape()
    {
        var events = new AgentEvent[]
        {
            new AgentEvent.AgentStart(),
            new AgentEvent.TurnStart(),
            new AgentEvent.ToolExecutionStart("call-1", "echo", new JsonObject()),
            new AgentEvent.AgentEnd(Array.Empty<Message>()),
        };
        Assert.Equal(4, events.Length);
        Assert.IsType<AgentEvent.AgentEnd>(events[^1]);
    }

    [Fact]
    public void Config_Defaults_Are_Parallel_And_Nullable()
    {
        var config = new AgentLoopConfig
        {
            Model = new Model { Id = "m", Name = "m", Api = "faux", Provider = "faux", BaseUrl = "http://localhost" },
            ConvertToLlm = messages => Task.FromResult<IReadOnlyList<Message>>(messages),
        };
        Assert.Equal(ToolExecutionMode.Parallel, config.ToolExecution);
        Assert.Null(config.StreamOptions);
        Assert.Null(config.BeforeToolCall);
        Assert.Null(config.AfterToolCall);
    }

    [Fact]
    public void Hook_And_Outcome_Records_RoundTrip()
    {
        var toolCall = new ToolCall { Id = "c1", Name = "echo" };
        var outcome = new AgentToolCallOutcome(
            toolCall, new AgentToolResult { IsError = true }, IsError: true) { DurationMs = 12 };
        Assert.Equal(12, outcome.DurationMs);
        Assert.True(outcome.IsError);

        var before = new BeforeToolCallResult(Block: true, Reason: "no", Terminate: true);
        Assert.True(before.Block);
        var after = new AfterToolCallResult(IsError: false, Terminate: true);
        Assert.True(after.Terminate);
    }

    [Fact]
    public void Default_Identity_Is_Pi_Attribution()
    {
        Assert.NotNull(AgentLoop.DefaultAgentIdentity.Attribution);
        Assert.Equal("pi", AgentLoop.DefaultAgentIdentity.Attribution!.Name);
    }

    [Fact]
    public void Context_Keeps_Mutable_Message_List()
    {
        var messages = new List<Message> { new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") } };
        var context = new AgentContext { Messages = messages, Tools = new[] { Echo() } };
        messages.Add(new UserMessage { Timestamp = 2, Content = MessageContent.From("again") });
        Assert.Equal(2, context.Messages.Count);
        Assert.Single(context.Tools!);
    }
}
