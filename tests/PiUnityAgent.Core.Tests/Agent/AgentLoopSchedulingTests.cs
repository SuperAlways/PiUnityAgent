using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

/// <summary>
/// S7 调度回调测试：finishTurn 决策、prepareRequest/prepareNextTurn 状态替换、
/// steering/follow-up 注入时机与 explicitContinuation（对应 pi agent-loop.test.ts 的调度族）。
/// </summary>
public class AgentLoopSchedulingTests
{
    private static FauxProvider FauxProviderWith(params FauxResponseStep[] steps)
    {
        var faux = new FauxProvider();
        faux.SetResponses(steps);
        return faux;
    }

    private static AgentLoopConfig Config(Model model)
        => new()
        {
            Model = model,
            ConvertToLlm = messages => Task.FromResult<IReadOnlyList<Message>>(messages),
            StreamOptions = new SimpleStreamOptions(),
        };

    private static Message UserPrompt(string text, long timestamp = 1)
        => new UserMessage { Timestamp = timestamp, Content = MessageContent.From(text) };

    private static AgentTool EchoTool(string name = "echo")
        => new()
        {
            Name = name, Description = "Echo", Label = "Echo",
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = (_, _, _, _) => Task.FromResult(new AgentToolResult()),
        };

    private static string FirstText(IReadOnlyList<ContentBlock> content)
        => Assert.IsType<TextContent>(content[0]).Text;

    // ---------- finishTurn ----------

