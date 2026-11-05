using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Types;

/// <summary>A model-callable tool declaration.</summary>
public sealed record Tool
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>JSON Schema object for the tool parameters.</summary>
    public JsonObject Parameters { get; init; } = new();

    /// <summary>Optional constrained-sampling configuration; false when unsupported.</summary>
    public JsonNode? ConstrainedSampling { get; init; }
}

/// <summary>Reference to a tool by name (used when removing tools).</summary>
public sealed record ToolReference
{
    public string Name { get; init; } = string.Empty;
}
