namespace PiUnityAgent.Ai.Types;

/// <summary>A user message. Content is text or text/image blocks.</summary>
public sealed record UserMessage : Message
{
    public override string Role => "user";

    /// <summary>Plain text content. Mutually exclusive with ContentBlocks.</summary>
    public string? Content { get; init; }

    /// <summary>Text and/or image blocks. Mutually exclusive with Content.</summary>
    public IReadOnlyList<ContentBlock>? ContentBlocks { get; init; }
}
