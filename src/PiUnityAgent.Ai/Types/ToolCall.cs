using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Types;

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCall : ContentBlock
{
    public override string Type => "toolCall";

    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Empty when the provider sent malformed JSON; see ArgumentsError.</summary>
    public JsonObject Arguments { get; init; } = new();

    /// <summary>Provider-specific thought signature (for example Google).</summary>
    public string? ThoughtSignature { get; init; }

    /// <summary>OpenAI Responses namespace for namespaced tools.</summary>
    public string? Namespace { get; init; }

    /// <summary>Set when the model's argument text was not a valid JSON object.</summary>
    public string? ArgumentsError { get; init; }
}
