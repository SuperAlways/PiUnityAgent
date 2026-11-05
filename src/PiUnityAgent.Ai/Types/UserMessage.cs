namespace PiUnityAgent.Ai.Types;

/// <summary>A user message. Content is text or text/image blocks.</summary>
public sealed record UserMessage : Message
{
    public override string Role => "user";

    /// <summary>Text or text/image blocks.</summary>
    public required MessageContent Content { get; init; }
}
