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
            Call("nope"), Array.Empty<AgentTool>(), Assistant(), Context(), Config(), CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("Tool nope not found", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task Tools_Are_Caller_Provided_Independent_Of_Context_Tools()
    {
        // pi RunToolCallOptions.tools 独立必填：解析针对调用方传入的 tools，而非 context.Tools
        // （2026-11-09 审计 U-1）。context.Tools 为空也能执行调用方给出的工具。
        var tool = Echo();
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), new[] { tool }, Assistant(), Context(), Config(), CancellationToken.None);
        Assert.True(outcome.IsError == false);
        Assert.Equal("hi", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task Validation_Error_Is_An_Error_Outcome()
    {
        var tool = Echo();
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(args: new JsonObject()), new[] { tool }, Assistant(), Context(tool), Config(), CancellationToken.None);
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
        var tool = Echo();
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), new[] { tool }, Assistant(), Context(tool), config, CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.True(outcome.Result.Terminate);
        Assert.Equal("denied", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task Before_Hook_Block_Empty_Reason_Falls_Back_To_Default()
    {
        // pi `beforeResult.reason || "Tool execution was blocked"`：空串同 null 回退默认文案。
        var config = Config() with
        {
            BeforeToolCall = (_, _) => Task.FromResult<BeforeToolCallResult?>(
                new BeforeToolCallResult(Block: true, Reason: "", Terminate: false)),
        };
        var tool = Echo();
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), new[] { tool }, Assistant(), Context(tool), config, CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("Tool execution was blocked", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
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
            Call(), new[] { tool }, Assistant(), Context(tool), config, CancellationToken.None);
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
        var tool = Echo();
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), new[] { tool }, Assistant(), Context(tool), config, CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("hook boom", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task Before_Hook_Throwing_OCE_Becomes_Error_Outcome_Without_Throwing()
    {
        // pi catch-all：钩子抛出的取消（OCE）同普通钩子异常落错误结果，不沿管线抛出（审计 U-2）。
        var config = Config() with
        {
            BeforeToolCall = (_, _) => throw new OperationCanceledException("hook canceled"),
        };
        var tool = Echo();
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), new[] { tool }, Assistant(), Context(tool), config, CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("hook canceled", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task Execute_Throw_Becomes_Error_With_Duration()
    {
        var tool = Echo() with
        {
            Execute = (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), new[] { tool }, Assistant(), Context(tool), Config(), CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("boom", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
        Assert.NotNull(outcome.DurationMs);
    }

    [Fact]
    public async Task Execute_Throwing_OCE_Becomes_Error_Outcome_Without_Throwing()
    {
        // pi executePreparedToolCall catch-all：遵守 CT 的工具抛 OCE 也落错误结果（含耗时），
        // 不让取消把整次运行打成流失败（审计 U-2）。
        var tool = Echo() with
        {
            Execute = (_, _, _, _) => throw new OperationCanceledException("canceled"),
        };
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), new[] { tool }, Assistant(), Context(tool), Config(), CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Contains("canceled", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text, StringComparison.Ordinal);
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
            Call(), new[] { tool }, Assistant(), Context(tool), Config(), CancellationToken.None,
            partial => { updates.Add("update"); return Task.CompletedTask; });
        Assert.True(outcome.IsError == false);
        Assert.Single(updates);
    }
}
