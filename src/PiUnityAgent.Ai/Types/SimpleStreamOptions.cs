namespace PiUnityAgent.Ai.Types;

public sealed record SimpleStreamOptions : StreamOptions
{
    public ToolChoice? ToolChoice { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
    public DeferredRequest? Deferred { get; init; }
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}
