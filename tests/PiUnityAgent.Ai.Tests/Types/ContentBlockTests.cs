using System.Text.Json;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class ContentBlockTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void TextContent_Serializes_With_Type_Discriminator()
    {
        var json = JsonSerializer.Serialize<ContentBlock>(
            new TextContent { Text = "hi" }, Options);

        Assert.Equal("{\"type\":\"text\",\"text\":\"hi\"}", json);
    }

    [Fact]
    public void ContentBlock_Deserializes_By_Type()
    {
        var block = JsonSerializer.Deserialize<ContentBlock>(
            "{\"type\":\"toolCall\",\"id\":\"call_1\",\"name\":\"query_hp\",\"arguments\":{}}",
            Options);

        var call = Assert.IsType<ToolCall>(block);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("query_hp", call.Name);
    }

    [Fact]
    public void ThinkingContent_Serializes_Signature()
    {
        var json = JsonSerializer.Serialize<ContentBlock>(
            new ThinkingContent { Thinking = "hm", ThinkingSignature = "sig" }, Options);

        Assert.Contains("\"type\":\"thinking\"", json);
        Assert.Contains("\"thinkingSignature\":\"sig\"", json);
    }

    [Fact]
    public void ContentBlock_Unknown_Discriminator_Throws()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ContentBlock>("{\"type\":\"bogus\"}", Options));
    }
}
