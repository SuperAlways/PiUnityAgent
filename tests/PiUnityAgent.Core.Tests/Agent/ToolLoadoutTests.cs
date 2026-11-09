using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

public class ToolLoadoutTests
{
    private static AgentTool Tool(string name, string description = "d")
        => new()
        {
            Name = name, Description = description, Label = name,
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = (_, _, _, _) => Task.FromResult(new AgentToolResult()),
        };

    private static AgentContext Context(IReadOnlyList<AgentTool>? tools, params Message[] messages)
        => new() { Messages = messages.ToList(), Tools = tools };

    [Fact]
    public void Tool_Changes_Insert_A_System_Message_Before_The_First_NonSystem_Prompt()
    {
        var user = new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") };
        var result = ToolLoadout.DeclareToolChanges(Context(new[] { Tool("echo") }), new Message[] { user }, 2);
        var system = Assert.IsType<SystemMessage>(result[0]);
        Assert.NotEmpty(system.ToolsAdded!);
        Assert.Equal("echo", system.ToolsAdded![0].Name);
        Assert.Equal("", Assert.IsType<MessageContent.Text>(system.Content).Value);
        Assert.Equal(user, result[1]);
    }

    [Fact]
    public void Changed_Definition_Is_Removal_Plus_Addition()
    {
        var previous = new SystemMessage
        {
            Timestamp = 1, Content = MessageContent.From(""),
            ToolsAdded = new[] { new Tool { Name = "echo", Description = "old", Parameters = new JsonObject { ["type"] = "object" } } },
        };
        var result = ToolLoadout.DeclareToolChanges(
            Context(new[] { Tool("echo", "new") }), Array.Empty<Message>(), 2,
            new[] { previous });
        var system = Assert.IsType<SystemMessage>(result[0]);
        Assert.Contains(system.ToolsAdded!, t => t.Name == "echo");
        Assert.Contains(system.ToolsRemoved!, r => r.Name == "echo");
    }

    [Fact]
    public void No_Changes_Do_Not_Insert_A_Message()
    {
        var user = new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") };
        var result = ToolLoadout.DeclareToolChanges(Context(null), new Message[] { user }, 2);
        Assert.Single(result);
        Assert.IsType<UserMessage>(result[0]);
    }
}
