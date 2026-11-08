using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Api;

/// <summary>
/// pi GoogleOptions.thinking：Google 思考控制（google-generative-ai.ts）。
/// </summary>
public sealed record GoogleThinkingOptions
{
    public bool Enabled { get; init; }

    /// <summary>pi budgetTokens：-1 动态、0 禁用。</summary>
    public int? BudgetTokens { get; init; }

    /// <summary>pi level：Gemini 3 离散档位。</summary>
    public ThinkingLevel? Level { get; init; }
}

/// <summary>
/// pi GoogleOptions：google-generative-ai 专属选项，叠在 <see cref="StreamOptions"/> 之上。
/// </summary>
public sealed record GoogleOptions : StreamOptions
{
    /// <summary>pi toolChoice 的 native 形状；C# 不做 SDK union。</summary>
    public JsonNode? ToolChoice { get; init; }

    /// <summary>pi thinking：思考控制。</summary>
    public GoogleThinkingOptions? Thinking { get; init; }
}
