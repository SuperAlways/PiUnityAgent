using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Events;

public interface IAssistantMessageEventStream : IAsyncEnumerable<AssistantMessageEvent>
{
    AssistantMessage? Partial { get; }

    Task<AssistantMessage> WaitForDoneAsync(CancellationToken cancellationToken = default);
}
