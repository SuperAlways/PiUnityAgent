using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

public sealed partial class Agent
{
    /// <summary>steering 队列的排空模式。</summary>
    public QueueMode SteeringMode
    {
        get => _steeringQueue.Mode;
        set => _steeringQueue.Mode = value;
    }

    /// <summary>follow-up 队列的排空模式。</summary>
    public QueueMode FollowUpMode
    {
        get => _followUpQueue.Mode;
        set => _followUpQueue.Mode = value;
    }

    /// <summary>排队一条消息：在当前助手回合结束后注入。</summary>
    public void Steer(Message message) => _steeringQueue.Enqueue(message);

    /// <summary>排队一条消息：仅在 agent 本应停止时运行。</summary>
    public void FollowUp(Message message) => _followUpQueue.Enqueue(message);

    public void ClearSteeringQueue() => _steeringQueue.Clear();

    public void ClearFollowUpQueue() => _followUpQueue.Clear();

    public void ClearAllQueues()
    {
        ClearSteeringQueue();
        ClearFollowUpQueue();
    }

    public bool HasQueuedMessages()
        => _steeringQueue.HasItems() || _followUpQueue.HasItems();

    /// <summary>预览下一回合将选取的消息（steering 优先）。</summary>
    public IReadOnlyList<Message> PeekQueuedMessages()
    {
        var steering = _steeringQueue.Peek();
        return steering.Count > 0 ? steering : _followUpQueue.Peek();
    }

    /// <summary>
    /// 从当前 transcript 续跑。最后一条须为 user 或 toolResult；
    /// 最后一条为 assistant 时先排空 steering，再排空 follow-up。
    /// </summary>
    public async Task Continue()
    {
        if (HasActiveRun())
        {
            throw new InvalidOperationException(
                "Agent is already processing. Wait for completion before continuing.");
        }

        var messages = _state.Messages;
        var lastMessage = messages.Count > 0 ? messages[messages.Count - 1] : null;
        if (lastMessage is null || messages.All(message => message is SystemMessage))
        {
            throw new InvalidOperationException("No messages to continue from");
        }

        if (lastMessage is AssistantMessage)
        {
            var steering = _steeringQueue.Drain();
            if (steering.Count > 0)
            {
                await RunPromptMessages(steering, skipInitialSteeringPoll: true)
                    .ConfigureAwait(false);
                return;
            }

            var followUps = _followUpQueue.Drain();
            if (followUps.Count > 0)
            {
                await RunPromptMessages(followUps).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("Cannot continue from message role: assistant");
        }

        await RunContinuation().ConfigureAwait(false);
    }
}
