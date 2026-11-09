using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Tools;

/// <summary>
/// 工具执行结果（pi <c>AgentToolResult</c>）：content 给模型、details 给 UI、structuredContent 给程序化调用方。
/// </summary>
public sealed record AgentToolResult
{
    /// <summary>给模型的内容；契约上只放 Text/Image，生成 ToolResultMessage 时由 S7 校验。</summary>
    public IReadOnlyList<ContentBlock> Content { get; init; } = Array.Empty<ContentBlock>();

    /// <summary>给 UI/日志的结构化细节。对应 pi details。</summary>
    public JsonNode? Details { get; init; }

    /// <summary>给程序化调用方的机器可读结果（不进模型）。对应 pi structuredContent。</summary>
    public JsonNode? StructuredContent { get; init; }

    /// <summary>本次工具执行自身的 usage。对应 pi usage。</summary>
    public Usage? Usage { get; init; }

    /// <summary>失败标记；模型看到错误 content，details/structuredContent 仍保留。对应 pi isError。</summary>
    public bool IsError { get; init; }

    /// <summary>批次提前终止提示（全批都为 true 才生效，归 S7）。对应 pi terminate。</summary>
    public bool Terminate { get; init; }

    /// <summary>pi createErrorToolResult 等价：文本错误 + details={} + isError。</summary>
    public static AgentToolResult FromError(string message, JsonNode? details = null)
        => new()
        {
            Content = new ContentBlock[] { new TextContent { Text = message } },
            Details = details ?? new JsonObject(),
            IsError = true,
        };
}
