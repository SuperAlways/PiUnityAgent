using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Api;

public class S5OptionsTests
{
    [Fact]
    public void Anthropic_Options_Expose_Pi_Fields()
    {
        var o = new AnthropicOptions
        {
            ThinkingEnabled = true,
            ThinkingBudgetTokens = 1024,
            Effort = ThinkingLevel.High,
            ThinkingDisplay = AnthropicThinkingDisplay.Summarized,
            InterleavedThinking = true,
        };
        Assert.True(o.ThinkingEnabled);
        Assert.Equal(1024, o.ThinkingBudgetTokens);
        Assert.Equal(ThinkingLevel.High, o.Effort);
        Assert.Equal(AnthropicThinkingDisplay.Summarized, o.ThinkingDisplay);
        Assert.True(o.InterleavedThinking);
    }

    [Fact]
    public void Anthropic_Options_Display_Has_Omitted_Value()
    {
        var o = new AnthropicOptions { ThinkingDisplay = AnthropicThinkingDisplay.Omitted };
        Assert.Equal(AnthropicThinkingDisplay.Omitted, o.ThinkingDisplay);
    }

    [Fact]
    public void Google_Options_Expose_Thinking()
    {
        var o = new GoogleOptions
        {
            Thinking = new GoogleThinkingOptions { Enabled = true, BudgetTokens = 2048 },
        };
        Assert.True(o.Thinking!.Enabled);
        Assert.Equal(2048, o.Thinking.BudgetTokens);

        var leveled = new GoogleOptions
        {
            Thinking = new GoogleThinkingOptions { Enabled = true, Level = ThinkingLevel.High },
        };
        Assert.Equal(ThinkingLevel.High, leveled.Thinking!.Level);
    }

    [Fact]
    public void S5_Options_ToolChoice_Is_JsonNode_Native_Shape()
    {
        var toolChoice = new JsonObject { ["type"] = "tool", ["name"] = "get_weather" };
        var a = new AnthropicOptions { ToolChoice = toolChoice };
        var g = new GoogleOptions { ToolChoice = JsonValue.Create("auto") };
        Assert.Same(toolChoice, a.ToolChoice);
        Assert.Equal("auto", g.ToolChoice!.GetValue<string>());
    }

    [Fact]
    public void S5_Options_Carry_Stream_Base_Fields()
    {
        var a = new AnthropicOptions { MaxTokens = 128, ApiKey = "k" };
        var g = new GoogleOptions { MaxTokens = 64, ApiKey = "k" };
        Assert.Equal(128, a.MaxTokens);
        Assert.Equal(64, g.MaxTokens);
    }
}
