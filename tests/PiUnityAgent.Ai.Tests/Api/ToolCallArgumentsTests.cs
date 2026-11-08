using System.Text.Json;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Api;
using Xunit;

namespace PiUnityAgent.Ai.Tests.Api;

public class ToolCallArgumentsTests
{
    [Fact]
    public void ToolCallArguments_Accumulates_Json_Fragments()
    {
        var args = new ToolCallArguments();
        args.Append("{\"city\":");
        args.Append("\"Hangzhou\"}");
        var result = args.Finish();
        Assert.Equal("Hangzhou", result["city"]!.GetValue<string>());
    }

    [Fact]
    public void ToolCallArguments_Accumulates_Nested_Objects_And_Arrays()
    {
        var args = new ToolCallArguments();
        args.Append("{\"list\":[1,2],\"nested\":");
        args.Append("{\"deep\":true}}");

        var result = args.Finish();

        Assert.Equal(2, result["list"]!.AsArray().Count);
        Assert.True(result["nested"]!["deep"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolCallArguments_Empty_Text_Finishes_As_Empty_Object()
    {
        var args = new ToolCallArguments();
        Assert.Empty(args.Finish());
    }

    [Fact]
    public void ToolCallArguments_Whitespace_Text_Finishes_As_Empty_Object()
    {
        var args = new ToolCallArguments();
        args.Append("   \n");
        Assert.Empty(args.Finish());
    }

    [Fact]
    public void ToolCallArguments_Malformed_Json_Throws_JsonException()
    {
        var args = new ToolCallArguments();
        args.Append("{\"city\": \"Hang");
        Assert.Throws<JsonException>(() => args.Finish());
    }

    [Fact]
    public void ToolCallArguments_Non_Object_Json_Throws_JsonException()
    {
        var args = new ToolCallArguments();
        args.Append("[1,2]");
        Assert.Throws<JsonException>(() => args.Finish());
    }

    [Fact]
    public void ToolCallArguments_Does_Not_Expose_Partial_Json()
    {
        // The accumulator only hands out the raw text; no partial JSON object escapes mid-stream.
        var args = new ToolCallArguments();
        args.Append("{\"a\":");
        Assert.Throws<JsonException>(() => args.Finish());
    }
}
