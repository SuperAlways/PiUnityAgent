using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Types;

/// <summary>Base for classifier questions.</summary>
public abstract record ClassifierQuestion
{
    public abstract string Type { get; }

    public string Instructions { get; init; } = string.Empty;
}

public sealed record ClassifierChoiceQuestion : ClassifierQuestion
{
    public override string Type => "choice";

    public IReadOnlyDictionary<string, string> Criteria { get; init; } =
        new Dictionary<string, string>();
}

public sealed record ClassifierScoreQuestion : ClassifierQuestion
{
    public override string Type => "score";

    public IReadOnlyList<string> Criteria { get; init; } = Array.Empty<string>();
}

public sealed record ClassifierBoolCriteria
{
    public string True { get; init; } = string.Empty;

    public string False { get; init; } = string.Empty;
}

public sealed record ClassifierBoolQuestion : ClassifierQuestion
{
    public override string Type => "bool";

    public ClassifierBoolCriteria Criteria { get; init; } = new();
}

/// <summary>Input for the classifier API.</summary>
public sealed record ClassifierContext
{
    public JsonObject State { get; init; } = new();

    public IReadOnlyList<ImageContent>? Images { get; init; }

    public IReadOnlyDictionary<string, ClassifierQuestion> Questions { get; init; } =
        new Dictionary<string, ClassifierQuestion>();
}

/// <summary>Base for classifier answers.</summary>
public abstract record ClassifierAnswer
{
    public abstract string Type { get; }
}

public sealed record ClassifierChoiceAnswer : ClassifierAnswer
{
    public override string Type => "choice";

    public string Choice { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, double> Probabilities { get; init; } =
        new Dictionary<string, double>();

    public double Confidence { get; init; }
}

public sealed record ClassifierScoreAnswer : ClassifierAnswer
{
    public override string Type => "score";

    public double Score { get; init; }

    public double Confidence { get; init; }
}

public sealed record ClassifierBoolAnswer : ClassifierAnswer
{
    public override string Type => "bool";

    public double Probability { get; init; }
}

/// <summary>Why a classifier response ended. Wire values match pi.</summary>
public enum ClassifierStopReason
{
    Stop,
    Error,
    Aborted,
}

/// <summary>Final classifier result.</summary>
public sealed record ClassifierResult
{
    public string Api { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, ClassifierAnswer> Answers { get; init; } =
        new Dictionary<string, ClassifierAnswer>();

    public Usage? Usage { get; init; }

    public ClassifierStopReason StopReason { get; init; }

    public string? ErrorMessage { get; init; }

    public long Timestamp { get; init; }
}
