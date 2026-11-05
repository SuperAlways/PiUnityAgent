using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed record Provider
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<Model> Models { get; init; }
    public required StreamFn Stream { get; init; }
}
