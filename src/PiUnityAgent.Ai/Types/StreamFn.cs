using PiUnityAgent.Ai.Events;

namespace PiUnityAgent.Ai.Types;

public delegate Task<IAssistantMessageEventStream> StreamFn(
    Model model,
    TranscriptContext context,
    SimpleStreamOptions? options,
    CancellationToken cancellationToken);
