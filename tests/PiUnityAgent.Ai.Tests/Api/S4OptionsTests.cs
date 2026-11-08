using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Api;

public class S4OptionsTests
{
    [Fact]
    public void OpenAi_Options_Expose_Pi_Fields()
    {
        var c = new OpenAiCompletionsOptions { ReasoningEffort = ThinkingLevel.High };
        Assert.Equal(ThinkingLevel.High, c.ReasoningEffort);

        var r = new OpenAiResponsesOptions
        {
            ReasoningEffort = ThinkingLevel.Medium,
            ReasoningSummary = OpenAiReasoningSummary.Auto,
            ServiceTier = "flex",
        };
        Assert.Equal("flex", r.ServiceTier);
        Assert.Equal(OpenAiReasoningSummary.Auto, r.ReasoningSummary);
    }

    [Fact]
    public void OpenAi_Options_ToolChoice_Is_JsonNode_Native_Shape()
    {
        var toolChoice = new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = "get_weather" },
        };

        var c = new OpenAiCompletionsOptions { ToolChoice = toolChoice };
        Assert.Same(toolChoice, c.ToolChoice);

        var r = new OpenAiResponsesOptions { ToolChoice = JsonValue.Create("auto") };
        Assert.Equal("auto", r.ToolChoice!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_Options_Carry_Stream_Base_Fields()
    {
        var budgets = new ThinkingBudgets { Minimal = 512, High = 8192 };
        var c = new OpenAiCompletionsOptions
        {
            ThinkingBudgets = budgets,
            MaxTokens = 100,
            ReasoningEffort = ThinkingLevel.Max,
        };
        Assert.Same(budgets, c.ThinkingBudgets);
        Assert.Equal(100, c.MaxTokens);
        Assert.Equal(ThinkingLevel.Max, c.ReasoningEffort);

        var r = new OpenAiResponsesOptions { MaxTokens = 55, ServiceTier = "priority" };
        Assert.Equal(55, r.MaxTokens);
        Assert.Equal("priority", r.ServiceTier);
    }
}
