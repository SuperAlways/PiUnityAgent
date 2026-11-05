using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Types;

/// <summary>Redacted error information attached to an assistant message diagnostic.</summary>
public sealed record DiagnosticErrorInfo
{
    public string? Name { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? Stack { get; init; }

    /// <summary>Provider/runtime error code; JSON string or number.</summary>
    public JsonNode? Code { get; init; }
}

/// <summary>Redacted provider/runtime diagnostic for failures and recoveries.</summary>
public sealed record AssistantMessageDiagnostic
{
    public string Type { get; init; } = string.Empty;

    public long Timestamp { get; init; }

    public DiagnosticErrorInfo? Error { get; init; }

    public JsonObject? Details { get; init; }
}
