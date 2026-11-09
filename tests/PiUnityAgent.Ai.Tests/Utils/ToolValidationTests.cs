using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Ai.Utils;

namespace PiUnityAgent.Ai.Tests.Utils;

public class ToolValidationTests
{
    private static Tool EchoTool(JsonObject schema)
        => new() { Name = "echo", Description = "Echo", Parameters = schema };

    private static ToolCall Call(string name, JsonObject args, string? argumentsError = null)
        => new() { Id = "call-1", Name = name, Arguments = args, ArgumentsError = argumentsError };

    private static JsonObject NumberSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "number" } },
        };

    [Fact]
    public void ValidateToolCall_Throws_When_Tool_Not_Found()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ToolValidation.ValidateToolCall(Array.Empty<Tool>(), Call("nope", new JsonObject())));
        Assert.Equal("Tool \"nope\" not found", error.Message);
    }

    [Fact]
    public void ArgumentsError_Is_Reported_Without_Validation()
    {
        var tool = EchoTool(NumberSchema());
        var error = Assert.Throws<InvalidOperationException>(() =>
            ToolValidation.ValidateToolArguments(tool, Call("echo", new JsonObject(), "{bad")));
        Assert.Equal("Invalid arguments for tool \"echo\": {bad", error.Message);
    }

    [Fact]
    public void Whitespace_ArgumentsError_Is_Still_Reported()
    {
        var tool = EchoTool(NumberSchema());
        var error = Assert.Throws<InvalidOperationException>(() =>
            ToolValidation.ValidateToolArguments(tool, Call("echo", new JsonObject(), " ")));
        Assert.Equal("Invalid arguments for tool \"echo\":  ", error.Message);
    }

    [Fact]
    public void Numeric_String_Is_Coerced_To_Number()
    {
        var tool = EchoTool(NumberSchema());
        var result = ToolValidation.ValidateToolArguments(tool, Call("echo", new JsonObject { ["value"] = "42" }));
        Assert.Equal("42", result!["value"]!.ToJsonString());
    }

    [Fact]
    public void Validation_Failure_Uses_Pi_Error_Message()
    {
        var tool = EchoTool(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["a"] = new JsonObject { ["type"] = "string" }, ["b"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("b"),
        });
        var error = Assert.Throws<InvalidOperationException>(() =>
            ToolValidation.ValidateToolArguments(tool, Call("echo", new JsonObject { ["a"] = "x" })));
        Assert.Contains("Validation failed for tool \"echo\":", error.Message, StringComparison.Ordinal);
        Assert.Contains("  - b: missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("Received arguments:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_Does_Not_Mutate_ToolCall_Arguments()
    {
        var tool = EchoTool(NumberSchema());
        var arguments = new JsonObject { ["value"] = "42" };
        _ = ToolValidation.ValidateToolArguments(tool, Call("echo", arguments));
        Assert.Equal("42", arguments["value"]!.GetValue<string>());
    }

    [Fact]
    public void ValidateToolCall_Uses_First_Matching_Tool()
    {
        var first = EchoTool(NumberSchema());
        var second = new Tool
        {
            Name = "echo",
            Description = "Other",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } },
            },
        };
        var result = ToolValidation.ValidateToolCall(
            new[] { first, second }, Call("echo", new JsonObject { ["value"] = "42" }));
        Assert.Equal("42", result!["value"]!.ToJsonString());
    }

    [Fact]
    public void Single_Value_Is_Wrapped_Into_Array()
    {
        var tool = EchoTool(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["xs"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } },
            },
        });
        var result = ToolValidation.ValidateToolArguments(tool, Call("echo", new JsonObject { ["xs"] = "7" }));
        Assert.Equal("[7]", result!["xs"]!.ToJsonString());
    }

    [Fact]
    public void Optional_Null_Is_Dropped()
    {
        var tool = EchoTool(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["a"] = new JsonObject { ["type"] = "string" } },
        });
        var result = ToolValidation.ValidateToolArguments(tool, Call("echo", new JsonObject { ["a"] = null }));
        Assert.False(result!.AsObject().ContainsKey("a"));
    }

    [Fact]
    public void Validator_Is_Cached_Per_Schema_Instance()
    {
        var schema = NumberSchema();
        Assert.Same(ToolValidation.ValidatorFor(schema), ToolValidation.ValidatorFor(schema));
    }

}
