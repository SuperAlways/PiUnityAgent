using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Api;

/// <summary>pi reasoning.summary 字面量联合。</summary>
public enum OpenAiReasoningSummary
{
    Auto,
    Detailed,
    Concise
}

/// <summary>
/// pi OpenAIResponsesOptions：openai-responses 专属选项，叠在 <see cref="StreamOptions"/> 之上。
/// </summary>
public sealed record OpenAiResponsesOptions : StreamOptions
{
    /// <summary>pi reasoningEffort；值域与 ThinkingLevel 一致。</summary>
    public ThinkingLevel? ReasoningEffort { get; init; }

    /// <summary>pi reasoning.summary。</summary>
    public OpenAiReasoningSummary? ReasoningSummary { get; init; }

    /// <summary>pi serviceTier 的 native 字符串（flex/priority/fast/default/auto）。</summary>
    public string? ServiceTier { get; init; }

    /// <summary>pi toolChoice 的 native 形状。</summary>
    public JsonNode? ToolChoice { get; init; }
}
