using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

public class AgentStateTests
{
    private static AgentTool Tool(string name) => new()
    {
        Name = name,
        Description = name,
        Label = name,
        Parameters = new JsonObject { ["type"] = "object" },
        Execute = (_, _, _, _) => Task.FromResult(new AgentToolResult()),
    };

    private static SystemMessage System(string text, long timestamp = 1)
        => new() { Content = MessageContent.From(text), Timestamp = timestamp };

    [Fact]
    public void Defaults_Match_Pi_Default_Model_And_Off_Thinking()
    {
        var state = new AgentState(initialState: null);

        Assert.Equal("unknown", state.Model.Id);
        Assert.Equal("unknown", state.Model.Provider);
        Assert.Equal(0, state.Model.ContextWindow);
        Assert.Equal(0, state.Model.MaxTokens);
        Assert.Empty(state.Model.Input);
        Assert.Equal(ModelThinkingLevel.Off, state.ThinkingLevel);
        Assert.Empty(state.Messages);
        Assert.Empty(state.Tools);
        Assert.False(state.IsStreaming);
        Assert.Null(state.StreamingMessage);
        Assert.Empty(state.PendingToolCalls);
        Assert.Null(state.ErrorMessage);
    }

    [Fact]
    public void Initial_SystemPrompt_And_Tools_Fold_Into_Leading_System_Message()
    {
        var state = new AgentState(new AgentInitialState
        {
            SystemPrompt = "you are pi",
            Tools = new[] { Tool("echo") },
        });

        var first = Assert.IsType<SystemMessage>(state.Messages[0]);
        Assert.Equal("you are pi", Assert.IsType<TextContent>(first.Content.ToBlocks()[0]).Text);
        Assert.Single(first.ToolsAdded!);
        Assert.Equal("echo", first.ToolsAdded![0].Name);
        Assert.Equal("you are pi", state.SystemPrompt);
    }

    [Fact]
    public void Existing_Leading_System_Message_Is_Not_Duplicated()
    {
        var existing = System("custom");
        var state = new AgentState(new AgentInitialState
        {
            SystemPrompt = "ignored",
            Messages = new Message[] { existing },
        });

        Assert.Single(state.Messages);
        Assert.Same(existing, state.Messages[0]);
    }

    [Fact]
    public void SystemPrompt_Replays_Content_Sections_And_Tools()
    {
        var messages = new Message[]
        {
            new SystemMessage
            {
                Content = MessageContent.From("base"),
                Sections = new Dictionary<string, string?> { ["tone"] = "calm" },
                ToolsAdded = new[] { Tool("echo").Definition },
                Timestamp = 1,
            },
            new SystemMessage
            {
                Content = MessageContent.From("later"),
                Sections = new Dictionary<string, string?> { ["tone"] = null, ["mode"] = "strict" },
                ToolsRemoved = new[] { new ToolReference { Name = "echo" } },
                Timestamp = 2,
            },
        };

        var prompt = AgentTranscript.GetCurrentSystemPrompt(messages);
        Assert.Contains("base", prompt);
        Assert.Contains("later", prompt);
        Assert.Contains("strict", prompt);
        Assert.DoesNotContain("calm", prompt);
    }

    [Fact]
    public void Messages_And_Tools_Setters_Copy_Top_Level_Array()
    {
        var source = new List<Message> { System("s") };
        var state = new AgentState(null);
        state.Messages = source;
        source.Add(new UserMessage { Content = MessageContent.From("u"), Timestamp = 2 });

        Assert.Single(state.Messages);

        var tools = new List<AgentTool> { Tool("echo") };
        state.Tools = tools;
        tools.Add(Tool("other"));
        Assert.Single(state.Tools);
    }
}
