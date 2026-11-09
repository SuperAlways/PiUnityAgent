using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Tools;

public class AgentToolTests
{
    private static AgentTool MakeTool() => new()
    {
        Name = "echo",
        Description = "Echo the arguments",
        Label = "Echo",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } },
        },
        Execute = (toolCallId, args, _, update) => Task.FromResult(new AgentToolResult
        {
            Content = new ContentBlock[] { new TextContent { Text = args!["value"]!.GetValue<string>() } },
            Details = new JsonObject { ["callId"] = toolCallId },
        }),
    };

    [Fact]
    public void AgentTool_Defaults_Are_Null()
    {
        var tool = MakeTool();
        Assert.Null(tool.ConstrainedSampling);
        Assert.Null(tool.PrepareArguments);
        Assert.Null(tool.OutputSchema);
        Assert.Null(tool.Replay);
        Assert.Null(tool.ExecutionMode);
    }

    [Fact]
    public void Definition_Projects_The_Declaration_Four_Fields()
    {
        var tool = MakeTool();
        var definition = tool.Definition;
        Assert.Equal(tool.Name, definition.Name);
        Assert.Equal(tool.Description, definition.Description);
        Assert.Same(tool.Parameters, definition.Parameters);
        Assert.Equal(tool.ConstrainedSampling, definition.ConstrainedSampling);
    }

    [Fact]
    public async Task Execute_Returns_The_Declared_Result()
    {
        var tool = MakeTool();
        var result = await tool.Execute("call-1", new JsonObject { ["value"] = "hi" }, CancellationToken.None, null);
        Assert.Equal("hi", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Equal("call-1", result.Details!["callId"]!.GetValue<string>());
        Assert.False(result.IsError);
    }
}
