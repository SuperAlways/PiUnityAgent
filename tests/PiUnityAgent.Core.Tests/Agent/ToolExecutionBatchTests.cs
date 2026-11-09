using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

/// <summary>
/// 批次管线直测（internal ExecuteToolCalls / FailToolCallsFromTruncatedMessage 经 IVT）：
/// 终止聚合、完成序/源顺序、预检内联 end、顺序/并行取消、截断批、settle 守卫。
/// </summary>
public class ToolExecutionBatchTests
{
    private static AgentContext Context(params AgentTool[] tools)
        => new() { Messages = new List<Message>(), Tools = tools };

    private static AgentLoopConfig Config(ToolExecutionMode mode = ToolExecutionMode.Parallel) => new()
    {
        Model = new Model { Id = "m", Name = "m", Api = "faux", Provider = "faux", BaseUrl = "http://localhost" },
        ConvertToLlm = messages => Task.FromResult<IReadOnlyList<Message>>(messages),
        ToolExecution = mode,
    };

    private static AssistantMessage Assistant(params ContentBlock[] content)
        => new()
        {
            Content = content, Api = "faux", Provider = "faux", Model = "m", Usage = new Usage(),
            StopReason = StopReason.ToolUse, Timestamp = 1,
        };

    private static ToolCall Call(string name, string id)
        => new() { Id = id, Name = name, Arguments = new JsonObject() };

    private static AgentTool Tool(string name, Func<string, Task<AgentToolResult>> execute)
        => new()
        {
            Name = name, Description = name, Label = name,
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = (_, _, _, _) => execute(name),
        };

    private static (List<AgentEvent> Events, AgentEventSink Sink) Recorder(Func<AgentEvent, Task>? onEvent = null)
    {
        var events = new List<AgentEvent>();
        return (events, async @event =>
        {
            events.Add(@event);
            if (onEvent is not null) await onEvent(@event);
        });
    }

    private static int IndexOfEndEvent(List<AgentEvent> events, string toolName)
        => events.FindIndex(e => e is AgentEvent.ToolExecutionEnd end && end.ToolName == toolName);

    private static int IndexOfStartEvent(List<AgentEvent> events, string toolName)
        => events.FindIndex(e => e is AgentEvent.ToolExecutionStart start && start.ToolName == toolName);

    private static string FirstText(ToolResultMessage message)
        => Assert.IsType<TextContent>(message.Content[0]).Text;

    // ---------- 终止聚合 ----------

    [Fact]
    public async Task Terminate_Requires_Every_Result_In_The_Batch()
    {
        var (events, sink) = Recorder();
        var terminating = Tool("t", _ => Task.FromResult(new AgentToolResult { Terminate = true }));
        var normal = Tool("n", _ => Task.FromResult(new AgentToolResult()));

        var all = await ToolExecution.ExecuteToolCalls(
            Context(terminating), Assistant(Call("t", "1"), Call("t", "2")),
            Config(ToolExecutionMode.Sequential), CancellationToken.None, sink);
        Assert.True(all.Terminate);
        Assert.Equal(2, all.Messages.Count);

        var mixed = await ToolExecution.ExecuteToolCalls(
            Context(terminating, normal), Assistant(Call("t", "1"), Call("n", "2")),
            Config(ToolExecutionMode.Sequential), CancellationToken.None, sink);
        Assert.False(mixed.Terminate);
    }

    // ---------- 完成序 / 源顺序 ----------

    [Fact]
    public async Task Parallel_End_Events_Follow_Completion_Order_And_Messages_Follow_Source_Order()
    {
        // a 先排队后放行、b 先放行：end 事件按完成序（b→a），消息按源顺序（a→b）。
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (events, sink) = Recorder(@event =>
        {
            if (@event is AgentEvent.ToolExecutionStart { ToolName: "b" }) gateB.TrySetResult();
            if (@event is AgentEvent.ToolExecutionEnd { ToolName: "b" }) gateA.TrySetResult();
            return Task.CompletedTask;
        });
        var toolA = Tool("a", async _ => { await gateA.Task; return new AgentToolResult(); });
        var toolB = Tool("b", async _ => { await gateB.Task; return new AgentToolResult(); });

        var batch = await ToolExecution.ExecuteToolCalls(
            Context(toolA, toolB), Assistant(Call("a", "1"), Call("b", "2")),
            Config(), CancellationToken.None, sink);

        var endOrder = events.OfType<AgentEvent.ToolExecutionEnd>().Select(e => e.ToolName).ToList();
        Assert.Equal(new[] { "b", "a" }, endOrder);
        Assert.Equal(new[] { "a", "b" }, batch.Messages.Select(m => m.ToolName).ToList());
    }

    [Fact]
    public async Task Unknown_Call_End_Is_Emitted_Inline_During_Preflight_Before_Ready_Executes()
    {
        var (events, sink) = Recorder();
        var executions = new List<string>();
        var echo = Tool("echo", name => { executions.Add(name); return Task.FromResult(new AgentToolResult()); });

        var batch = await ToolExecution.ExecuteToolCalls(
            Context(echo), Assistant(Call("nope", "1"), Call("echo", "2")),
            Config(), CancellationToken.None, sink);

        // 事件序：start(nope) → end(nope)（预检内联）→ start(echo) → …
        var endNope = IndexOfEndEvent(events, "nope");
        var startEcho = IndexOfStartEvent(events, "echo");
        Assert.True(endNope >= 0);
        Assert.True(startEcho > endNope);
        Assert.Equal(new[] { "echo" }, executions);
        Assert.Equal(new[] { "nope", "echo" }, batch.Messages.Select(m => m.ToolName).ToList());
        Assert.True(batch.Messages[0].IsError);
        Assert.Equal("Tool nope not found", FirstText(batch.Messages[0]));
    }

