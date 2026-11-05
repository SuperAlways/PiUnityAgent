namespace PiUnityAgent.Ai.Types;

public enum StrictMode { Prefer, Require }

public enum GrammarFormat { OpenAiLark, OpenAiRegex }

public abstract record ConstrainedSamplingConfig
{
    public sealed record JsonSchema(StrictMode Strict) : ConstrainedSamplingConfig;
    public sealed record Grammar(IReadOnlyDictionary<GrammarFormat, string> Variants) : ConstrainedSamplingConfig;
}

public abstract record ConstrainedSampling
{
    public sealed record Disabled : ConstrainedSampling;
    public sealed record Config(ConstrainedSamplingConfig Value) : ConstrainedSampling;
}
