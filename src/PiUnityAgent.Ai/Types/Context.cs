namespace PiUnityAgent.Ai.Types;

/// <summary>Raw request input accepted by public stream entry points.</summary>
public sealed record Context
{
    public string? SystemPrompt { get; init; }

    public required IReadOnlyList<Message> Messages { get; init; }

    public IReadOnlyList<Tool>? Tools { get; init; }
}

/// <summary>
/// Normalized request context. System prompt and tool declarations are carried
/// by system messages; only normalizeContext should produce this shape.
/// </summary>
public sealed record TranscriptContext
{
    public required IReadOnlyList<Message> Messages { get; init; }
}
