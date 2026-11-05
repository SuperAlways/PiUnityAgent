using System;

namespace PiUnityAgent.Ai.Types;

/// <summary>Why a model response ended. Wire values match pi.</summary>
public enum StopReason
{
    Pending,
    Stop,
    Length,
    ToolUse,
    Error,
    Aborted,
    Deferred,
}

/// <summary>Wire-name conversion for <see cref="StopReason"/>.</summary>
public static class StopReasonExtensions
{
    public static string ToWire(this StopReason reason) => reason switch
    {
        StopReason.Pending => "pending",
        StopReason.Stop => "stop",
        StopReason.Length => "length",
        StopReason.ToolUse => "toolUse",
        StopReason.Error => "error",
        StopReason.Aborted => "aborted",
        StopReason.Deferred => "deferred",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown stop reason."),
    };

    public static StopReason FromWire(string wire) => wire switch
    {
        "pending" => StopReason.Pending,
        "stop" => StopReason.Stop,
        "length" => StopReason.Length,
        "toolUse" => StopReason.ToolUse,
        "error" => StopReason.Error,
        "aborted" => StopReason.Aborted,
        "deferred" => StopReason.Deferred,
        _ => throw new ArgumentOutOfRangeException(nameof(wire), wire, "Unknown stop reason wire value."),
    };
}
