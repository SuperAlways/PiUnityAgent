using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class ValidationTests
{
    [Fact]
    public void AssistantMessage_Rejects_Image_Blocks()
    {
        var message = new AssistantMessage
        {
            Content = new ContentBlock[] { new ImageContent { Data = "AAAA", MimeType = "image/png" } },
            Api = "x", Provider = "p", Model = "m",
            Usage = new Usage(), StopReason = StopReason.Stop, Timestamp = 1,
        };

        Assert.Throws<ArgumentException>(() => MessageValidation.Validate(message));
    }

    [Fact]
    public void ToolPair_Requires_Matching_Call()
    {
        var assistant = new AssistantMessage
        {
            Content = new ContentBlock[] { new ToolCall { Id = "call_1", Name = "query_hp" } },
            Api = "x", Provider = "p", Model = "m",
            Usage = new Usage(), StopReason = StopReason.ToolUse, Timestamp = 1,
        };

        MessageValidation.ValidateToolPair(assistant, new ToolResultMessage
        {
            ToolCallId = "call_1", ToolName = "query_hp",
            Content = Array.Empty<ContentBlock>(), IsError = false, Timestamp = 2,
        });

        Assert.Throws<InvalidOperationException>(() => MessageValidation.ValidateToolPair(
            assistant,
            new ToolResultMessage
            {
                ToolCallId = "call_2", ToolName = "query_hp",
                Content = Array.Empty<ContentBlock>(), IsError = false, Timestamp = 2,
            }));
    }
}
