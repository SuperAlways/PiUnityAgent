using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace PiUnityAgent.Ai.Api;

/// <summary>一条 SSE 事件（data 载荷 + 可选 event/id）。对应 pi ai 包内联的 SSE 解析。</summary>
internal sealed record SseEvent(string? Event, string Data, string? Id);

/// <summary>
/// SSE 流解析器（pi ai 包内联版本）：行协议语义为 field: value、空行分发、
/// 多行 data 用 \n 连接、注释行（以 ':' 开头）跳过；<c>data: [DONE]</c> 照常产出，
/// 由协议适配器自行判别。读异常（如流中途断开）直接向上传播给调用方。
/// 逐行读取，不缓冲整条响应。
/// </summary>
internal static class SseEventReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? @event = null;
        string? id = null;
        var dataLines = new List<string>();

        while (true)
        {
            // netstandard2.1 的 ReadLineAsync 无 CancellationToken 重载：在每个 await 之间检查令牌。
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null) break;

            if (line.Length == 0)
            {
                // 空行 = 分发；没有 data 的空事件（如只有 event 字段）不产出。
                if (dataLines.Count > 0)
                {
                    yield return new SseEvent(@event, string.Join("\n", dataLines), id);
                    dataLines.Clear();
                }
                @event = null;
                id = null;
                continue;
            }

            if (line[0] == ':') continue; // 注释/心跳行

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            if (field == "data") dataLines.Add(value);
            else if (field == "event") @event = value;
            else if (field == "id") id = value;
        }

        // 流结束时仍有未分发的事件：照 pi 语义补发。
        if (dataLines.Count > 0)
        {
            yield return new SseEvent(@event, string.Join("\n", dataLines), id);
        }
    }
}
