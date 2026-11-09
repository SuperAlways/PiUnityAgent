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
        var (result, error) = args.Finish();
        Assert.Null(error);
        Assert.Equal("Hangzhou", result["city"]!.GetValue<string>());
    }

    [Fact]
    public void ToolCallArguments_Accumulates_Nested_Objects_And_Arrays()
    {
        var args = new ToolCallArguments();
        args.Append("{\"list\":[1,2],\"nested\":");
        args.Append("{\"deep\":true}}");

        var (result, error) = args.Finish();

        Assert.Null(error);
        Assert.Equal(2, result["list"]!.AsArray().Count);
        Assert.True(result["nested"]!["deep"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolCallArguments_Empty_Text_Finishes_As_Empty_Object()
    {
        var args = new ToolCallArguments();
        var (result, error) = args.Finish();
        Assert.Null(error);
        Assert.Empty(result);
    }

    [Fact]
    public void ToolCallArguments_Whitespace_Text_Finishes_As_Empty_Object()
    {
        var args = new ToolCallArguments();
        args.Append("   \n");
        var (result, error) = args.Finish();
        Assert.Null(error);
        Assert.Empty(result);
    }

    [Fact]
    public void ToolCallArguments_Exposes_Growing_Partial_Json()
    {
        var arguments = new ToolCallArguments();
        var first = arguments.Append("{\"city\"");
        Assert.Equal("{}", first.ToJsonString());
        var second = arguments.Append(":\"SF\"}");
        Assert.Equal("{\"city\":\"SF\"}", second.ToJsonString());
    }

    [Fact]
    public void ToolCallArguments_Partial_Snapshot_Nests_Containers()
    {
        // 钉住 partial 对象图（嵌套对象/数组/数字/布尔）能整体转成 JsonObject——
        // ToObject 若改走别的序列化路径，这里先红。
        // 快照规则：已完成的成员显示；键已读但值未开始的成员省略。
        var arguments = new ToolCallArguments();
        Assert.Equal("{\"list\":[1,2]}", arguments.Append("{\"list\":[1,2],").ToJsonString());
        Assert.Equal("{\"list\":[1,2],\"nested\":{}}", arguments.Append("\"nested\":{\"deep\":").ToJsonString());
        Assert.Equal("{\"list\":[1,2],\"nested\":{\"deep\":true}}", arguments.Append("true}}").ToJsonString());
    }

    [Fact]
    public void ToolCallArguments_Finish_Repairs_Recoverable_Json()
    {
        var arguments = new ToolCallArguments();
        arguments.Append("{\"text\":\"line1\nline2\"}");
        var (parsed, error) = arguments.Finish();
        Assert.Equal("line1\nline2", parsed["text"]!.GetValue<string>());
        Assert.Null(error);
    }

    [Fact]
    public void ToolCallArguments_Finish_Reports_Unrecoverable_Json()
    {
        var arguments = new ToolCallArguments();
        arguments.Append("{\"path\":\"/x\",\"oops\"");
        var (parsed, error) = arguments.Finish();
        Assert.Empty(parsed);
        Assert.Contains("Received arguments:", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolCallArguments_Malformed_Json_Finishes_With_ArgumentsError()
    {
        // 终态不抛 JsonException（pi parseToolCallArguments）：畸形参数回退空对象 + argumentsError。
        var args = new ToolCallArguments();
        args.Append("{\"city\": \"Hang");
        var (parsed, error) = args.Finish();
        Assert.Empty(parsed);
        Assert.NotNull(error);
        Assert.Contains("Received arguments:", error!, StringComparison.Ordinal);
        Assert.Contains("\"city\": \"Hang", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolCallArguments_Non_Object_Json_Finishes_With_ArgumentsError()
    {
        var args = new ToolCallArguments();
        args.Append("[1,2]");
        var (parsed, error) = args.Finish();
        Assert.Empty(parsed);
        Assert.NotNull(error);
        Assert.Contains("JSON object", error!, StringComparison.Ordinal);
    }
}
