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
    [InlineData(StopReason.Stop, "\"stop\"")]
    [InlineData(StopReason.Length, "\"length\"")]
    [InlineData(StopReason.ToolUse, "\"toolUse\"")]
    [InlineData(StopReason.Error, "\"error\"")]
    [InlineData(StopReason.Aborted, "\"aborted\"")]
    [InlineData(StopReason.Deferred, "\"deferred\"")]
    public void StopReason_Serializes_Wire_Name(StopReason value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Options));
    }

    [Theory]
    [InlineData(StopReason.Pending, "\"pending\"")]
    [InlineData(StopReason.Stop, "\"stop\"")]
    [InlineData(StopReason.Length, "\"length\"")]
    [InlineData(StopReason.ToolUse, "\"toolUse\"")]
    [InlineData(StopReason.Error, "\"error\"")]
    [InlineData(StopReason.Aborted, "\"aborted\"")]
    [InlineData(StopReason.Deferred, "\"deferred\"")]
    public void StopReason_RoundTrips_Wire_Name(StopReason value, string wire)
    {
        Assert.Equal(value, JsonSerializer.Deserialize<StopReason>(wire, Options));
    }

    [Theory]
    [InlineData(ThinkingLevel.Minimal, "\"minimal\"")]
    [InlineData(ThinkingLevel.Low, "\"low\"")]
    [InlineData(ThinkingLevel.Medium, "\"medium\"")]
    [InlineData(ThinkingLevel.High, "\"high\"")]
    [InlineData(ThinkingLevel.XHigh, "\"xhigh\"")]
    [InlineData(ThinkingLevel.Max, "\"max\"")]
    public void ThinkingLevel_Serializes_Wire_Name(ThinkingLevel value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Options));
    }

    [Theory]
    [InlineData(ThinkingLevel.Minimal, "\"minimal\"")]
    [InlineData(ThinkingLevel.Low, "\"low\"")]
    [InlineData(ThinkingLevel.Medium, "\"medium\"")]
    [InlineData(ThinkingLevel.High, "\"high\"")]
    [InlineData(ThinkingLevel.XHigh, "\"xhigh\"")]
    [InlineData(ThinkingLevel.Max, "\"max\"")]
    public void ThinkingLevel_RoundTrips_Wire_Name(ThinkingLevel value, string wire)
    {
        Assert.Equal(value, JsonSerializer.Deserialize<ThinkingLevel>(wire, Options));
    }

    [Theory]
    [InlineData(ModelThinkingLevel.Off, "\"off\"")]
    [InlineData(ModelThinkingLevel.Minimal, "\"minimal\"")]
    [InlineData(ModelThinkingLevel.Low, "\"low\"")]
    [InlineData(ModelThinkingLevel.Medium, "\"medium\"")]
    [InlineData(ModelThinkingLevel.High, "\"high\"")]
    [InlineData(ModelThinkingLevel.XHigh, "\"xhigh\"")]
    [InlineData(ModelThinkingLevel.Max, "\"max\"")]
    public void ModelThinkingLevel_Serializes_Wire_Name(ModelThinkingLevel value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Options));
    }

    [Theory]
    [InlineData(ModelThinkingLevel.Off, "\"off\"")]
    [InlineData(ModelThinkingLevel.Minimal, "\"minimal\"")]
    [InlineData(ModelThinkingLevel.Low, "\"low\"")]
    [InlineData(ModelThinkingLevel.Medium, "\"medium\"")]
    [InlineData(ModelThinkingLevel.High, "\"high\"")]
    [InlineData(ModelThinkingLevel.XHigh, "\"xhigh\"")]
    [InlineData(ModelThinkingLevel.Max, "\"max\"")]
    public void ModelThinkingLevel_RoundTrips_Wire_Name(ModelThinkingLevel value, string wire)
    {
        Assert.Equal(value, JsonSerializer.Deserialize<ModelThinkingLevel>(wire, Options));
    }

    [Fact]
    public void Enum_Deserializes_Wire_Name()
    {
        Assert.Equal(StopReason.ToolUse,
            JsonSerializer.Deserialize<StopReason>("\"toolUse\"", Options));
        Assert.Equal(ThinkingLevel.XHigh,
            JsonSerializer.Deserialize<ThinkingLevel>("\"xhigh\"", Options));
    }

    [Fact]
    public void StopReason_Unknown_Wire_Name_Deserialize_Throws_JsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<StopReason>("\"nope\"", Options));
    }

    [Fact]
    public void StopReason_Null_Json_Deserialize_Throws_JsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<StopReason>("null", Options));
    }

    [Fact]
    public void StopReason_FromWire_Unknown_Wire_Name_Throws_ArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StopReasonExtensions.FromWire("nope"));
    }
}
