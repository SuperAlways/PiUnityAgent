using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// 主循环实现（<see cref="AgentLoop"/> 的 partial）。对应 TS <c>runLoop</c>：
/// 外层循环（agent 本应停止时若有 follow-up 消息则继续）× 内层循环（工具调用与
/// steering 消息，直到模型不再请求工具且队列已清空）。
/// </summary>
public static partial class AgentLoop
{
    private static async Task RunLoop(
        AgentContext initialContext,
        List<Message> newMessages,
        AgentLoopConfig initialConfig,
        CancellationToken signal,
        AgentEventSink emit,
        StreamFn streamFunction)
    {
        var currentContext = initialContext;
        var config = initialConfig;
        AgentTurnContext? lastCompletedTurn = null;
        var explicitContinuation = false;
        // 起始处先查一次 steering（用户可能在等待期间已输入）。
        var pendingMessages = new List<Message>(await PollAsync(config.GetSteeringMessages).ConfigureAwait(false));

        while (true)
        {
            var hasMoreToolCalls = true;

            // 内层循环：处理工具调用与 steering 消息。
            while (hasMoreToolCalls || pendingMessages.Count > 0)
            {
                var preparedMessages = new List<Message>();
                if (lastCompletedTurn is not null)
                {
                    var nextTurnSnapshot = await (config.PrepareNextTurn?.Invoke(lastCompletedTurn)
                        ?? Task.FromResult<AgentLoopTurnUpdate?>(null)).ConfigureAwait(false);
                    if (nextTurnSnapshot is not null)
                    {
                        currentContext = nextTurnSnapshot.Context ?? currentContext;
                        preparedMessages = (nextTurnSnapshot.Messages ?? Array.Empty<Message>()).ToList();
                        config = ApplyThinkingLevelUpdate(
                            config with { Model = nextTurnSnapshot.Model ?? config.Model },
                            nextTurnSnapshot.ThinkingLevel);
                    }

                    // Prepare 可能长时运行（例如 compaction）。仅当上一次轮询为空时再查一次，
                    // 否则 one-at-a-time 模式会在同一回合投递两条消息。
                    if (pendingMessages.Count == 0)
                        pendingMessages = (await PollAsync(config.GetSteeringMessages).ConfigureAwait(false)).ToList();
                    await emit(new AgentEvent.TurnStart()).ConfigureAwait(false);
                }

                // 下一次助手响应前，处理 prepared 与排队的消息（含工具装载变更公告）。
                var declared = ToolLoadout.DeclareToolChanges(
                    currentContext,
                    [.. preparedMessages, .. pendingMessages],
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                foreach (var pending in declared)
                {
                    await emit(new AgentEvent.MessageStart(pending)).ConfigureAwait(false);
                    await emit(new AgentEvent.MessageEnd(pending)).ConfigureAwait(false);
                    currentContext.Messages.Add(pending);
                    newMessages.Add(pending);
                }
                pendingMessages.Clear();

                var requestUpdate = await (config.PrepareRequest?.Invoke(
                        new PrepareRequestContext(currentContext, config.Model, EffectiveThinkingLevel(config)),
                        signal)
                    ?? Task.FromResult<AgentRequestUpdate?>(null)).ConfigureAwait(false);
                if (requestUpdate is not null)
                {
                    currentContext = requestUpdate.Context ?? currentContext;
                    config = ApplyThinkingLevelUpdate(
                        config with { Model = requestUpdate.Model ?? config.Model },
                        requestUpdate.ThinkingLevel);
                }

                // 流式获取助手响应。
                var message = await StreamAssistantResponse(
                    currentContext, config, signal, emit, streamFunction).ConfigureAwait(false);
                newMessages.Add(message);

                if (message.StopReason is StopReason.Error or StopReason.Aborted)
                {
                    // 错误与中止是硬退出：不再轮询队列。
                    lastCompletedTurn = new AgentTurnContext(
                        message, Array.Empty<ToolResultMessage>(), currentContext, newMessages);
                    if (config.FinishTurn is not null)
                        await config.FinishTurn(lastCompletedTurn, signal).ConfigureAwait(false);
                    await emit(new AgentEvent.TurnEnd(message, Array.Empty<ToolResultMessage>())).ConfigureAwait(false);
                    await emit(new AgentEvent.AgentEnd(newMessages)).ConfigureAwait(false);
                    return;
                }

                // 收集工具调用。
                var toolCalls = message.Content.OfType<ToolCall>().ToList();

                var toolResults = new List<ToolResultMessage>();
                hasMoreToolCalls = false;
                if (toolCalls.Count > 0)
                {
                    // "length" 表示输出被 token 上限截断：所有工具调用的参数都可能被
                    // 截断且不可安全执行，全部报错让模型重新发起。
                    var executedToolBatch = message.StopReason == StopReason.Length
                        ? await ToolExecution.FailToolCallsFromTruncatedMessage(toolCalls, emit).ConfigureAwait(false)
                        : await ToolExecution.ExecuteToolCalls(
                            currentContext, message, config, signal, emit).ConfigureAwait(false);
                    toolResults.AddRange(executedToolBatch.Messages);
                    hasMoreToolCalls = !executedToolBatch.Terminate;

                    foreach (var result in toolResults)
                    {
                        currentContext.Messages.Add(result);
                        newMessages.Add(result);
                    }
                }

                lastCompletedTurn = new AgentTurnContext(message, toolResults, currentContext, newMessages);
                var decision = await (config.FinishTurn?.Invoke(lastCompletedTurn, signal)
                    ?? Task.FromResult<AgentTurnDecision?>(null)).ConfigureAwait(false);
                await emit(new AgentEvent.TurnEnd(message, toolResults)).ConfigureAwait(false);

                if (decision is AgentTurnDecision.End)
                {
                    await emit(new AgentEvent.AgentEnd(newMessages)).ConfigureAwait(false);
                    return;
                }

                explicitContinuation = decision is AgentTurnDecision.Continue;
                pendingMessages = (await PollAsync(config.GetSteeringMessages).ConfigureAwait(false)).ToList();
                if (hasMoreToolCalls || pendingMessages.Count > 0)
                    explicitContinuation = false;
            }

            // agent 本应停止：检查 follow-up 消息。
            var followUpMessages = await PollAsync(config.GetFollowUpMessages).ConfigureAwait(false);
            if (followUpMessages.Count > 0)
            {
                explicitContinuation = false;
                pendingMessages = followUpMessages.ToList();
                continue;
            }

            // 没有自然产生的请求：用一次仅上下文的回合兑现 continuation 决策。
            if (explicitContinuation)
            {
                explicitContinuation = false;
                continue;
            }

            break;
        }

        await emit(new AgentEvent.AgentEnd(newMessages)).ConfigureAwait(false);
    }

    /// <summary>钩子安全轮询：委托缺失或抛出时返回空列表（契约：钩子不得抛出）。</summary>
    private static async Task<IReadOnlyList<Message>> PollAsync(
        Func<Task<IReadOnlyList<Message>>>? poll)
    {
        try
        {
            if (poll is null) return Array.Empty<Message>();
            return await poll().ConfigureAwait(false) ?? Array.Empty<Message>();
        }
        catch
        {
            return Array.Empty<Message>();
        }
    }

    /// <summary>
    /// prepareRequest / prepareNextTurn 对思考档位的更新规则（对齐 pi）：
    /// 更新值未给出时保持原值；<c>Off</c> 归一为未设置（<c>SimpleStreamOptions.Reasoning = null</c>）。
    /// </summary>
    private static AgentLoopConfig ApplyThinkingLevelUpdate(AgentLoopConfig config, ModelThinkingLevel? updated)
    {
        if (updated is null)
            return config;

        var options = config.StreamOptions ?? new SimpleStreamOptions();
        return config with
        {
            StreamOptions = options with
            {
                Reasoning = updated == ModelThinkingLevel.Off ? null : ToThinkingLevel(updated.Value),
            },
        };
    }
}
