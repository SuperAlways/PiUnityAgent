using System;

namespace PiUnityAgent.Ai.Types;

public static class TranscriptNormalization
{
    // TODO(S9, D11): getCurrentSystemMessage / getCurrentTools / collapseSystemMessages（台账 D11）。
    /// <summary>
    /// 把 Context.systemPrompt 与 Context.tools 折成前导 SystemMessage；
    /// 两者都为空时不插入 system 消息。原消息顺序不变。
    /// </summary>
    public static TranscriptContext NormalizeContext(Context context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        var hasPrompt = !string.IsNullOrEmpty(context.SystemPrompt);
        var hasTools = context.Tools is { Count: > 0 };
        if (!hasPrompt && !hasTools)
        {
            return new TranscriptContext { Messages = context.Messages };
        }

        var system = new SystemMessage
        {
            Timestamp = 0,
            Content = MessageContent.From(context.SystemPrompt ?? string.Empty),
            ToolsAdded = hasTools ? context.Tools : null,
        };

        var messages = new Message[context.Messages.Count + 1];
        messages[0] = system;
        for (var index = 0; index < context.Messages.Count; index++)
        {
            messages[index + 1] = context.Messages[index];
        }

        return new TranscriptContext { Messages = messages };
    }
}
