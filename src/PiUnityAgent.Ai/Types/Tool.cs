using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Types;

/// <summary>A model-callable tool declaration.</summary>
public sealed record Tool
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>JSON Schema object for the tool parameters.</summary>
    public required JsonObject Parameters { get; init; }

    /// <summary>Optional constrained-sampling configuration.</summary>
    public ConstrainedSampling? ConstrainedSampling { get; init; }
}

/// <summary>Reference to a tool by name (used when removing tools).</summary>
public sealed record ToolReference
{
    public required string Name { get; init; }
}
