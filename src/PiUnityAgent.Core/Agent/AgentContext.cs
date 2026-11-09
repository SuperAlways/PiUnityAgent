using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public sealed record AgentContext
{
    public required List<Message> Messages { get; init; }
    public IReadOnlyList<AgentTool>? Tools { get; init; }
}
