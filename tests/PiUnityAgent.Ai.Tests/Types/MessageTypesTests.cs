using System.Text.Json;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class MessageTypesTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void TextContent_Type_IsText()
    {
        var content = new TextContent { Text = "hello" };

        Assert.Equal("text", content.Type);
        Assert.Equal("hello", content.Text);
    }

    [Fact]
    public void AssistantMessage_Preserves_Content_Blocks()
    {
        var message = new AssistantMessage
        {
            Api = "anthropic-messages",
            Provider = "anthropic",
            Model = "claude-sonnet-4-5",
            StopReason = StopReason.ToolUse,
            Content = new ContentBlock[]
            {
                new TextContent { Text = "I will check." },
                new ThinkingContent { Thinking = "Need the HP value." },
                new ToolCall
                {
                    Id = "call_1",
                    Name = "query_hp",
                    Arguments = new JsonObject { ["target"] = "player" },
                },
            },
        };

        Assert.Equal("assistant", message.Role);
        Assert.Equal(3, message.Content.Count);
        Assert.Contains(message.Content, block => block is TextContent);
        Assert.Contains(message.Content, block => block is ThinkingContent);
        Assert.Contains(message.Content, block => block is ToolCall);
    }

    [Fact]
    public void ToolCall_Serializes_Arguments_And_ArgumentsError()
    {
        var call = new ToolCall
        {
            Id = "call_1",
            Name = "query_hp",
            Arguments = new JsonObject { ["target"] = "player" },
            ArgumentsError = "trailing comma",
        };

        var json = JsonSerializer.Serialize<ContentBlock>(call, JsonOptions);

        Assert.Contains("\"type\":\"toolCall\"", json);
        Assert.Contains("\"name\":\"query_hp\"", json);
        Assert.Contains("\"argumentsError\":\"trailing comma\"", json);
    }

    [Fact]
    public void StopReason_Wire_RoundTrips()
    {
        foreach (StopReason reason in Enum.GetValues(typeof(StopReason)))
        {
            Assert.Equal(reason, StopReasonExtensions.FromWire(reason.ToWire()));
        }
    }

    [Fact]
    public void Usage_Preserves_Totals_And_Cost()
    {
        var usage = new Usage
        {
            Input = 10,
            Output = 20,
            CacheRead = 5,
            CacheWrite = 3,
            Reasoning = 7,
            TotalTokens = 38,
            Cost = new UsageCost { Total = 0.012, TotalFromProvider = true },
        };

        Assert.Equal(38, usage.TotalTokens);
        Assert.Equal(0.012, usage.Cost.Total);
        Assert.True(usage.Cost.TotalFromProvider);
        Assert.Equal(7, usage.Reasoning);
    }

    [Fact]
    public void SystemMessage_Requires_Exactly_One_Content_Form()
    {
        MessageValidation.Validate(new SystemMessage { Content = "base prompt" });
        MessageValidation.Validate(new SystemMessage
        {
            ContentBlocks = new[] { new TextContent { Text = "base prompt" } },
        });

        Assert.Throws<ArgumentException>(() => MessageValidation.Validate(new SystemMessage()));
        Assert.Throws<ArgumentException>(() => MessageValidation.Validate(new SystemMessage
        {
            Content = "text",
            ContentBlocks = new[] { new TextContent { Text = "blocks" } },
        }));
    }

    [Fact]
    public void UserMessage_Allows_Text_And_Image_But_Not_ToolCall()
    {
        MessageValidation.Validate(new UserMessage
        {
            ContentBlocks = new ContentBlock[]
            {
                new TextContent { Text = "look" },
                new ImageContent { Data = "AAAA", MimeType = "image/png" },
            },
        });

        Assert.Throws<ArgumentException>(() => MessageValidation.Validate(new UserMessage
        {
            ContentBlocks = new ContentBlock[] { new ToolCall { Id = "c", Name = "x" } },
        }));
    }

    [Fact]
    public void AssistantMessage_Rejects_Image_Blocks()
    {
        Assert.Throws<ArgumentException>(() => MessageValidation.Validate(new AssistantMessage
        {
            Content = new ContentBlock[] { new ImageContent { Data = "AAAA", MimeType = "image/png" } },
        }));
    }

    [Fact]
    public void ToolResult_Requires_ToolCallId_And_Only_Text_Or_Image()
    {
        MessageValidation.Validate(new ToolResultMessage
        {
            ToolCallId = "call_1",
            ToolName = "query_hp",
            Content = new ContentBlock[] { new TextContent { Text = "hp=80" } },
        });

        Assert.Throws<ArgumentException>(() => MessageValidation.Validate(new ToolResultMessage
        {
            ToolName = "query_hp",
        }));

        Assert.Throws<ArgumentException>(() => MessageValidation.Validate(new ToolResultMessage
        {
            ToolCallId = "call_1",
            ToolName = "query_hp",
            Content = new ContentBlock[] { new ThinkingContent { Thinking = "nope" } },
        }));
    }

    [Fact]
    public void ValidateToolPair_Requires_Matching_Call()
    {
        var assistant = new AssistantMessage
        {
            Content = new ContentBlock[] { new ToolCall { Id = "call_1", Name = "query_hp" } },
        };

        MessageValidation.ValidateToolPair(assistant, new ToolResultMessage
        {
            ToolCallId = "call_1",
            ToolName = "query_hp",
        });

        Assert.Throws<InvalidOperationException>(() => MessageValidation.ValidateToolPair(
            assistant,
            new ToolResultMessage { ToolCallId = "call_2", ToolName = "query_hp" }));
    }

    [Fact]
    public void Context_Defaults_Are_Empty()
    {
        var context = new Context();

        Assert.Empty(context.Messages);
        Assert.Null(context.Tools);
        Assert.Null(context.SystemPrompt);
    }
}
