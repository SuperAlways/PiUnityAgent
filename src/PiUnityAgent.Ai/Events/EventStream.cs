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

    /// <summary>流是否已收场（终态事件 / <see cref="End"/> / <see cref="Fail"/> / 回调异常之后为 true）。</summary>
    public bool IsEnded => _ended;

    /// <summary>
    /// 入队一个事件；命中终态判定时封流并落定 <see cref="WaitForResultAsync"/> 的结果。
    /// 单生产者契约：<c>_ended</c> 的 check-then-act 不是原子操作，并发 <c>Push</c>
    /// 可能让多个事件都走到终态处理，多生产者并发调用不受支持。
    /// 若终态判定或结果提取回调抛出异常，流按失败收场：通道以该异常完成
    /// （该事件可能已入队、对消费者可见），<see cref="WaitForResultAsync"/> 抛出原始异常。
    /// </summary>
    public void Push(TEvent @event)
    {
        if (_ended)
        {
            return;
        }

        _channel.Writer.TryWrite(@event);
        try
        {
            if (_isComplete(@event))
            {
                var result = _extractResult(@event);
                _ended = true;
                _channel.Writer.TryComplete();
                _completion.TrySetResult(result);
            }
        }
        catch (Exception ex)
        {
            _ended = true;
            _channel.Writer.TryComplete(ex);
            _completion.TrySetException(ex);
        }
    }

    /// <summary>在不推送终态事件的前提下收场，结果即 <paramref name="result"/>。</summary>
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

    /// <summary>
    /// 以异常收场，仅限基础设施错误；模型错误应编码为事件推送（如 AssistantMessageEvent.Error），
    /// 不走本方法。迭代侧消费完已入队事件后抛出原始异常（Channels 完成错误不包
    /// <see cref="ChannelClosedException"/>，经 <c>ExceptionDispatchInfo.Throw</c> 原样抛出），
    /// <see cref="WaitForResultAsync"/> 同样抛出原始异常。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> 为 null。</exception>
    public void Fail(Exception error)
    {
        if (error is null)
        {
            throw new ArgumentNullException(nameof(error));
        }

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
