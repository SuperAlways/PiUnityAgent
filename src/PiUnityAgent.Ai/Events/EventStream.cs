using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Events;

/// <summary>
/// 可推送的异步事件流：生产者 Push/End，单消费者按序迭代，结束后取终值。
/// 对应 pi EventStream；单消费者语义，不承诺广播。
/// </summary>
public sealed class EventStream<TEvent, TResult> : IAsyncEnumerable<TEvent>
    where TEvent : notnull
{
    private readonly Channel<TEvent> _channel;
    private readonly Func<TEvent, bool> _isComplete;
    private readonly Func<TEvent, TResult> _extractResult;
    private readonly TaskCompletionSource<TResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _ended;

    public EventStream(Func<TEvent, bool> isComplete, Func<TEvent, TResult> extractResult)
    {
        _isComplete = isComplete;
        _extractResult = extractResult;
        _channel = Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool IsEnded => _ended;

    public void Push(TEvent @event)
    {
        if (_ended)
        {
            return;
        }

        _channel.Writer.TryWrite(@event);
        if (_isComplete(@event))
        {
            _ended = true;
            _channel.Writer.TryComplete();
            _completion.TrySetResult(_extractResult(@event));
        }
    }

    public void End(TResult result)
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        _channel.Writer.TryComplete();
        _completion.TrySetResult(result);
    }

    public void Fail(Exception error)
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        _channel.Writer.TryComplete(error);
        _completion.TrySetException(error);
    }

    public async Task<TResult> WaitForResultAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            var cancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult(true));
            var completed = await Task.WhenAny(_completion.Task, cancelled.Task).ConfigureAwait(false);
            if (completed == cancelled.Task)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return await _completion.Task.ConfigureAwait(false);
    }

    public async IAsyncEnumerator<TEvent> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var @event))
            {
                yield return @event;
            }
        }
    }
}
