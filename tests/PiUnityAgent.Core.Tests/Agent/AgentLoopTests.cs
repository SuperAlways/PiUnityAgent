using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

/// <summary>
/// agent 循环端到端行为测试：以 Faux provider 脚本驱动完整循环，验证事件序列、
/// 工具执行、钩子与终止语义（对应 pi.net tests/Pi.Agent.Tests/AgentLoopTests.cs 移植）。
/// </summary>
public class AgentLoopTests
{
    private static FauxProvider FauxProviderWith(params FauxResponseStep[] steps)
    {
        var faux = new FauxProvider();
        faux.SetResponses(steps);
        return faux;
    }

    private static AgentLoopConfig Config(Model model, ToolExecutionMode mode = ToolExecutionMode.Parallel)
        => new()
        {
            Model = model,
            ToolExecution = mode,
            ConvertToLlm = messages => Task.FromResult<IReadOnlyList<Message>>(messages),
            StreamOptions = new SimpleStreamOptions(),
        };

    private static Message UserPrompt(string text, long timestamp = 1)
        => new UserMessage { Timestamp = timestamp, Content = MessageContent.From(text) };

    private static AgentTool EchoTool(string name = "echo")
        => new()
        {
            Name = name,
            Description = "Echo",
            Label = "Echo",
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = (_, args, _, _) => Task.FromResult(new AgentToolResult
            {
                Content = new ContentBlock[] { new TextContent { Text = "echo:" + (args?.ToJsonString() ?? "{}") } },
            }),
        };

    private static async Task<List<AgentEvent>> CollectAsync(EventStream<AgentEvent, IReadOnlyList<Message>> stream)
    {
        var events = new List<AgentEvent>();
        await foreach (var @event in stream) events.Add(@event);
        return events;
    }

    private static string FirstText(IReadOnlyList<ContentBlock> content)
        => Assert.IsType<TextContent>(content[0]).Text;

    private static ToolResultMessage ResultAt(IReadOnlyList<Message> messages, int index)
        => Assert.IsType<ToolResultMessage>(messages[index]);

    // ---------- 事件序列 / 终值 ----------

