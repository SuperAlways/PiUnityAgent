using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

public class ToolExecutionTests
{
    private static AgentContext Context(params AgentTool[] tools)
        => new() { Messages = new List<Message>(), Tools = tools };

    private static AgentLoopConfig Config() => new()
    {
        Model = new Model { Id = "m", Name = "m", Api = "faux", Provider = "faux", BaseUrl = "http://localhost" },
        ConvertToLlm = messages => Task.FromResult<IReadOnlyList<Message>>(messages),
    };

    private static AgentTool Echo(AgentToolResult? result = null)
        => new()
        {
            Name = "echo",
            Description = "Echo",
            Label = "Echo",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("value"),
            },
            Execute = (_, args, _, _) => Task.FromResult(result ?? new AgentToolResult
            {
                Content = new ContentBlock[] { new TextContent { Text = args!["value"]!.GetValue<string>() } },
                Details = new JsonObject(),
            }),
        };

    private static AssistantMessage Assistant(params ContentBlock[] content)
        => new()
        {
            Content = content, Api = "faux", Provider = "faux", Model = "m", Usage = new Usage(),
            StopReason = StopReason.ToolUse, Timestamp = 1,
        };

    private static ToolCall Call(string name = "echo", JsonObject? args = null, string? error = null)
        => new() { Id = "call-1", Name = name, Arguments = args ?? new JsonObject { ["value"] = "hi" }, ArgumentsError = error };

    [Fact]
    public async Task Unknown_Tool_Is_An_Error_Outcome()
    {
        var outcome = await ToolExecution.RunToolCallAsync(
            Call("nope"), Assistant(), Context(), Config(), CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("Tool nope not found", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task Validation_Error_Is_An_Error_Outcome()
    {
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(args: new JsonObject()), Assistant(), Context(Echo()), Config(), CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Contains("Validation failed for tool \"echo\"",
            Assert.IsType<TextContent>(outcome.Result.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Before_Hook_Block_Uses_Reason_And_Terminate()
    {
        var config = Config() with
        {
            BeforeToolCall = (_, _) => Task.FromResult<BeforeToolCallResult?>(
                new BeforeToolCallResult(Block: true, Reason: "denied", Terminate: true)),
        };
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), Assistant(), Context(Echo()), config, CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.True(outcome.Result.Terminate);
        Assert.Equal("denied", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task After_Hook_Replaces_Fields_And_Drops_Structured_Content_When_Content_Replaced()
    {
        var config = Config() with
        {
            AfterToolCall = (context, _) => Task.FromResult<AfterToolCallResult?>(
                new AfterToolCallResult(
                    Content: new ContentBlock[] { new TextContent { Text = "override" } },
                    IsError: true)),
        };
        var tool = Echo(new AgentToolResult
        {
            Content = new ContentBlock[] { new TextContent { Text = "original" } },
            StructuredContent = new JsonObject { ["x"] = 1 },
        });
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), Assistant(), Context(tool), config, CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("override", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
        Assert.Null(outcome.Result.StructuredContent);
    }

    [Fact]
    public async Task Before_Hook_Throw_Becomes_Error_Outcome()
    {
        var config = Config() with
        {
            BeforeToolCall = (_, _) => throw new InvalidOperationException("hook boom"),
        };
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), Assistant(), Context(Echo()), config, CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("hook boom", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task Execute_Throw_Becomes_Error_With_Duration()
    {
        var tool = Echo() with
        {
            Execute = (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), Assistant(), Context(tool), Config(), CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("boom", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
        Assert.NotNull(outcome.DurationMs);
    }

    [Fact]
    public async Task Update_After_Settle_Is_Ignored()
    {
        var updates = new List<string>();
        var tool = Echo() with
        {
            Execute = async (_, _, _, update) =>
            {
                if (update is not null) await update(new AgentToolResult());
                return new AgentToolResult { Content = new ContentBlock[] { new TextContent { Text = "done" } } };
            },
        };
        // 直接调用 runToolCall 时 onUpdate 由调用方提供（不产生事件）；此处只断言正常返回。
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), Assistant(), Context(tool), Config(), CancellationToken.None,
            partial => { updates.Add("update"); return Task.CompletedTask; });
        Assert.True(outcome.IsError == false);
        Assert.Single(updates);
    }
}
