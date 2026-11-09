using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// 低层 agent 循环。对应 TS <c>agent-loop.ts</c> / pi.net <c>AgentLoop.cs</c>：
/// 全程使用 transcript 消息（LLM 兼容消息仅在调用边界经 <c>ConvertToLlm</c> 转换）。
/// 主循环 = 外层（follow-up 续跑）× 内层（工具调用 / steering 消息）。
/// </summary>
public static partial class AgentLoop
{
    /// <summary>
    /// 以新 prompt 消息启动 agent 循环。prompt 会追加进上下文并发出相应事件。
    /// 对应 TS <c>agentLoop()</c>。
    /// </summary>
    public static EventStream<AgentEvent, IReadOnlyList<Message>> Loop(
        IReadOnlyList<Message> prompts,
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken signal,
        StreamFn? streamFn = null)
    {
        var stream = CreateAgentStream();
        _ = PumpAsync(RunAsync(prompts, context, config, PushSink(stream), signal, streamFn), stream);
        return stream;
    }

    /// <summary>
    /// 从当前上下文继续循环（用于重试；上下文最后一条消息须经 ConvertToLlm 转换为
    /// user 或 toolResult，否则 provider 会拒绝请求）。对应 TS <c>agentLoopContinue()</c>。
    /// </summary>
    public static EventStream<AgentEvent, IReadOnlyList<Message>> Continue(
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken signal,
        StreamFn? streamFn = null)
    {
        ValidateContinueContext(context);
        var stream = CreateAgentStream();
        _ = PumpAsync(RunContinueAsync(context, config, PushSink(stream), signal, streamFn), stream);
        return stream;
    }

    /// <summary>带 prompt 的异步入口（对应 TS <c>runAgentLoop</c>）。</summary>
    public static async Task<IReadOnlyList<Message>> RunAsync(
        IReadOnlyList<Message> prompts,
        AgentContext context,
        AgentLoopConfig config,
        AgentEventSink emit,
        CancellationToken signal,
        StreamFn? streamFn = null)
    {
        var initialMessages = ToolLoadout.DeclareToolChanges(
            context, prompts, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToList();
        var newMessages = new List<Message>(initialMessages);
        var currentContext = context with
        {
            Messages = [.. context.Messages, .. initialMessages],
        };

        await emit(new AgentEvent.AgentStart()).ConfigureAwait(false);
        await emit(new AgentEvent.TurnStart()).ConfigureAwait(false);
        foreach (var message in initialMessages)
        {
            await emit(new AgentEvent.MessageStart(message)).ConfigureAwait(false);
            await emit(new AgentEvent.MessageEnd(message)).ConfigureAwait(false);
        }

        await RunLoop(currentContext, newMessages, config, signal, emit,
            streamFn ?? DefaultStreamFn.Get()).ConfigureAwait(false);
        return newMessages;
    }

    /// <summary>续跑入口（对应 TS <c>runAgentLoopContinue</c>）。</summary>
    public static async Task<IReadOnlyList<Message>> RunContinueAsync(
        AgentContext context,
        AgentLoopConfig config,
        AgentEventSink emit,
        CancellationToken signal,
        StreamFn? streamFn = null)
    {
        ValidateContinueContext(context);
        var newMessages = new List<Message>();
        await emit(new AgentEvent.AgentStart()).ConfigureAwait(false);
        await emit(new AgentEvent.TurnStart()).ConfigureAwait(false);
        await RunLoop(context, newMessages, config, signal, emit,
            streamFn ?? DefaultStreamFn.Get()).ConfigureAwait(false);
        return newMessages;
    }

    /// <summary>异步泵：循环完成则写终值，异常则转入流（对齐 pi.net 的 PumpAsync 路线）。</summary>
    private static async Task PumpAsync(
        Task<IReadOnlyList<Message>> run,
        EventStream<AgentEvent, IReadOnlyList<Message>> stream)
    {
        try
        {
            stream.End(await run.ConfigureAwait(false));
        }
        catch (Exception error)
        {
            stream.Fail(error);
        }
    }

    /// <summary>
    /// 续跑上下文守卫：空上下文或最后一条为 assistant 都无法构成下一次请求
    /// （provider 需要 user/toolResult 收尾）。
    /// </summary>
    private static void ValidateContinueContext(AgentContext context)
    {
        if (context.Messages.Count == 0)
            throw new InvalidOperationException("Cannot continue: no messages in context");
        if (context.Messages[^1] is AssistantMessage)
            throw new InvalidOperationException("Cannot continue from message role: assistant");
    }

    /// <summary>事件流装配：<c>agent_end</c> 为终态事件，终值即其携带的新增消息。</summary>
    private static EventStream<AgentEvent, IReadOnlyList<Message>> CreateAgentStream()
        => new(
            @event => @event is AgentEvent.AgentEnd,
            @event => ((AgentEvent.AgentEnd)@event).Messages);

    /// <summary>把同步 Push 适配为异步事件槽。</summary>
    private static AgentEventSink PushSink(EventStream<AgentEvent, IReadOnlyList<Message>> stream)
        => @event =>
        {
            stream.Push(@event);
            return Task.CompletedTask;
        };
}
