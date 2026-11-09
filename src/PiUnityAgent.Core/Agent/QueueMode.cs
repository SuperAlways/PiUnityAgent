using System;
using System.Collections.Generic;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

/// <summary>队列排空模式。对应 pi types.ts:55 的 "all" | "one-at-a-time"。</summary>
public enum QueueMode
{
    /// <summary>一次排空全部排队消息。</summary>
    All,

    /// <summary>每次只投递最旧的一条，其余留给下个排空点。</summary>
    OneAtATime,
}

/// <summary>
/// pi agent.ts:149-180 PendingMessageQueue 的 C# 等价物。
/// 队列可被 Unity 主线程与循环续体跨线程访问，故内部加锁；pi 本身单线程无锁。
/// </summary>
internal sealed class PendingMessageQueue
{
    private readonly object _gate = new();
    private readonly List<Message> _messages = new();
    private QueueMode _mode;

    public PendingMessageQueue(QueueMode mode)
        => _mode = mode;

    public QueueMode Mode
    {
        get
        {
            lock (_gate) return _mode;
        }
        set
        {
            lock (_gate) _mode = value;
        }
    }

    public void Enqueue(Message message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        lock (_gate) _messages.Add(message);
    }

    public bool HasItems()
    {
        lock (_gate) return _messages.Count > 0;
    }

    /// <summary>预览将被选取的消息，不消费。</summary>
    public IReadOnlyList<Message> Peek()
    {
        lock (_gate) return Select();
    }

    /// <summary>排空并移除被选取的消息。</summary>
    public IReadOnlyList<Message> Drain()
    {
        lock (_gate)
        {
            var drained = Select();
            if (drained.Count > 0) _messages.RemoveRange(0, drained.Count);
            return drained;
        }
    }

    public void Clear()
    {
        lock (_gate) _messages.Clear();
    }

    private IReadOnlyList<Message> Select()
    {
        if (_mode == QueueMode.All) return _messages.ToArray();
        return _messages.Count > 0 ? new[] { _messages[0] } : Array.Empty<Message>();
    }
}
