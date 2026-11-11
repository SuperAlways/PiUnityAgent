using System;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Utils;

namespace PiUnityAgent.Ai.Tests.Utils;

public class StringEnumTests
{
    [Fact]
    public void Creates_String_Schema_With_Enum_Values()
    {
        var schema = StringEnum.Create(new[] { "add", "subtract", "multiply", "divide" });
        Assert.Equal("string", schema["type"]!.GetValue<string>());
        var enumValues = Assert.IsType<JsonArray>(schema["enum"]);
        Assert.Equal(4, enumValues.Count);
        Assert.Equal("add", enumValues[0]!.GetValue<string>());
        Assert.Equal("subtract", enumValues[1]!.GetValue<string>());
        Assert.Equal("multiply", enumValues[2]!.GetValue<string>());
        Assert.Equal("divide", enumValues[3]!.GetValue<string>());
        // pi 真值展开：未给 description/default 时键不出现。
        Assert.False(schema.ContainsKey("description"));
        Assert.False(schema.ContainsKey("default"));
    }

    [Fact]
    public void Includes_Description_And_Default_When_NonEmpty()
    {
        var schema = StringEnum.Create(
            new[] { "add", "subtract" },
            description: "The operation to perform",
            defaultValue: "add");
        Assert.Equal("The operation to perform", schema["description"]!.GetValue<string>());
        Assert.Equal("add", schema["default"]!.GetValue<string>());
    }

    [Fact]
    public void Omits_Empty_Description_And_Default()
    {
        // pi 是真值展开：空串同样省略（但空白串是真值，保留）。
        var empty = StringEnum.Create(new[] { "a" }, description: "", defaultValue: "");
        Assert.False(empty.ContainsKey("description"));
        Assert.False(empty.ContainsKey("default"));

        var whitespace = StringEnum.Create(new[] { "a" }, description: " ", defaultValue: " ");
        Assert.True(whitespace.ContainsKey("description"));
        Assert.True(whitespace.ContainsKey("default"));
    }
}
