namespace PiUnityAgent.Ai.Types;

/// <summary>System prompt message. Later system messages patch sections or the tool set.</summary>
public sealed record SystemMessage : Message
{
    public override string Role => "system";

    /// <summary>Instruction text. Mutually exclusive with ContentBlocks.</summary>
    public string? Content { get; init; }

    /// <summary>Instruction text as blocks. Mutually exclusive with Content.</summary>
    public IReadOnlyList<TextContent>? ContentBlocks { get; init; }

    /// <summary>Named prompt sections; null removes a section.</summary>
    public IReadOnlyDictionary<string, string?>? Sections { get; init; }

    /// <summary>Complete tool definitions that become available at this point.</summary>
    public IReadOnlyList<Tool>? ToolsAdded { get; init; }

    /// <summary>Tools that stop being available at this point.</summary>
    public IReadOnlyList<ToolReference>? ToolsRemoved { get; init; }
}
