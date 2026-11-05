using System.Text.Json;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class EnumWireTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Theory]
    [InlineData(StopReason.Pending, "\"pending\"")]
    [InlineData(StopReason.ToolUse, "\"toolUse\"")]
    [InlineData(StopReason.Error, "\"error\"")]
    public void StopReason_Serializes_Wire_Name(StopReason value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Options));
    }

    [Theory]
    [InlineData(ThinkingLevel.XHigh, "\"xhigh\"")]
    [InlineData(ThinkingLevel.Max, "\"max\"")]
    public void ThinkingLevel_Serializes_Wire_Name(ThinkingLevel value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Options));
    }

    [Fact]
    public void Enum_Deserializes_Wire_Name()
    {
        Assert.Equal(StopReason.ToolUse,
            JsonSerializer.Deserialize<StopReason>("\"toolUse\"", Options));
        Assert.Equal(ThinkingLevel.XHigh,
            JsonSerializer.Deserialize<ThinkingLevel>("\"xhigh\"", Options));
    }
}
