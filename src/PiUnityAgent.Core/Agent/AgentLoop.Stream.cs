using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// 助手流式响应（<see cref="AgentLoop"/> 的 partial）。对应 TS <c>streamAssistantResponse</c>：
/// transformContext → convertToLlm → 解析 API key → streamFn；事件逐条转发为
/// <c>message_start / message_update / message_end</c>。
/// </summary>
public static partial class AgentLoop
{
    private static async Task<AssistantMessage> StreamAssistantResponse(
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit,
        StreamFn streamFunction)
    {
        // 上下文变换（transcript → transcript）。
        IReadOnlyList<Message> messages = context.Messages;
        if (config.TransformContext is not null)
            messages = await config.TransformContext(messages, signal).ConfigureAwait(false);

        // 转换为 LLM 兼容消息（调用边界）并规范化。S7 的 transcript 已由装载公告
        // （ToolLoadout）携带工具声明系统消息，故规范化只吃消息，不传 Context.Tools，
        // 避免与公告重复声明（与 pi.net「transcript 自带声明」路线一致）。
        var llmMessages = await config.ConvertToLlm(messages).ConfigureAwait(false);
        var llmContext = TranscriptNormalization.NormalizeContext(new Context { Messages = llmMessages });

        // 解析 API key（支持会过期的短时 token）。
        var resolvedApiKey = config.GetApiKey is not null
            ? await config.GetApiKey(config.Model.Provider, signal).ConfigureAwait(false)
            : null;
        resolvedApiKey ??= config.StreamOptions?.ApiKey;

        // TS：`identity: mergeClientIdentity(DEFAULT_AGENT_IDENTITY, config.identity)`。
        var identity = ClientIdentityMerger.Merge(DefaultAgentIdentity, config.Identity);
        var options = (config.StreamOptions ?? new SimpleStreamOptions()) with
        {
            ApiKey = resolvedApiKey,
            Identity = identity,
            CancellationToken = signal,
        };

        var response = streamFunction(config.Model, llmContext, options);

        var partialMessage = (AssistantMessage?)null;
        var addedPartial = false;

        // TS `result()`：把终态消息的 thinkingLevel 记为本次请求的档位（覆盖 provider 回传值）。
        var requestThinkingLevel = EffectiveThinkingLevel(config);
        AssistantMessage RecordThinkingLevel(AssistantMessage message)
            => message with { ThinkingLevel = requestThinkingLevel };

        // 消费助手事件流。取消的唯一入口是 options.CancellationToken（S3 定案：provider 收到
        // 令牌后自行产出 Error/Aborted 终态），不给迭代器另挂原始令牌——否则中止场景下
        // 令牌先于缓冲终态事件被观察到，硬退出路径（§4.1.5）会被迭代异常截胡。
        var enumerator = response.GetAsyncEnumerator(CancellationToken.None);
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var @event = enumerator.Current;
                switch (@event)
                {
                    case AssistantMessageEvent.Start start:
                        partialMessage = start.Partial;
                        context.Messages.Add(partialMessage);
                        addedPartial = true;
                        await emit(new AgentEvent.MessageStart(partialMessage)).ConfigureAwait(false);
                        break;

                    case AssistantMessageEvent.Error error:
                    {
                        var finalMessage = RecordThinkingLevel(error.Message);
                        if (addedPartial)
                            context.Messages[^1] = finalMessage;
                        else
                            context.Messages.Add(finalMessage);
                        if (!addedPartial)
                            await emit(new AgentEvent.MessageStart(finalMessage)).ConfigureAwait(false);
                        await emit(new AgentEvent.MessageEnd(finalMessage)).ConfigureAwait(false);
                        return finalMessage;
                    }

                    case AssistantMessageEvent.Done done:
                    {
                        var finalMessage = RecordThinkingLevel(done.Message);
                        if (addedPartial)
                            context.Messages[^1] = finalMessage;
                        else
                            context.Messages.Add(finalMessage);
                        if (!addedPartial)
                            await emit(new AgentEvent.MessageStart(finalMessage)).ConfigureAwait(false);
                        await emit(new AgentEvent.MessageEnd(finalMessage)).ConfigureAwait(false);
                        return finalMessage;
                    }

                    default:
                        var snapshot = GetPartial(@event);
                        if (partialMessage is not null && snapshot is not null)
                        {
                            partialMessage = snapshot;
                            context.Messages[^1] = partialMessage;
                            await emit(new AgentEvent.MessageUpdate(@event, partialMessage)).ConfigureAwait(false);
                        }
                        break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        // 流意外结束（无终态事件）：以当前快照收尾。
        var fallback = RecordThinkingLevel(partialMessage ?? new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = config.Model.Api,
            Provider = config.Model.Provider,
            Model = config.Model.Id,
            Usage = new Usage(),
            StopReason = StopReason.Error,
            ErrorMessage = "Stream ended without a terminal event",
            Timestamp = 0,
        });
        if (addedPartial)
            context.Messages[^1] = fallback;
        else
            context.Messages.Add(fallback);
        await emit(new AgentEvent.MessageEnd(fallback)).ConfigureAwait(false);
        return fallback;
    }

