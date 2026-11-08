using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Api;

/// <summary>
/// 一条流式 tool call 的参数累积器（pi <c>ToolCallArgumentsStream</c> 的最小端口）。
/// 保存原始文本，不解析中间片段——未完成 JSON 绝不暴露给 partial 事件；
/// 完整文本只在这条 tool call 结束时用 <see cref="Finish"/> 解析一次。
/// </summary>
internal sealed class ToolCallArguments
{
    private readonly StringBuilder _text = new();

    /// <summary>追加一段参数文本（function.arguments 增量）。</summary>
    public void Append(string delta) => _text.Append(delta);

    /// <summary>累积到的原始文本。</summary>
    public string Text => _text.ToString();

    /// <summary>
    /// 解析完整参数文本。<c>JsonNode.Parse</c> 失败或结果不是 JSON object 时抛
    /// <see cref="JsonException"/>，由协议适配器转为终态 Error（pi 对应 argumentsError）。
    /// </summary>
    public JsonObject Finish()
    {
        var json = _text.ToString();
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            // STJ 抛的是 JsonReaderException（子类）；统一成 JsonException 让适配器按一类接。
            throw new JsonException($"Invalid tool call arguments: {error.Message}", error);
        }

        if (parsed is not JsonObject obj)
        {
            throw new JsonException("Tool arguments must be a JSON object");
        }

        return obj;
    }
}