    [Fact]
    public async Task FinishTurn_End_Stops_Requests()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))));
        var config = Config(faux.GetModel()) with
        {
            FinishTurn = (_, _) => Task.FromResult<AgentTurnDecision?>(AgentTurnDecision.End),
        };
        await foreach (var _ in AgentLoop.Loop(
            new Message[] { UserPrompt("hi") },
            new AgentContext { Messages = new List<Message>() }, config, CancellationToken.None, faux.Provider.Stream)) { }
        Assert.Equal(1, faux.State.CallCount);
    }

    [Fact]
    public async Task Explicit_Continuation_Makes_Exactly_One_Context_Only_Turn()
    {
        // 没有自然请求（无 tool-result / steering / follow-up）时，continue 兑现为一次仅上下文的回合。
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("response 1"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("response 2"))));
        var finishCalls = 0;
        var config = Config(faux.GetModel()) with
        {
            FinishTurn = (_, _) =>
            {
                finishCalls++;
                return Task.FromResult<AgentTurnDecision?>(
                    finishCalls == 1 ? AgentTurnDecision.Continue : null);
            },
        };

        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("run") },
            new AgentContext { Messages = new List<Message>() },
            config, CancellationToken.None, faux.Provider.Stream);
        var result = await stream.WaitForResultAsync();

        Assert.Equal(2, faux.State.CallCount);
        Assert.Equal(2, finishCalls);
        Assert.Equal(3, result.Count);
        Assert.Equal("response 2", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[^1]).Content[0]).Text);
    }

    // ---------- continue 守卫 ----------

    [Fact]
    public void Continue_Rejects_Assistant_Last_Message()
    {
        var faux = FauxProviderWith();
        var context = new AgentContext
        {
            Messages = new List<Message> { Faux.AssistantMessage(Faux.Text("x")) },
        };
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentLoop.Continue(context, Config(faux.GetModel()), CancellationToken.None, null));
        Assert.Equal("Cannot continue from message role: assistant", error.Message);
    }

    [Fact]
    public void Continue_Rejects_Empty_Context()
    {
        var faux = FauxProviderWith();
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentLoop.Continue(
                new AgentContext { Messages = new List<Message>() },
                Config(faux.GetModel()), CancellationToken.None, null));
        Assert.Equal("Cannot continue: no messages in context", error.Message);
    }

    [Fact]
    public async Task Continue_Does_Not_Reemit_Existing_Context_And_Result_Excludes_It()
    {
        // 续跑不发 prompt 事件、初始 turn_start 照发；终值只含本次新增消息。
        var faux = FauxProviderWith(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("resumed"))));
        var prior = UserPrompt("prior", 1);
        var context = new AgentContext { Messages = new List<Message> { prior } };
        var events = new List<AgentEvent>();

        var stream = AgentLoop.Continue(context, Config(faux.GetModel()), CancellationToken.None, faux.Provider.Stream);
        await foreach (var @event in stream) events.Add(@event);
        var result = await stream.WaitForResultAsync();

        Assert.IsType<AgentEvent.AgentStart>(events[0]);
        Assert.IsType<AgentEvent.TurnStart>(events[1]);
        Assert.IsType<AgentEvent.MessageStart>(events[2]); // assistant 响应，无 user prompt 事件
        Assert.DoesNotContain(events.OfType<AgentEvent.MessageStart>(), e => e.Message is UserMessage);
        Assert.Single(result);
        Assert.IsType<AssistantMessage>(result[0]);
        // 续跑不追加 prompt：上下文 = 既有 prior + 本次 assistant 响应。
        Assert.Equal(2, context.Messages.Count);
        Assert.Contains(context.Messages, m => ReferenceEquals(m, prior));
    }

    // ---------- steering ----------

    // 注：pi 在 runLoop 入口先轮询一次 steering；本用例让第一次轮询返回空、第二次（工具批后）返回 steering。
    // 断言目标按 pi 语义订正：RunAsync 以 `context with { Messages = [...] }` 复制出运行上下文
    // （pi.net 同款），调用方传入的 context 对象不被回写，故注入点看「第二次请求的 transcript」与结果消息。
    [Fact]
    public async Task Steering_Messages_Are_Injected_After_Tool_Batch()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { new ToolCall { Id = "c1", Name = "echo" } }, StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("final"))));
        var steering = new Message[] { UserPrompt("steer", 3) };
        var poll = 0;
        IReadOnlyList<Message> NextSteering()
        {
            poll++;
            return poll == 2 ? steering : Array.Empty<Message>();
        }
        var config = Config(faux.GetModel()) with
        {
            GetSteeringMessages = () => Task.FromResult(NextSteering()),
        };
        var requestContexts = new List<IReadOnlyList<Message>>();
        var fauxStream = faux.Provider.Stream;
        StreamFn streamFn = (model, context, options) =>
        {
            requestContexts.Add(context.Messages.ToList());
            return fauxStream(model, context, options);
        };
        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("hi") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { EchoTool() } },
            config, CancellationToken.None, streamFn);
        var result = await stream.WaitForResultAsync();

        Assert.Equal(2, faux.State.CallCount);
        // 工具批之后发起的第二次请求：transcript 已携带 steering 消息。
        Assert.True(requestContexts.Count >= 2);
        Assert.Contains(requestContexts[1], m =>
            m is UserMessage { Content: MessageContent.Text { Value: "steer" } });
        // steering 消息也计入本次运行的新增消息。
        Assert.Contains(result, m =>
            m is UserMessage { Content: MessageContent.Text { Value: "steer" } });
    }

    // ---------- prepareRequest / prepareNextTurn ----------

    [Fact]
    public async Task PrepareRequest_Can_Replace_Request_State()
    {
        var faux = FauxProviderWith(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("done"))));
        var replacementModel = faux.GetModel() with { Id = "replacement", Name = "replacement" };
        var canonical = UserPrompt("canonical projection", 1);
        var steering = UserPrompt("steering", 2);

        var steeringDelivered = false;
        var prepareCalls = 0;
        Model? requestModel = null;
        IReadOnlyList<Message>? requestContext = null;
        ThinkingLevel? requestReasoning = null;

        var config = Config(faux.GetModel()) with
        {
            GetSteeringMessages = () =>
            {
                if (steeringDelivered)
                    return Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
                steeringDelivered = true;
                return Task.FromResult<IReadOnlyList<Message>>(new Message[] { steering });
            },
            PrepareRequest = (request, _) =>
            {
                prepareCalls++;
                // prepareRequest 看到的 context 已包含本轮注入的 steering。
                Assert.Contains(request.Context.Messages, m => ReferenceEquals(m, steering));
                return Task.FromResult<AgentRequestUpdate?>(new AgentRequestUpdate
                {
                    Context = new AgentContext { Messages = new List<Message> { canonical } },
                    Model = replacementModel,
                    ThinkingLevel = ModelThinkingLevel.High,
                });
            },
        };

        var fauxStream = faux.Provider.Stream;
        StreamFn streamFn = (model, context, options) =>
        {
            requestModel = model;
            // 快照：llmContext.Messages 与运行上下文共享同一列表（identity ConvertToLlm 别名，
            // 流式 partial 会原地追加），断言取请求时刻的形状。
            requestContext = context.Messages.ToList();
            requestReasoning = (options as SimpleStreamOptions)?.Reasoning;
            return fauxStream(model, context, options);
        };

        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("prompt") },
            new AgentContext { Messages = new List<Message>() },
            config, CancellationToken.None, streamFn);
        var result = await stream.WaitForResultAsync();

        Assert.Equal(1, prepareCalls);
        Assert.Equal(replacementModel, requestModel);
        Assert.Single(requestContext!);
        Assert.Equal("canonical projection", FirstText(Assert.IsType<UserMessage>(requestContext![0]).Content.ToBlocks()));
        Assert.Equal(ThinkingLevel.High, requestReasoning);
        // 终态消息的 thinkingLevel 记为本次请求档位（prepareRequest 更新后生效）。
        Assert.Equal(ModelThinkingLevel.High, Assert.IsType<AssistantMessage>(result[^1]).ThinkingLevel);
    }

    [Fact]
    public async Task PrepareNextTurn_Snapshot_Applies_Before_Next_Request()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { Faux.ToolCall("echo", new JsonObject { ["value"] = "hello" }, "tool-1") },
                StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("done"))));

        var prepared = false;
        var prepareCalls = 0;
        var llmCalls = 0;
        var secondTurnHadUpdate = false;

        var config = Config(faux.GetModel()) with
        {
            PrepareNextTurn = _ =>
            {
                prepareCalls++;
                if (prepared) return Task.FromResult<AgentLoopTurnUpdate?>(null);
                prepared = true;
                return Task.FromResult<AgentLoopTurnUpdate?>(new AgentLoopTurnUpdate
                {
                    Messages = new Message[]
                    {
                        new SystemMessage { Timestamp = 1, Content = MessageContent.From("updated guidance") },
                    },
                });
            },
        };

        var fauxStream = faux.Provider.Stream;
        StreamFn streamFn = (_, context, options) =>
        {
            llmCalls++;
            if (llmCalls == 2)
            {
                secondTurnHadUpdate = context.Messages.Any(m =>
                    m is SystemMessage system && system.Content is MessageContent.Text { Value: "updated guidance" });
            }
            return fauxStream(_, context, options);
        };

        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("echo something") },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { EchoTool() } },
            config, CancellationToken.None, streamFn);
        await foreach (var _ in stream) { }

        Assert.Equal(2, llmCalls);
        Assert.Equal(1, prepareCalls);
        Assert.True(secondTurnHadUpdate, "第二次请求的 transcript 应携带 prepareNextTurn 追加的消息");
    }

    // ---------- follow-up ----------

    [Fact]
    public async Task FollowUp_Messages_Continue_After_Inner_Loop_Exits()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("first"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("second"))));
        var followUp = new Message[] { UserPrompt("follow", 2) };
        var followUpDelivered = false;
        var config = Config(faux.GetModel()) with
        {
            GetFollowUpMessages = () =>
            {
                if (followUpDelivered)
                    return Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
                followUpDelivered = true;
                return Task.FromResult<IReadOnlyList<Message>>(followUp);
            },
        };

        var stream = AgentLoop.Loop(
            new Message[] { UserPrompt("start") },
            new AgentContext { Messages = new List<Message>() },
            config, CancellationToken.None, faux.Provider.Stream);
        var result = await stream.WaitForResultAsync();

        Assert.Equal(2, faux.State.CallCount);
        Assert.Equal(4, result.Count);
        var follow = Assert.IsType<UserMessage>(result[2]);
        Assert.Equal("follow", FirstText(follow.Content.ToBlocks()));
        Assert.Equal("second", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[^1]).Content[0]).Text);
    }
}
