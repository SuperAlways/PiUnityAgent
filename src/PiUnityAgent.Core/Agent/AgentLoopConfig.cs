using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public sealed record AgentLoopConfig
{
    public required Model Model { get; init; }
    public required Func<IReadOnlyList<Message>, Task<IReadOnlyList<Message>>> ConvertToLlm { get; init; }
    public Func<IReadOnlyList<Message>, CancellationToken, Task<IReadOnlyList<Message>>>? TransformContext { get; init; }
    public Func<string, CancellationToken, Task<string?>>? GetApiKey { get; init; }
    public FinishTurn? FinishTurn { get; init; }
    public PrepareRequest? PrepareRequest { get; init; }
    public PrepareNextTurn? PrepareNextTurn { get; init; }
    public Func<Task<IReadOnlyList<Message>>>? GetSteeringMessages { get; init; }
    public Func<Task<IReadOnlyList<Message>>>? GetFollowUpMessages { get; init; }
    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;
    public BeforeToolCall? BeforeToolCall { get; init; }
    public AfterToolCall? AfterToolCall { get; init; }
    public SimpleStreamOptions? StreamOptions { get; init; }
    public ClientIdentity? Identity { get; init; }
}
