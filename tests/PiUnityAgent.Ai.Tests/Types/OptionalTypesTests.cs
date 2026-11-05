using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class OptionalTypesTests
{
    [Fact]
    public void Deferred_And_Diagnostics_Are_Present()
    {
        var message = new AssistantMessage
        {
            Timestamp = 0,
            Api = "anthropic-messages",
            Provider = "anthropic",
            Model = "claude-sonnet-4-5",
            Usage = new Usage(),
            StopReason = StopReason.Stop,
            Content = Array.Empty<ContentBlock>(),
            Deferred = new DeferredHandle
            {
                Provider = "openai",
                ModelId = "gpt-5",
                Api = "openai-responses",
                Id = "resp_1",
                PollAfterMs = 1000,
                Data = new JsonObject { ["kind"] = "batch" },
            },
            Diagnostics = new[]
            {
                new AssistantMessageDiagnostic
                {
                    Type = "retry",
                    Timestamp = 123,
                    Error = new DiagnosticErrorInfo
                    {
                        Name = "HttpRequestException",
                        Message = "timeout",
                        Code = "ETIMEDOUT",
                    },
                },
            },
        };

        Assert.Equal("resp_1", message.Deferred.Id);
        Assert.Equal(1000, message.Deferred.PollAfterMs);
        Assert.Single(message.Diagnostics);
        Assert.Equal("ETIMEDOUT", message.Diagnostics[0].Error!.Code!.GetValue<string>());
    }

    [Fact]
    public void NestedToolCalls_Are_Present()
    {
        var message = new ToolResultMessage
        {
            Timestamp = 0,
            ToolCallId = "call_1",
            ToolName = "codemode",
            Content = Array.Empty<ContentBlock>(),
            IsError = false,
            NestedCalls = new NestedToolCalls
            {
                Complete = false,
                Calls = new[]
                {
                    new NestedToolCallRecord
                    {
                        Id = "nested_1",
                        Name = "inner_tool",
                        Arguments = new JsonObject { ["x"] = 1 },
                        Status = NestedToolCallStatus.Ok,
                    },
                },
            },
        };

        Assert.NotNull(message.NestedCalls);
        Assert.False(message.NestedCalls.Complete);
        Assert.Single(message.NestedCalls.Calls);
        Assert.Equal(NestedToolCallStatus.Ok, message.NestedCalls.Calls[0].Status);
    }

    [Fact]
    public void Images_And_Classifier_Types_Are_Present()
    {
        var images = new ImagesContext
        {
            Input = new IImagesInputContent[]
            {
                new TextContent { Text = "draw" },
                new ImageContent { Data = "AAAA", MimeType = "image/png" },
            },
        };

        var generated = new AssistantImages
        {
            Api = "openrouter-images",
            Provider = "openrouter",
            Model = "image-model",
            Output = new IImagesOutputContent[] { new TextContent { Text = "done" } },
            StopReason = ImagesStopReason.Stop,
        };

        var classifierContext = new ClassifierContext
        {
            State = new JsonObject { ["score"] = 1 },
            Questions = new Dictionary<string, ClassifierQuestion>
            {
                ["is_spam"] = new ClassifierBoolQuestion
                {
                    Instructions = "Is this spam?",
                    Criteria = new ClassifierBoolCriteria { True = "spam", False = "ham" },
                },
            },
        };

        var classifierResult = new ClassifierResult
        {
            Api = "openai",
            Provider = "openai",
            Model = "classifier",
            Answers = new Dictionary<string, ClassifierAnswer>
            {
                ["is_spam"] = new ClassifierBoolAnswer { Probability = 0.8 },
            },
            StopReason = ClassifierStopReason.Stop,
        };

        Assert.Equal(2, images.Input.Count);
        Assert.Single(generated.Output);
        Assert.Single(classifierContext.Questions);
        Assert.Equal(0.8, ((ClassifierBoolAnswer)classifierResult.Answers["is_spam"]).Probability);
    }
}
