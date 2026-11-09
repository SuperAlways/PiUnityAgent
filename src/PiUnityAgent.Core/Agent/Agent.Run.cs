using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public sealed partial class Agent
{
    /// <summary>以文本（可带图片）启动新 prompt。对应 pi prompt(input, images)。</summary>
    public Task Prompt(string input, IReadOnlyList<ImageContent>? images = null)
    {
        var content = new List<ContentBlock> { new TextContent { Text = input } };
        if (images is not null)
        {
            content.AddRange(images);
        }
        return Prompt(new UserMessage
        {
            Content = MessageContent.From(content),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>以单条消息启动 prompt。</summary>
    public Task Prompt(Message message)
        => Prompt(new Message[] { message });

    /// <summary>以消息批次启动 prompt。</summary>
    public async Task Prompt(IReadOnlyList<Message> messages)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }
        if (HasActiveRun())
        {
            throw new InvalidOperationException(
                "Agent is already processing a prompt. Use steer() or followUp() to queue messages, or wait for completion.");
        }

        await RunPromptMessages(messages).ConfigureAwait(false);
    }

    private Task RunPromptMessages(
        IReadOnlyList<Message> messages,
        bool skipInitialSteeringPoll = false)
        => RunWithLifecycle(signal => AgentLoop.RunAsync(
            messages,
            CreateContextSnapshot(),
            CreateLoopConfig(skipInitialSteeringPoll),
            ProcessEvents,
            signal,
            StreamFunction));

    private Task RunContinuation()
        => RunWithLifecycle(signal => AgentLoop.RunContinueAsync(
            CreateContextSnapshot(),
            CreateLoopConfig(),
            ProcessEvents,
            signal,
            StreamFunction));

    private AgentContext CreateContextSnapshot() => new()
    {
        Messages = _state.Messages.ToList(),
        Tools = _state.Tools.ToList(),
    };

    private AgentLoopConfig CreateLoopConfig(bool skipInitialSteeringPoll = false)
    {
        var thinkingLevel = _state.ThinkingLevel;
        var streamOptions = new SimpleStreamOptions
        {
            SessionId = SessionId,
            OnPayload = OnPayload,
            OnResponse = OnResponse,
            OnProviderStreamEvent = OnProviderStreamEvent,
            Transport = Transport,
            ThinkingBudgets = ThinkingBudgets,
            MaxRetryDelayMs = MaxRetryDelayMs,
            Reasoning = thinkingLevel == ModelThinkingLevel.Off
                ? null
                : ToThinkingLevel(thinkingLevel),
        };

        var skipPoll = skipInitialSteeringPoll;
        return new AgentLoopConfig
        {
            Model = _state.Model,
            ConvertToLlm = ConvertToLlm,
            TransformContext = TransformContext,
            GetApiKey = GetApiKey is null
                ? null
                : (provider, _) => GetApiKey(provider),
            FinishTurn = FinishTurn,
            PrepareRequest = PrepareRequest,
            PrepareNextTurn = CreateLoopPrepareNextTurn(),
            ToolExecution = ToolExecution,
            BeforeToolCall = BeforeToolCall,
            AfterToolCall = AfterToolCall,
            StreamOptions = streamOptions,
            Identity = Identity,
            GetSteeringMessages = () =>
            {
                if (skipPoll)
                {
                    skipPoll = false;
                    return Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
                }
                return Task.FromResult(_steeringQueue.Drain());
            },
            GetFollowUpMessages = () => Task.FromResult(_followUpQueue.Drain()),
        };
    }

    /// <summary>
    /// 把门面层 prepareNextTurn（只收 signal）包装成 S7 loop 层委托（只收 AgentTurnContext）：
    /// 闭包在执行时经 <see cref="Signal"/> 取当前运行的取消令牌——loop 层委托不携带 signal，
    /// 由包装层闭合这一缝隙。无任何回调注册时返回 null（loop 层按缺省跳过）。
    /// </summary>
    private PrepareNextTurn? CreateLoopPrepareNextTurn()
    {
        if (PrepareNextTurnWithContext is null && PrepareNextTurn is null)
        {
            return null;
        }

        return async turn =>
        {
            var signal = Signal ?? CancellationToken.None;
            if (PrepareNextTurnWithContext is not null)
            {
                return await PrepareNextTurnWithContext(
                    new PrepareNextTurnContext(
                        turn.Message,
                        turn.ToolResults,
                        turn.Context,
                        turn.NewMessages),
                    signal).ConfigureAwait(false);
            }

            return await PrepareNextTurn!(signal).ConfigureAwait(false);
        };
    }

    /// <summary>ModelThinkingLevel → 流选项 ThinkingLevel（Off 不映射，由调用点归一为 null）。</summary>
    private static ThinkingLevel ToThinkingLevel(ModelThinkingLevel level) => level switch
    {
        ModelThinkingLevel.Minimal => ThinkingLevel.Minimal,
        ModelThinkingLevel.Low => ThinkingLevel.Low,
        ModelThinkingLevel.Medium => ThinkingLevel.Medium,
        ModelThinkingLevel.High => ThinkingLevel.High,
        ModelThinkingLevel.XHigh => ThinkingLevel.XHigh,
        ModelThinkingLevel.Max => ThinkingLevel.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Off has no provider-level ThinkingLevel."),
    };
}
