using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Api;

/// <summary>
/// pi AnthropicThinkingDisplay：思考内容在 API 响应中的回传形态（anthropic-messages.ts）。
/// </summary>
public enum AnthropicThinkingDisplay
{
    /// <summary>thinking 块含摘要思考文本。</summary>
    Summarized,

    /// <summary>thinking 块返回空文本，仅加密签名照常回传（多轮续接用）。</summary>
    Omitted,
}

/// <summary>
/// pi AnthropicOptions：anthropic-messages 专属选项，叠在 <see cref="StreamOptions"/> 之上。
/// </summary>
public sealed record AnthropicOptions : StreamOptions
{
    /// <summary>pi thinkingEnabled：是否开扩展思考；自适应模型由模型自决，老模型走预算。</summary>
    public bool? ThinkingEnabled { get; init; }

    /// <summary>pi thinkingBudgetTokens：扩展思考 token 预算（仅老模型）。</summary>
    public int? ThinkingBudgetTokens { get; init; }

    /// <summary>pi effort：自适应思考档位；值域与 <see cref="ThinkingLevel"/> 一致。</summary>
    public ThinkingLevel? Effort { get; init; }

    /// <summary>pi thinkingDisplay：thinking 回传形态；默认 Summarized。</summary>
    public AnthropicThinkingDisplay? ThinkingDisplay { get; init; }

    /// <summary>pi interleavedThinking：非自适应思考模型是否请求 interleaved thinking beta 头；默认 true。</summary>
    public bool? InterleavedThinking { get; init; }

    /// <summary>pi toolChoice 的 native 形状；C# 不做 SDK union。</summary>
    public JsonNode? ToolChoice { get; init; }
}
