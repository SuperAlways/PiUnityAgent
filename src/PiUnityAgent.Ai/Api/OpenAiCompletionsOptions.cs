using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Api;

/// <summary>
/// pi OpenAICompletionsOptions：openai-completions 专属选项，叠在 <see cref="StreamOptions"/> 之上。
/// </summary>
public sealed record OpenAiCompletionsOptions : StreamOptions
{
    /// <summary>pi toolChoice 的 native 形状；C# 不做 SDK union。</summary>
    public JsonNode? ToolChoice { get; init; }

    /// <summary>pi reasoningEffort；值域与 ThinkingLevel 一致。</summary>
    public ThinkingLevel? ReasoningEffort { get; init; }

    /// <summary>pi thinkingBudgets；四档预算。</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}
