using System;
using System.Collections.Generic;
using System.Linq;

namespace PiUnityAgent.Ai.Types;

/// <summary>Runtime validation for the invariants that C# unions cannot express.</summary>
public static class MessageValidation
{
    public static void Validate(Message message)
    {
        switch (message)
        {
            case SystemMessage system:
                RequireExactlyOne(system.Content, system.ContentBlocks, nameof(SystemMessage));
                RequireBlocks(system.ContentBlocks, nameof(SystemMessage), b => b is TextContent);
                break;

            case UserMessage user:
                RequireExactlyOne(user.Content, user.ContentBlocks, nameof(UserMessage));
                RequireBlocks(user.ContentBlocks, nameof(UserMessage), b => b is TextContent || b is ImageContent);
                break;

            case AssistantMessage assistant:
                RequireBlocks(assistant.Content, nameof(AssistantMessage),
                    b => b is TextContent || b is ThinkingContent || b is ToolCall);
                break;

            case ToolResultMessage result:
                if (string.IsNullOrEmpty(result.ToolCallId))
                {
                    throw new ArgumentException("ToolResultMessage.ToolCallId must reference a ToolCall.", nameof(message));
                }

                RequireBlocks(result.Content, nameof(ToolResultMessage),
                    b => b is TextContent || b is ImageContent);
                break;

            default:
                throw new ArgumentException($"Unknown message type: {message.GetType().Name}.", nameof(message));
        }
    }

    /// <summary>Ensures a tool result answers a tool call in the assistant message.</summary>
    public static void ValidateToolPair(AssistantMessage assistant, ToolResultMessage result)
    {
        if (!assistant.Content.OfType<ToolCall>().Any(call => call.Id == result.ToolCallId))
        {
            throw new InvalidOperationException(
                $"ToolResultMessage.ToolCallId '{result.ToolCallId}' does not match any ToolCall in the assistant message.");
        }
    }

    private static void RequireExactlyOne<T>(string? text, IReadOnlyList<T>? blocks, string owner)
    {
        var hasText = text is not null;
        var hasBlocks = blocks is not null;
        if (hasText == hasBlocks)
        {
            throw new ArgumentException($"{owner} must set exactly one of Content or ContentBlocks.");
        }
    }

    private static void RequireBlocks(
        IReadOnlyList<ContentBlock>? blocks,
        string owner,
        Func<ContentBlock, bool> allowed)
    {
        if (blocks is null)
        {
            return;
        }

        foreach (var block in blocks)
        {
            if (!allowed(block))
            {
                throw new ArgumentException($"{owner} contains unsupported content block: {block.Type}.");
            }
        }
    }
}
