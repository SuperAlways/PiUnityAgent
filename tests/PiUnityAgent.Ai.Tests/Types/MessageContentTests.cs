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

    [Fact]
    public void String_Json_Deserializes_To_Text()
    {
        var content = JsonSerializer.Deserialize<MessageContent>("\"hi\"", Options);

        var text = Assert.IsType<MessageContent.Text>(content);
        Assert.Equal("hi", text.Value);
    }

    [Fact]
    public void Array_Json_Deserializes_To_Blocks_With_ToolCall()
    {
        var content = JsonSerializer.Deserialize<MessageContent>(
            "[{\"type\":\"toolCall\",\"id\":\"call_1\",\"name\":\"query_hp\",\"arguments\":{}}]",
            Options);

        var blocks = Assert.IsType<MessageContent.Blocks>(content);
        var call = Assert.IsType<ToolCall>(Assert.Single(blocks.Value));
        Assert.Equal("call_1", call.Id);
        Assert.Equal("query_hp", call.Name);
    }

    [Fact]
    public void Null_Json_Deserializes_To_Empty_Blocks()
    {
        var content = JsonSerializer.Deserialize<MessageContent>("null", Options);

        var blocks = Assert.IsType<MessageContent.Blocks>(content);
        Assert.Empty(blocks.Value);
    }

    [Fact]
    public void Message_RoundTrip_Preserves_Role_And_Content()
    {
        var system = new SystemMessage
        {
            Content = MessageContent.From("base"),
            Timestamp = 1,
        };
        var user = new UserMessage
        {
            Content = MessageContent.From(new ContentBlock[] { new TextContent { Text = "hi" } }),
            Timestamp = 2,
        };

        var systemBack = JsonSerializer.Deserialize<SystemMessage>(
            JsonSerializer.Serialize(system, Options), Options);
        Assert.NotNull(systemBack);
        Assert.Equal("system", systemBack.Role);
        Assert.Equal(1, systemBack.Timestamp);
        var systemText = Assert.IsType<MessageContent.Text>(systemBack.Content);
        Assert.Equal("base", systemText.Value);

        var userBack = JsonSerializer.Deserialize<UserMessage>(
            JsonSerializer.Serialize(user, Options), Options);
        Assert.NotNull(userBack);
        Assert.Equal("user", userBack.Role);
        Assert.Equal(2, userBack.Timestamp);
        var userBlocks = Assert.IsType<MessageContent.Blocks>(userBack.Content);
        var text = Assert.IsType<TextContent>(Assert.Single(userBlocks.Value));
        Assert.Equal("hi", text.Text);
    }
}
