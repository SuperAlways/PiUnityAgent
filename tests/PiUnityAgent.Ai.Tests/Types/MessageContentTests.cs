using System.Text.Json;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class MessageContentTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Text_Content_Serializes_As_String()
    {
        Assert.Equal("\"hi\"",
            JsonSerializer.Serialize(MessageContent.From("hi"), Options));
    }

    [Fact]
    public void Blocks_Content_Serializes_As_Array()
    {
        var content = MessageContent.From(new ContentBlock[]
        {
            new TextContent { Text = "hi" },
        });

        var json = JsonSerializer.Serialize(content, Options);
        Assert.StartsWith("[", json);
        Assert.Contains("\"type\":\"text\"", json);
    }

    [Fact]
    public void Text_Content_Converts_To_Blocks()
    {
        var blocks = MessageContent.From("hi").ToBlocks();
        var text = Assert.IsType<TextContent>(Assert.Single(blocks));
        Assert.Equal("hi", text.Text);
    }
}
