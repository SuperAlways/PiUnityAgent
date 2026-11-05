namespace PiUnityAgent.Ai.Types;

/// <summary>Base type for all messages the model can see.</summary>
public abstract record Message
{
    /// <summary>Wire role name: system, user, assistant, or toolResult.</summary>
    public abstract string Role { get; }

    /// <summary>Unix timestamp in milliseconds.</summary>
    public required long Timestamp { get; init; }
}
