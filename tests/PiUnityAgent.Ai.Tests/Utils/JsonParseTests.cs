using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Utils;

namespace PiUnityAgent.Ai.Tests.Utils;

public class JsonParseTests
{
    [Fact]
    public void ParseToolCallArguments_Returns_Object_And_No_Error()
    {
        var (arguments, error) = JsonParse.ParseToolCallArguments("{\"path\":\"/x\",\"n\":2}");
        Assert.Equal(2, arguments["n"]!.GetValue<int>());
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseToolCallArguments_Empty_Input_Is_Empty_Object(string? input)
    {
        var (arguments, error) = JsonParse.ParseToolCallArguments(input);
        Assert.Empty(arguments);
        Assert.Null(error);
    }

    [Fact]
    public void ParseToolCallArguments_Repairs_Recoverable_Text()
    {
        var (arguments, error) = JsonParse.ParseToolCallArguments("{\"text\":\"line1\nline2\"}");
        Assert.Equal("line1\nline2", arguments["text"]!.GetValue<string>());
        Assert.Null(error);
    }

    [Fact]
    public void ParseToolCallArguments_Unrecoverable_Keeps_Received_Arguments()
    {
        var raw = "{\"path\":\"/x\",\"oops\"";
        var (arguments, error) = JsonParse.ParseToolCallArguments(raw);
        Assert.Empty(arguments);
        Assert.NotNull(error);
        Assert.Contains("Received arguments:", error, StringComparison.Ordinal);
        Assert.Contains(raw, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseToolCallArguments_Rejects_NonObject_Json()
    {
        var (arguments, error) = JsonParse.ParseToolCallArguments("[1,2,3]");
        Assert.Empty(arguments);
        Assert.NotNull(error);
        Assert.Contains("Received arguments:", error, StringComparison.Ordinal);
    }
}
