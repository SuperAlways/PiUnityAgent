using System.Text.Json;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Utils;

namespace PiUnityAgent.Ai.Api;

/// <summary>
/// 一条流式 tool call 的参数累积器（pi <c>ToolCallArgumentsStream</c>）：
/// delta 期间暴露可解析的部分 JSON 快照；终点用一次 repair 解析给出终值/argumentsError。
/// 原始文本不进入 partial 消息或 transcript。
/// </summary>
internal sealed class ToolCallArguments
{
    private readonly ToolCallArgumentsStream _stream = new();

    public string Text => _stream.Text;

    /// <summary>追加一段参数文本；返回 partial 参数（对象形态；非对象快照显示为 {}）。</summary>
    public JsonObject Append(string delta) => ToObject(_stream.Append(delta));

    /// <summary>
    /// 接受 provider 的完整参数文本（pi <c>ToolCallArgumentsStream.replace</c>，utils/streaming-json.ts）：
    /// 文本接续已累积文本时返回缺失片段，否则重置重新累积（delta 为 null）。两种情形都返回 partial 参数。
    /// </summary>
    public (string? Delta, JsonObject Arguments) Replace(string text)
    {
        var (delta, arguments) = _stream.Replace(text);
        return (delta, ToObject(arguments));
    }

    /// <summary>终点：parseJsonWithRepair 语义，返回参数或 argumentsError。</summary>
    public (JsonObject Arguments, string? ArgumentsError) Finish() => _stream.Finish();

    /// <summary>
    /// 解析器产出的 partial 值图（null/bool/string/double/List/Dictionary）整体转成 JsonObject；
    /// 非对象快照（不该出现在工具参数上）显示为 {}。每次都新建节点：块上换引用即可让
    /// 之前的 partial 消息快照保持不变。
    /// </summary>
    private static JsonObject ToObject(IReadOnlyDictionary<string, object?> partial)
        => JsonSerializer.SerializeToNode(partial) as JsonObject ?? new JsonObject();
}
