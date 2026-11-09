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
            PrepareNextTurn = null,
            ToolExecution = ToolExecution,
            BeforeToolCall = BeforeToolCall,
            AfterToolCall = AfterToolCall,
            StreamOptions = streamOptions,
            Identity = Identity,
            GetSteeringMessages = null,
            GetFollowUpMessages = null,
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
