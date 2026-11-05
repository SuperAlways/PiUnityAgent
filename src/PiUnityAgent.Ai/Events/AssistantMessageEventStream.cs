using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Events;

/// <summary>一条助手响应的流式事件；维护最新 Partial，终态写 DurationMs。</summary>
public sealed class AssistantMessageEventStream : IAssistantMessageEventStream
{
    private readonly EventStream<AssistantMessageEvent, AssistantMessage> _inner;
    private readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _startedAtMonotonic = Stopwatch.GetTimestamp();
    private volatile AssistantMessage? _partial;

    public AssistantMessageEventStream()
        => _inner = new EventStream<AssistantMessageEvent, AssistantMessage>(
            isComplete: @event => @event.IsTerminal,
            extractResult: @event => @event switch
            {
                AssistantMessageEvent.Done done => done.Message,
                AssistantMessageEvent.Error error => error.ErrorMessage,
                _ => throw new InvalidOperationException("Terminal event expected."),
            });

    public AssistantMessage? Partial => _partial;

    public void Push(AssistantMessageEvent @event)
    {
        if (_inner.IsEnded)
        {
            return;
        }

        AssistantMessageEventValidation.Validate(@event);
        Time(@event switch
        {
            AssistantMessageEvent.Done done => done.Message,
            AssistantMessageEvent.Error error => error.ErrorMessage,
            _ => null,
        });

        _partial = @event switch
        {
            AssistantMessageEvent.Start start => start.Partial,
            AssistantMessageEvent.TextStart text => text.Partial,
            AssistantMessageEvent.TextDelta text => text.Partial,
            AssistantMessageEvent.TextEnd text => text.Partial,
            AssistantMessageEvent.ThinkingStart thinking => thinking.Partial,
            AssistantMessageEvent.ThinkingDelta thinking => thinking.Partial,
            AssistantMessageEvent.ThinkingEnd thinking => thinking.Partial,
            AssistantMessageEvent.ToolCallStart call => call.Partial,
            AssistantMessageEvent.ToolCallDelta call => call.Partial,
            AssistantMessageEvent.ToolCallEnd call => call.Partial,
            AssistantMessageEvent.Done done => done.Message,
            AssistantMessageEvent.Error error => error.ErrorMessage,
            _ => _partial,
        };

        _inner.Push(@event);
    }

    public void End(AssistantMessage finalMessage)
    {
        if (_inner.IsEnded)
        {
            return;
        }

        Time(finalMessage);
        _partial = finalMessage;
        _inner.End(finalMessage);
    }

    public Task<AssistantMessage> WaitForDoneAsync(CancellationToken cancellationToken = default)
        => _inner.WaitForResultAsync(cancellationToken);

    public IAsyncEnumerator<AssistantMessageEvent> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
        => _inner.GetAsyncEnumerator(cancellationToken);

    private void Time(AssistantMessage? message)
    {
        if (message is null || _inner.IsEnded || message.DurationMs is not null)
        {
            return;
        }

        if (message.Timestamp < _startedAt)
        {
            return;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - _startedAtMonotonic) * 1000.0
            / Stopwatch.Frequency;
        message.DurationMs = (int)Math.Min(int.MaxValue, Math.Max(0, Math.Round(elapsedMs)));
    }
}