    /// <summary>取事件携带的部分消息快照（终态事件取最终消息）。</summary>
    private static AssistantMessage? GetPartial(AssistantMessageEvent @event) => @event switch
    {
        AssistantMessageEvent.Start start => start.Partial,
        AssistantMessageEvent.TextStart textStart => textStart.Partial,
        AssistantMessageEvent.TextDelta { Partial: var p } => p,
        AssistantMessageEvent.TextEnd { Partial: var p } => p,
        AssistantMessageEvent.ThinkingDelta { Partial: var p } => p,
        AssistantMessageEvent.ThinkingStart { Partial: var p } => p,
        AssistantMessageEvent.ThinkingEnd { Partial: var p } => p,
        AssistantMessageEvent.ToolCallStart { Partial: var p } => p,
        AssistantMessageEvent.ToolCallDelta { Partial: var p } => p,
        AssistantMessageEvent.ToolCallEnd { Partial: var p } => p,
        AssistantMessageEvent.Done done => done.Message,
        AssistantMessageEvent.Error error => error.Message,
        _ => null,
    };

    /// <summary>本次请求的有效思考档位：选项未给出时按 pi 视为 Off。</summary>
    private static ModelThinkingLevel EffectiveThinkingLevel(AgentLoopConfig config)
        => config.StreamOptions?.Reasoning is { } level ? MapToModelThinkingLevel(level) : ModelThinkingLevel.Off;

    /// <summary>pi 思考档位 → 模型档位（Off 档不存在于 ThinkingLevel，逐一映射而非按序号强转）。</summary>
    private static ModelThinkingLevel MapToModelThinkingLevel(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal => ModelThinkingLevel.Minimal,
        ThinkingLevel.Low => ModelThinkingLevel.Low,
        ThinkingLevel.Medium => ModelThinkingLevel.Medium,
        ThinkingLevel.High => ModelThinkingLevel.High,
        ThinkingLevel.XHigh => ModelThinkingLevel.XHigh,
        ThinkingLevel.Max => ModelThinkingLevel.Max,
        _ => ModelThinkingLevel.Off,
    };

    /// <summary>模型档位 → pi 思考档位（Off 由调用方归一为 null，不进此映射）。</summary>
    private static ThinkingLevel ToThinkingLevel(ModelThinkingLevel level) => level switch
    {
        ModelThinkingLevel.Minimal => ThinkingLevel.Minimal,
        ModelThinkingLevel.Low => ThinkingLevel.Low,
        ModelThinkingLevel.Medium => ThinkingLevel.Medium,
        ModelThinkingLevel.High => ThinkingLevel.High,
        ModelThinkingLevel.XHigh => ThinkingLevel.XHigh,
        ModelThinkingLevel.Max => ThinkingLevel.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Off is normalized by the caller."),
    };
}