    // ---------- 取消 ----------

    [Fact]
    public async Task Sequential_Abort_Stops_Subsequent_Calls()
    {
        var (events, sink) = Recorder();
        var cts = new CancellationTokenSource();
        var executions = new List<string>();
        var a = Tool("a", name => { executions.Add(name); cts.Cancel(); return Task.FromResult(new AgentToolResult()); });
        var b = Tool("b", name => { executions.Add(name); return Task.FromResult(new AgentToolResult()); });

        var batch = await ToolExecution.ExecuteToolCalls(
            Context(a, b), Assistant(Call("a", "1"), Call("b", "2")),
            Config(ToolExecutionMode.Sequential), cts.Token, sink);

        Assert.Equal(new[] { "a" }, executions);
        Assert.Single(batch.Messages);
        Assert.Equal("a", batch.Messages[0].ToolName);
        Assert.Equal(0, events.Count(e => e is AgentEvent.ToolExecutionStart { ToolName: "b" }));
    }

    [Fact]
    public async Task PreCancelled_Token_Produces_Operation_Aborted_Without_Executing()
    {
        var (events, sink) = Recorder();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var executions = new List<string>();
        var a = Tool("a", name => { executions.Add(name); return Task.FromResult(new AgentToolResult()); });

        var batch = await ToolExecution.ExecuteToolCalls(
            Context(a), Assistant(Call("a", "1")), Config(), cts.Token, sink);

        Assert.Empty(executions);
        Assert.Single(batch.Messages);
        Assert.True(batch.Messages[0].IsError);
        Assert.Equal("Operation aborted", FirstText(batch.Messages[0]));
    }

    [Fact]
    public async Task Parallel_Thunk_Aborts_At_Entry_When_Cancelled_During_Preflight()
    {
        // 预检 b 的钩子取消令牌：b 在 prepare 的取消探测即被中止（不进 thunk），
        // a 的 thunk 入口探测中止——两者都不执行。
        var (events, sink) = Recorder();
        var cts = new CancellationTokenSource();
        var executions = new List<string>();
        var a = Tool("a", name => { executions.Add(name); return Task.FromResult(new AgentToolResult()); });
        var b = Tool("b", name => { executions.Add(name); return Task.FromResult(new AgentToolResult()); });
        var config = Config() with
        {
            BeforeToolCall = (context, _) =>
            {
                if (context.ToolCall.Name == "b") cts.Cancel();
                return Task.FromResult<BeforeToolCallResult?>(null);
            },
        };

        var batch = await ToolExecution.ExecuteToolCalls(
            Context(a, b), Assistant(Call("a", "1"), Call("b", "2")),
            config, cts.Token, sink);

        Assert.Empty(executions);
        Assert.Equal(2, batch.Messages.Count);
        Assert.All(batch.Messages, m =>
        {
            Assert.True(m.IsError);
            Assert.Equal("Operation aborted", FirstText(m));
        });
        Assert.Equal(0, events.Count(e => e is AgentEvent.ToolExecutionUpdate));
    }

    // ---------- 截断批 ----------

    [Fact]
    public async Task Truncated_Batch_Fails_Every_Call_With_Exact_Text()
    {
        var (events, sink) = Recorder();

        var batch = await ToolExecution.FailToolCallsFromTruncatedMessage(
            new[] { Call("a", "1"), Call("b", "2") }, sink);

        Assert.False(batch.Terminate);
        Assert.Equal(2, batch.Messages.Count);
        foreach (var (message, name) in batch.Messages.Zip(new[] { "a", "b" }))
        {
            Assert.True(message.IsError);
            Assert.Equal(
                $"Tool call \"{name}\" was not executed: the response hit the output token limit, "
                + "so its arguments may be truncated. Re-issue the tool call with complete arguments.",
                FirstText(message));
        }
        // 每调用：start → end → message_start → message_end。
        Assert.Equal(8, events.Count);
        Assert.Equal(2, events.Count(e => e is AgentEvent.ToolExecutionEnd end && end.IsError));
    }

    // ---------- settle 守卫 ----------

    [Fact]
    public async Task Batch_Drops_Updates_Arriving_After_Settle()
    {
        AgentToolUpdateCallback? captured = null;
        var echo = new AgentTool
        {
            Name = "echo", Description = "echo", Label = "echo",
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = async (_, _, _, update) =>
            {
                captured = update;
                if (update is not null) await update(new AgentToolResult()); // settle 前的一次：应送达
                return new AgentToolResult();
            },
        };
        var (events, sink) = Recorder();

        await ToolExecution.ExecuteToolCalls(
            Context(echo), Assistant(Call("echo", "1")), Config(), CancellationToken.None, sink);

        Assert.NotNull(captured);
        Assert.Equal(1, events.Count(e => e is AgentEvent.ToolExecutionUpdate));
        await captured!(new AgentToolResult()); // settle 后的迟到更新：应被丢弃
        Assert.Equal(1, events.Count(e => e is AgentEvent.ToolExecutionUpdate));
    }
}
