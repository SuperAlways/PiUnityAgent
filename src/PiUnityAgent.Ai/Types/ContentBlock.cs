namespace PiUnityAgent.Ai.Types;

/// <summary>Base type for content blocks carried by messages.</summary>
public abstract record ContentBlock
{
    /// <summary>Wire discriminator, matching pi's content block type strings.</summary>
    public abstract string Type { get; }
}
