using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Tools;

public class AgentToolResultTests
{
    [Fact]
    public void Defaults_Are_Non_Error_And_Empty()
    {
        var result = new AgentToolResult();
        Assert.Empty(result.Content);
        Assert.Null(result.Details);
        Assert.Null(result.StructuredContent);
        Assert.Null(result.Usage);
        Assert.False(result.IsError);
        Assert.False(result.Terminate);
    }

    [Fact]
    public void FromError_Text_Details_And_Flag()
    {
        var result = AgentToolResult.FromError("boom");
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Equal("boom", text.Text);
        var details = Assert.IsType<JsonObject>(result.Details);
        Assert.Empty(details);
        Assert.True(result.IsError);
    }

    [Fact]
    public void FromError_Keeps_Caller_Details()
    {
        var details = new JsonObject { ["code"] = 7 };
        var result = AgentToolResult.FromError("boom", details);
        Assert.Same(details, result.Details);
    }

    [Fact]
    public void Content_Can_Carry_Text_And_Image()
    {
        var result = new AgentToolResult
        {
            Content = new ContentBlock[]
            {
                new TextContent { Text = "see image" },
                new ImageContent { Data = "AAAA", MimeType = "image/png" },
            },
        };
        Assert.Equal(2, result.Content.Count);
        Assert.IsType<ImageContent>(result.Content[1]);
    }
}
