using System;
using System.Collections.Generic;
using System.Linq;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// Agent 自身所需的 transcript 重放子集（初始 system 消息 + 当前 system 消息）。
/// 公开 getCurrentTools/collapseSystemMessages 仍是 D11/S9；本类 internal，S9 提升。
/// </summary>
internal static class AgentTranscript
{
    public static SystemMessage? CreateInitialSystemMessage(
        string? systemPrompt,
        IReadOnlyList<AgentTool> tools)
    {
        var hasPrompt = !string.IsNullOrEmpty(systemPrompt);
        var hasTools = tools.Count > 0;
        if (!hasPrompt && !hasTools) return null;

        return new SystemMessage
        {
            Content = MessageContent.From(systemPrompt ?? string.Empty),
            Timestamp = 0,
            ToolsAdded = hasTools
                ? tools.Select(ToolLoadout.ToToolDeclaration).ToList()
                : null,
        };
    }

    public static SystemMessage? GetCurrentSystemMessage(IReadOnlyList<Message> messages)
    {
        var content = new List<string>();
        var sections = new Dictionary<string, string?>(StringComparer.Ordinal);
        long? timestamp = null;

        foreach (var message in messages)
        {
            if (message is not SystemMessage system) continue;
            timestamp ??= system.Timestamp;

            var text = ContentText(system.Content);
            if (text.Length > 0) content.Add(text);

            if (system.Sections is not null)
            {
                foreach (var pair in system.Sections)
                {
                    if (pair.Value is null) sections.Remove(pair.Key);
                    else sections[pair.Key] = pair.Value;
                }
            }
        }

        var tools = ToolLoadout.GetCurrentTools(messages);
        if (timestamp is null && tools.Count == 0) return null;

        return new SystemMessage
        {
            Content = MessageContent.From(string.Join("\n\n", content)),
            Timestamp = timestamp ?? 0,
            Sections = sections.Count > 0 ? sections : null,
            ToolsAdded = tools.Count > 0 ? tools : null,
        };
    }

    public static string GetCurrentSystemPrompt(IReadOnlyList<Message> messages)
    {
        var message = GetCurrentSystemMessage(messages);
        if (message is null) return string.Empty;

        var parts = new List<string> { ContentText(message.Content) };
        if (message.Sections is not null)
        {
            foreach (var value in message.Sections.Values)
            {
                if (value is not null) parts.Add(value);
            }
        }

        return string.Join("\n\n", parts.Where(part => part.Length > 0));
    }

    /// <summary>pi contentText：仅 text 块，以 "\n" 连接。</summary>
    private static string ContentText(MessageContent content)
        => string.Join("\n", content.ToBlocks().OfType<TextContent>().Select(text => text.Text));
}
