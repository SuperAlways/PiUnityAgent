using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>旧 S3 初版形状，仅用于 Task 7 过渡；Task 10 删除。</summary>
public delegate Task<IAssistantMessageEventStream> LegacyStreamFn(
    Model model,
    TranscriptContext context,
    SimpleStreamOptions? options,
    CancellationToken cancellationToken);

public sealed record Provider
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<Model> Models { get; init; }
    public required LegacyStreamFn Stream { get; init; }
}
