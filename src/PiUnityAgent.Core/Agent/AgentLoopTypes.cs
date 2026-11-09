using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

public enum AgentTurnDecision { Continue, End }

public sealed record AgentTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    AgentContext Context,
    IReadOnlyList<Message> NewMessages);

public sealed record PrepareRequestContext(AgentContext Context, Model Model, ModelThinkingLevel ThinkingLevel);

public sealed record AgentLoopTurnUpdate
{
    public AgentContext? Context { get; init; }
    public IReadOnlyList<Message>? Messages { get; init; }
    public Model? Model { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
}

public sealed record AgentRequestUpdate
{
    public AgentContext? Context { get; init; }
    public Model? Model { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
}

public delegate Task<AgentTurnDecision?> FinishTurn(AgentTurnContext turn, CancellationToken signal);
public delegate Task<AgentRequestUpdate?> PrepareRequest(PrepareRequestContext request, CancellationToken signal);
public delegate Task<AgentLoopTurnUpdate?> PrepareNextTurn(AgentTurnContext turn);
