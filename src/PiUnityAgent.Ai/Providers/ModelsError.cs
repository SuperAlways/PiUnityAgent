using System;

namespace PiUnityAgent.Ai.Providers;

public enum ModelsErrorCode
{
    ModelSource,
    ModelValidation,
    Provider,
    Stream,
    Auth,
    OAuth
}

public sealed class ModelsError : Exception
{
    public ModelsError(ModelsErrorCode code, string message, Exception? cause = null)
        : base(WithCauseDetail(message, cause), cause)
    {
        Code = code;
    }

    public ModelsErrorCode Code { get; }

    private static string WithCauseDetail(string message, Exception? cause)
    {
        if (cause is null) return message;
        var detail = cause.Message.Trim();
        if (detail.Length == 0 || message.Contains(detail, StringComparison.Ordinal)) return message;
        return $"{message}: {detail}";
    }
}
