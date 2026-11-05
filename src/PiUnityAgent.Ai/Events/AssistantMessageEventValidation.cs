using System;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Events;

public static class AssistantMessageEventValidation
{
    public static void Validate(AssistantMessageEvent @event)
    {
        switch (@event)
        {
            case AssistantMessageEvent.Done done
                when done.Reason is not (StopReason.Stop or StopReason.Length
                    or StopReason.ToolUse or StopReason.Deferred):
                throw new ArgumentException(
                    $"Done event requires stop/length/toolUse/deferred, got {done.Reason}.",
                    nameof(@event));

            case AssistantMessageEvent.Error error
                when error.Reason is not (StopReason.Error or StopReason.Aborted):
                throw new ArgumentException(
                    $"Error event requires error/aborted, got {error.Reason}.",
                    nameof(@event));
        }
    }
}