    [Fact]
    public async Task Simple_Text_Turn_Emits_Full_Event_Sequence()
    {
        var faux = FauxProviderWith(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("你好"))));
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("hi") },
            new AgentContext { Messages = new List<Message>() },
            Config(faux.GetModel()),
            CancellationToken.None,
            faux.Provider.Stream);
        var events = await CollectAsync(stream);

        Assert.IsType<AgentEvent.AgentStart>(events[0]);
        Assert.IsType<AgentEvent.TurnStart>(events[1]);
        Assert.IsType<AgentEvent.MessageStart>(events[2]);
        Assert.Contains(events, e => e is AgentEvent.MessageUpdate);
        Assert.IsType<AgentEvent.MessageEnd>(events[^3]);
        Assert.IsType<AgentEvent.TurnEnd>(events[^2]);
        Assert.IsType<AgentEvent.AgentEnd>(events[^1]);

        var result = await stream.WaitForResultAsync();
        Assert.Equal(2, result.Count);
        Assert.IsType<UserMessage>(result[0]);
        Assert.Equal("你好", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[1]).Content[0]).Text);
    }

    // ---------- 工具循环 ----------

    [Fact]
    public async Task Tool_Call_Turn_Executes_Tool_And_Continues()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { Faux.ToolCall("echo", new JsonObject { ["value"] = "hi" }) },
                StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("done"))));
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("use the tool") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { EchoTool() } },
            Config(faux.GetModel()),
            CancellationToken.None,
            faux.Provider.Stream);
        var events = await CollectAsync(stream);
        var result = await stream.WaitForResultAsync();

        Assert.Contains(events, e => e is AgentEvent.ToolExecutionStart);
        Assert.Contains(events, e => e is AgentEvent.ToolExecutionEnd { IsError: false });
        Assert.Contains(events, e => e is AgentEvent.TurnEnd { ToolResults.Count: 1 });

        // pi 语义：装载公告在 prompt 之前插入声明 system 消息（简报原稿 result[2] 按此订正为 result[3]）。
        Assert.Equal(5, result.Count);
        Assert.IsType<SystemMessage>(result[0]);
        Assert.IsType<UserMessage>(result[1]);
        var toolResult = ResultAt(result, 3);
        Assert.False(toolResult.IsError);
        Assert.Equal("done", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[^1]).Content[0]).Text);
        Assert.Equal(2, faux.State.CallCount);
    }

    [Fact]
    public async Task ToolCallTurnRecordsExecutionDuration()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { Faux.ToolCall("echo", new JsonObject { ["value"] = "x" }, "call-1") },
                StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("done"))));
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("use the tool") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { EchoTool() } },
            Config(faux.GetModel(), ToolExecutionMode.Sequential),
            CancellationToken.None,
            faux.Provider.Stream);
        var events = await CollectAsync(stream);
        var result = await stream.WaitForResultAsync();

        // execute() 的耗时（单调时钟，毫秒）落到 tool_execution_end 事件与 toolResult 消息上，
        // 两处取同一个值。
        var endEvent = events.OfType<AgentEvent.ToolExecutionEnd>().Single();
        Assert.NotNull(endEvent.DurationMs);
        Assert.True(endEvent.DurationMs >= 0, $"durationMs 应非负，实际 {endEvent.DurationMs}");

        var toolResult = ResultAt(result, 3);
        Assert.Equal(endEvent.DurationMs, toolResult.DurationMs);
    }

    [Fact]
    public async Task BlockedToolCallProducesErrorResult()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { Faux.ToolCall("echo", new JsonObject(), "call-1") },
                StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("ok"))));
        var config = Config(faux.GetModel()) with
        {
            BeforeToolCall = (_, _) => Task.FromResult<BeforeToolCallResult?>(
                new BeforeToolCallResult(Block: true, Reason: "不允许执行")),
        };
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("go") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { EchoTool() } },
            config,
            CancellationToken.None,
            faux.Provider.Stream);
        var events = await CollectAsync(stream);
        var result = await stream.WaitForResultAsync();

        // user + system(声明) + assistant(toolUse) + toolResult(错误) + assistant(ok)。
        Assert.Equal(5, result.Count);
        var toolResult = ResultAt(result, 3);
        Assert.True(toolResult.IsError);
        Assert.Contains("不允许执行", FirstText(toolResult.Content));
        var endEvent = events.OfType<AgentEvent.ToolExecutionEnd>().Single();
        Assert.True(endEvent.IsError);
        // 被阻断的调用没有执行过，故没有耗时（只在 execute() 周围计时）。
        Assert.Null(endEvent.DurationMs);
        Assert.Null(ResultAt(result, 3).DurationMs);
    }

    [Fact]
    public async Task MalformedToolArgumentsAreReportedWithoutExecuting()
    {
        // 模型给的参数文本不是合法 JSON 时带 argumentsError，预检据此直接产出 isError 结果，
        // 工具绝不带着残缺字段执行。
        var executions = 0;
        var tool = EchoTool() with
        {
            Execute = (_, _, _, _) =>
            {
                executions++;
                return Task.FromResult(new AgentToolResult
                {
                    Content = new ContentBlock[] { new TextContent { Text = "executed" } },
                });
            },
        };
        var malformedCall = Faux.ToolCall("echo", new JsonObject(), "call-1") with
        {
            ArgumentsError = "Unexpected end of JSON input\n\nReceived arguments:\n{\"path\":",
        };
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { malformedCall }, StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("ok"))));
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("go") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { tool } },
            Config(faux.GetModel(), ToolExecutionMode.Sequential),
            CancellationToken.None,
            faux.Provider.Stream);
        var events = await CollectAsync(stream);
        var result = await stream.WaitForResultAsync();

        Assert.Equal(0, executions);
        var toolResult = ResultAt(result, 3);
        Assert.True(toolResult.IsError);
        var text = FirstText(toolResult.Content);
        Assert.Contains("Invalid arguments for tool", text);
        Assert.Contains("Received arguments:", text);
        Assert.Contains("{\"path\":", text);
        var endEvent = events.OfType<AgentEvent.ToolExecutionEnd>().Single();
        Assert.True(endEvent.IsError);
        Assert.Null(endEvent.DurationMs);
    }

    [Fact]
    public async Task SchemaViolationIsReportedWithoutExecuting()
    {
        // 校验在 beforeToolCall 之前：不满足 schema 的调用同样以 isError 结果回给模型，工具不执行。
        var executions = 0;
        var tool = EchoTool() with
        {
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray { "path" },
            },
            Execute = (_, _, _, _) =>
            {
                executions++;
                return Task.FromResult(new AgentToolResult
                {
                    Content = new ContentBlock[] { new TextContent { Text = "executed" } },
                });
            },
        };
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { Faux.ToolCall("echo", new JsonObject(), "call-1") }, StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("ok"))));
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("go") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { tool } },
            Config(faux.GetModel(), ToolExecutionMode.Sequential),
            CancellationToken.None,
            faux.Provider.Stream);
        var result = await stream.WaitForResultAsync();

        Assert.Equal(0, executions);
        var toolResult = ResultAt(result, 3);
        Assert.True(toolResult.IsError);
        var text = FirstText(toolResult.Content);
        Assert.Contains("Validation failed for tool \"echo\"", text);
        Assert.Contains("Received arguments:", text);
    }

    [Fact]
    public async Task ToolReceivesCoercedArguments()
    {
        // 工具拿到的是强制类型转换后的参数，不是模型原样文本。
        JsonNode? received = null;
        var tool = EchoTool() with
        {
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["count"] = new JsonObject { ["type"] = "number" },
                },
            },
            Execute = (_, args, _, _) =>
            {
                received = args;
                return Task.FromResult(new AgentToolResult
                {
                    Content = new ContentBlock[] { new TextContent { Text = "ok" } },
                });
            },
        };
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { Faux.ToolCall("echo", new JsonObject { ["count"] = "5" }, "call-1") },
                StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("done"))));
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("go") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { tool } },
            Config(faux.GetModel(), ToolExecutionMode.Sequential),
            CancellationToken.None,
            faux.Provider.Stream);
        await stream.WaitForResultAsync();

        var args = Assert.IsType<JsonObject>(received);
        // JSON number 在 C# 侧统一落成 double，与 TS 的 JS number 一致。
        Assert.Equal(5d, args["count"]!.GetValue<double>());
    }

    // ---------- 终止语义 ----------

    [Fact]
    public async Task ErrorStopReasonIsHardExit()
    {
        var ordering = new List<string>();
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(Array.Empty<ContentBlock>(), StopReason.Error, "boom")),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("unreachable"))));
        var config = Config(faux.GetModel()) with
        {
            FinishTurn = (_, _) =>
            {
                ordering.Add("finishTurn");
                return Task.FromResult<AgentTurnDecision?>(null);
            },
        };
        var events = new List<AgentEvent>();
        AgentEventSink sink = @event =>
        {
            events.Add(@event);
            if (@event is AgentEvent.TurnEnd) ordering.Add("turn_end");
            return Task.CompletedTask;
        };
        var result = await AgentLoop.RunAsync(
            new Message[] { UserPrompt("hi") },
            new AgentContext { Messages = new List<Message>() },
            config,
            sink,
            CancellationToken.None,
            faux.Provider.Stream);

        // 只有 user + 失败的 assistant 消息；不再继续请求。
        Assert.Equal(2, result.Count);
        var assistant = Assert.IsType<AssistantMessage>(result[1]);
        Assert.Equal(StopReason.Error, assistant.StopReason);
        Assert.Equal("boom", assistant.ErrorMessage);
        // finishTurn 先于 turn_end；只有一个 turn_start（错误后不再发起新回合）。
        Assert.Equal(new[] { "finishTurn", "turn_end" }, ordering);
        Assert.Single(events.OfType<AgentEvent.TurnStart>());
        Assert.Single(events.OfType<AgentEvent.TurnEnd>());
        Assert.Single(events.OfType<AgentEvent.AgentEnd>());
    }

    [Fact]
    public async Task TruncatedToolCallsAllFail()
    {
        var executions = 0;
        var tool = EchoTool() with
        {
            Execute = (_, _, _, _) =>
            {
                executions++;
                return Task.FromResult(new AgentToolResult());
            },
        };
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[]
                {
                    Faux.ToolCall("echo", new JsonObject(), "call-1"),
                    Faux.ToolCall("echo", new JsonObject(), "call-2"),
                },
                StopReason.Length)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("recovered"))));
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("go") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { tool } },
            Config(faux.GetModel()),
            CancellationToken.None,
            faux.Provider.Stream);
        var events = await CollectAsync(stream);
        var result = await stream.WaitForResultAsync();

        // 截断的调用不执行：整批按源顺序失败。
        Assert.Equal(0, executions);
        Assert.Equal(2, events.Count(e => e is AgentEvent.ToolExecutionEnd { IsError: true }));

        // user + system(声明) + assistant(截断) + 2x toolResult(错误) + assistant(恢复) = 6 条。
        Assert.Equal(6, result.Count);
        var first = ResultAt(result, 3);
        Assert.True(first.IsError);
        Assert.Equal(
            "Tool call \"echo\" was not executed: the response hit the output token limit, "
            + "so its arguments may be truncated. Re-issue the tool call with complete arguments.",
            FirstText(first.Content));
        Assert.Equal("recovered", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[^1]).Content[0]).Text);
    }

    // ---------- steering 注入 ----------

    [Fact]
    public async Task SteeringMessagesInjectMidRun()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("first"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("second"))));
        var steeringDelivered = new List<Message> { UserPrompt("steer", 3) };
        var polled = false;
        var config = Config(faux.GetModel()) with
        {
            GetSteeringMessages = () =>
            {
                // 首次轮询（循环起点）返回空，模拟"用户稍后才输入"。
                if (!polled)
                {
                    polled = true;
                    return Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
                }
                var copy = steeringDelivered.ToList();
                steeringDelivered.Clear();
                return Task.FromResult<IReadOnlyList<Message>>(copy);
            },
        };

        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("start") },
            new AgentContext { Messages = new List<Message>() },
            config,
            CancellationToken.None,
            faux.Provider.Stream);
        var result = await stream.WaitForResultAsync();

        // steering 消息在回合之间注入：user, assistant(first), steer(user), assistant(second)。
        Assert.Equal(4, result.Count);
        var steer = Assert.IsType<UserMessage>(result[2]);
        Assert.Equal("steer", FirstText(steer.Content.ToBlocks()));
    }

    // ---------- 并行批次（事件序 vs 消息序） ----------

    [Fact]
    public async Task Parallel_End_Completion_Order_But_Result_Message_Source_Order()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[]
                {
                    Faux.ToolCall("echo", new JsonObject { ["value"] = "first" }, "tool-1"),
                    Faux.ToolCall("echo", new JsonObject { ["value"] = "second" }, "tool-2"),
                },
                StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("done"))));

        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResolved = false;
        var parallelObserved = false;
        var tool = new AgentTool
        {
            Name = "echo",
            Description = "Echo",
            Label = "Echo",
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = async (_, args, _, _) =>
            {
                var value = args?["value"]?.GetValue<string>();
                if (value == "first") { await releaseFirst.Task.ConfigureAwait(true); firstResolved = true; }
                if (value == "second" && !firstResolved) parallelObserved = true;
                return new AgentToolResult
                {
                    Content = new ContentBlock[] { new TextContent { Text = "echoed: " + value } },
                };
            },
        };

        var events = new List<AgentEvent>();
        AgentEventSink sink = @event =>
        {
            events.Add(@event);
            // 第二个调用完成即放行第一个：end 按完成序（tool-2 → tool-1）。
            if (@event is AgentEvent.ToolExecutionEnd { ToolCallId: "tool-2" }) releaseFirst.TrySetResult();
            return Task.CompletedTask;
        };

        await AgentLoop.RunAsync(
            new Message[] { UserPrompt("echo both") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { tool } },
            Config(faux.GetModel()),
            sink,
            CancellationToken.None,
            faux.Provider.Stream);

        Assert.True(parallelObserved, "第二个调用应在第一个完成前起跑");

        var endOrder = events.OfType<AgentEvent.ToolExecutionEnd>().Select(e => e.ToolCallId).ToList();
        Assert.Equal(new[] { "tool-2", "tool-1" }, endOrder);

        var resultMessageOrder = events
            .Where(e => e is AgentEvent.MessageEnd { Message: ToolResultMessage })
            .Select(e => ((AgentEvent.MessageEnd)e).Message)
            .Cast<ToolResultMessage>()
            .Select(m => m.ToolCallId)
            .ToList();
        Assert.Equal(new[] { "tool-1", "tool-2" }, resultMessageOrder);

        var turnResultOrder = events.OfType<AgentEvent.TurnEnd>()
            .SelectMany(e => e.ToolResults)
            .Select(m => m.ToolCallId)
            .ToList();
        Assert.Equal(new[] { "tool-1", "tool-2" }, turnResultOrder);
    }

    // ---------- 顺序模式中止 ----------

    [Fact]
    public async Task Sequential_Mode_Stops_After_Abort()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[]
                {
                    Faux.ToolCall("echo", new JsonObject { ["value"] = "a" }, "call-a"),
                    Faux.ToolCall("echo", new JsonObject { ["value"] = "b" }, "call-b"),
                },
                StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("unreachable"))));
        var cts = new CancellationTokenSource();
        var executions = new List<string>();
        var abortingTool = EchoTool() with
        {
            Execute = (_, args, _, _) =>
            {
                executions.Add(args?["value"]?.GetValue<string>() ?? "");
                cts.Cancel();
                return Task.FromResult(new AgentToolResult());
            },
        };
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("go") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { abortingTool } },
            Config(faux.GetModel(), ToolExecutionMode.Sequential),
            cts.Token,
            faux.Provider.Stream);
        var events = await CollectAsync(stream);
        var result = await stream.WaitForResultAsync();

        // 第一个调用中止后不再执行后续调用（b 没有 start 事件）。
        Assert.Equal(new[] { "a" }, executions);
        Assert.Equal(0, events.Count(e => e is AgentEvent.ToolExecutionStart { ToolCallId: "call-b" }));

        // 带着中止令牌的下一请求由 faux 产出 aborted 终态：硬退出（turn_end + agent_end）。
        var final = Assert.IsType<AssistantMessage>(result[^1]);
        Assert.Equal(StopReason.Aborted, final.StopReason);
        Assert.Equal(2, faux.State.CallCount);
        // 工具回合 + 中止回合各一次 turn_end，随后 agent_end 收场。
        Assert.Equal(2, events.Count(e => e is AgentEvent.TurnEnd));
        Assert.Equal(2, events.Count(e => e is AgentEvent.TurnStart));
        Assert.Single(events.OfType<AgentEvent.AgentEnd>());
    }

    // ---------- 工具抛取消 = 错误结果而非流失败（审计 U-2，pi catch-all 语义） ----------

    [Fact]
    public async Task Tool_Throwing_OCE_Yields_Error_Result_And_Loop_Completes()
    {
        // 工具遵守 CT 惯用法抛 OperationCanceledException（令牌本身未取消）：pi 把它当
        // 普通工具失败落 isError 结果，循环继续下一请求并正常发出 turn_end/agent_end；
        // 而不是 stream.Fail 中断消费者。
        var oceTool = EchoTool() with
        {
            Execute = (_, _, _, _) => throw new OperationCanceledException("canceled"),
        };
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { Faux.ToolCall("echo", new JsonObject { ["value"] = "hi" }) },
                StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("done"))));
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("use the tool") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { oceTool } },
            Config(faux.GetModel()),
            CancellationToken.None,
            faux.Provider.Stream);
        var events = await CollectAsync(stream);
        var result = await stream.WaitForResultAsync();

        var end = Assert.IsType<AgentEvent.ToolExecutionEnd>(events.Single(e => e is AgentEvent.ToolExecutionEnd));
        Assert.True(end.IsError);
        Assert.Contains("canceled", Assert.IsType<TextContent>(end.Result.Content[0]).Text, StringComparison.Ordinal);
        Assert.Equal(2, events.Count(e => e is AgentEvent.TurnEnd));
        Assert.Single(events.OfType<AgentEvent.AgentEnd>());
        Assert.Equal("done", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[^1]).Content[0]).Text);
        Assert.Equal(2, faux.State.CallCount);
    }

    // ---------- DefaultStreamFn ----------

    [Fact]
    public void DefaultStreamFn_Missing_Throws_Pi_Message()
    {
        DefaultStreamFn.Set(null);
        Assert.False(DefaultStreamFn.IsConfigured);
        var error = Assert.Throws<InvalidOperationException>(() => DefaultStreamFn.Get());
        Assert.Equal(
            "No default stream function configured. Pass streamFn explicitly or call setDefaultStreamFn().",
            error.Message);
    }

    [Fact]
    public async Task Loop_Without_StreamFn_Uses_Default_Stream_Fn()
    {
        var faux = FauxProviderWith(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("hi"))));
        DefaultStreamFn.Set(faux.Provider.Stream);
        try
        {
            Assert.True(DefaultStreamFn.IsConfigured);
            var stream = AgentLoop.Loop(
                new Message[] { UserPrompt("hi") },
                new AgentContext { Messages = new List<Message>() },
                Config(faux.GetModel()),
                CancellationToken.None);
            var result = await stream.WaitForResultAsync();
            Assert.Equal(2, result.Count);
            Assert.Equal("hi", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[1]).Content[0]).Text);
        }
        finally
        {
            DefaultStreamFn.Set(null);
        }
    }

    [Fact]
    public async Task Loop_Without_StreamFn_And_No_Default_Fails_The_Stream()
    {
        DefaultStreamFn.Set(null);
        var faux = FauxProviderWith(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("hi"))));
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("hi") },
            new AgentContext { Messages = new List<Message>() },
            Config(faux.GetModel()),
            CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in stream) { }
        });
        Assert.Contains("No default stream function configured", error.Message);
    }

    // ---------- 流收尾（无终态事件） ----------

    /// <summary>构造一个不发终态事件、直接以 End(终值) 收场的流（faux 恒发终态事件，造不出此路径）。</summary>
    private static StreamFn StreamEndingWithoutTerminal(AssistantMessage final, params AssistantMessageEvent[] events)
        => (_, _, _) =>
        {
            var stream = new AssistantMessageEventStream();
            foreach (var @event in events) stream.Push(@event);
            stream.End(final);
            return stream;
        };

    private static void AssertSingleAssistantStartBeforeEnd(
        List<AgentEvent> events, out AgentEvent.MessageStart start, out AgentEvent.MessageEnd end)
    {
        var starts = events.OfType<AgentEvent.MessageStart>().Where(e => e.Message is AssistantMessage).ToList();
        var ends = events.OfType<AgentEvent.MessageEnd>().Where(e => e.Message is AssistantMessage).ToList();
        Assert.Single(starts);
        Assert.Single(ends);
        Assert.True(events.IndexOf(starts[0]) < events.IndexOf(ends[0]), "message_start 应先于 message_end");
        start = starts[0];
        end = ends[0];
    }

    [Fact]
    public async Task Stream_Ending_Without_Terminal_Event_Still_Emits_Message_Start_And_End()
    {
        var final = Faux.AssistantMessage(Faux.Text("hello"));
        var startPartial = final with { Content = Array.Empty<ContentBlock>(), StopReason = StopReason.Pending };
        var deltaPartial = final with
        {
            Content = new ContentBlock[] { new TextContent { Text = "hel" } },
            StopReason = StopReason.Pending,
        };
        var faux = FauxProviderWith(); // 本用例的流由自定义 StreamFn 提供，faux 队列不消费
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("hi") },
            new AgentContext { Messages = new List<Message>() },
            Config(faux.GetModel()),
            CancellationToken.None,
            StreamEndingWithoutTerminal(
                final,
                new AssistantMessageEvent.Start(startPartial),
                new AssistantMessageEvent.TextDelta(0, "hel", deltaPartial)));
        var events = await CollectAsync(stream);
        var result = await stream.WaitForResultAsync();

        AssertSingleAssistantStartBeforeEnd(events, out _, out var end);

        // 终值取流终值（End 传入的 final），不是最后的 partial 快照（TS `await result()`）。
        var endMessage = Assert.IsType<AssistantMessage>(end.Message);
        Assert.Equal("hello", Assert.IsType<TextContent>(endMessage.Content[0]).Text);
        Assert.Equal(StopReason.Stop, endMessage.StopReason);
        Assert.Equal(2, result.Count);
        Assert.Equal("hello", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[^1]).Content[0]).Text);
    }

    [Fact]
    public async Task Stream_Completing_Without_Any_Event_Emits_Message_Start_And_End()
    {
        // 修复点：完全无事件的流收尾也补发 message_start（此前只发 message_end，违反 start→end 不变式）。
        var final = Faux.AssistantMessage(Faux.Text("quiet"));
        var faux = FauxProviderWith();
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("hi") },
            new AgentContext { Messages = new List<Message>() },
            Config(faux.GetModel()),
            CancellationToken.None,
            StreamEndingWithoutTerminal(final));
        var events = await CollectAsync(stream);
        var result = await stream.WaitForResultAsync();

        AssertSingleAssistantStartBeforeEnd(events, out var start, out _);
        Assert.Equal("quiet", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(start.Message).Content[0]).Text);
        Assert.Equal(2, result.Count);
        Assert.Equal("quiet", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[^1]).Content[0]).Text);
    }

    // ---------- getApiKey 回退（JS 真值语义） ----------

    [Fact]
    public async Task GetApiKey_Empty_String_Falls_Back_To_StreamOptions_ApiKey()
    {
        var faux = FauxProviderWith(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("hi"))));
        var seenApiKeys = new List<string?>();
        var fauxStream = faux.Provider.Stream;
        StreamFn streamFn = (model, context, options) =>
        {
            seenApiKeys.Add(options?.ApiKey);
            return fauxStream(model, context, options);
        };

        // getApiKey 返回空串：按 TS 的 || 真值回退到 StreamOptions.ApiKey（?? 语义不会回退）。
        var emptyConfig = Config(faux.GetModel()) with
        {
            GetApiKey = (_, _) => Task.FromResult<string?>(""),
            StreamOptions = new SimpleStreamOptions { ApiKey = "fallback" },
        };
        await AgentLoop.RunAsync(
            new Message[] { UserPrompt("hi") },
            new AgentContext { Messages = new List<Message>() },
            emptyConfig, _ => Task.CompletedTask, CancellationToken.None, streamFn);
        Assert.Equal(new string?[] { "fallback" }, seenApiKeys);

        // 非空值不被覆盖。
        seenApiKeys.Clear();
        var realConfig = Config(faux.GetModel()) with
        {
            GetApiKey = (_, _) => Task.FromResult<string?>("real-key"),
            StreamOptions = new SimpleStreamOptions { ApiKey = "fallback" },
        };
        await AgentLoop.RunAsync(
            new Message[] { UserPrompt("hi") },
            new AgentContext { Messages = new List<Message>() },
            realConfig, _ => Task.CompletedTask, CancellationToken.None, streamFn);
        Assert.Equal(new string?[] { "real-key" }, seenApiKeys);
    }
}
