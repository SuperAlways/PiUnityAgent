using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public static class Faux
{
    public const string DefaultApi = "faux";
    public const string DefaultProvider = "faux";
    public const string DefaultModelId = "faux-1";
    public const string DefaultModelName = "Faux Model";
    public const string DefaultBaseUrl = "http://localhost:0";

    public static TextContent Text(string text) => new() { Text = text };

    public static ThinkingContent Thinking(string thinking) => new() { Thinking = thinking };

    public static ToolCall ToolCall(string name, JsonObject arguments, string id)
        => new() { Id = id, Name = name, Arguments = arguments };

    public static AssistantMessage AssistantMessage(
        string content,
        StopReason stopReason = StopReason.Stop,
        string? errorMessage = null,
        string? responseId = null,
        long timestamp = 0)
        => AssistantMessage(new ContentBlock[] { Text(content) }, stopReason, errorMessage, responseId, timestamp);

    public static AssistantMessage AssistantMessage(
        ContentBlock content,
        StopReason stopReason = StopReason.Stop,
        string? errorMessage = null,
        string? responseId = null,
        long timestamp = 0)
        => AssistantMessage(new[] { content }, stopReason, errorMessage, responseId, timestamp);

    public static AssistantMessage AssistantMessage(
        IReadOnlyList<ContentBlock> content,
        StopReason stopReason = StopReason.Stop,
        string? errorMessage = null,
        string? responseId = null,
        long timestamp = 0)
        => new()
        {
            Content = content,
            Api = DefaultApi,
            Provider = DefaultProvider,
            Model = DefaultModelId,
            Usage = new Usage(),
            StopReason = stopReason,
            ErrorMessage = errorMessage,
            ResponseId = responseId,
            Timestamp = timestamp,
        };
}

public sealed record FauxModelDefinition
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public bool Reasoning { get; init; }
    public IReadOnlyList<ModelInputModality>? Input { get; init; }
    public ModelCost? Cost { get; init; }
    public int ContextWindow { get; init; } = 128000;
    public int MaxTokens { get; init; } = 16384;
}

public sealed record FauxProviderOptions
{
    public string Api { get; init; } = Faux.DefaultApi;
    public string Provider { get; init; } = Faux.DefaultProvider;
    public IReadOnlyList<FauxModelDefinition>? Models { get; init; }
    public double TokensPerSecond { get; init; }
    public int MinTokenSize { get; init; } = 3;
    public int MaxTokenSize { get; init; } = 5;
    public IClock? Clock { get; init; }
}

public sealed class FauxProviderState
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);
    internal void IncrementCallCount() => Interlocked.Increment(ref _callCount);
}

public delegate Task<AssistantMessage> FauxResponseFactory(
    TranscriptContext context,
    SimpleStreamOptions? options,
    FauxProviderState state,
    Model model);

public abstract record FauxResponseStep
{
    public sealed record Response(AssistantMessage Message) : FauxResponseStep;

    // 简报原稿为 `record Factory(FauxResponseFactory Factory)`；位置参数名 Factory 与基类
    // 嵌套类型成员 FauxResponseStep.Factory 同名（CS8866），而嵌套类型内部又禁止同名成员
    // （CS0542）——属性无法叫 Factory。故委托属性改名为 Create；类型名与位置构造
    // new FauxResponseStep.Factory(fn) 保持原样，计划文档中 factory.Factory(...) 调用点
    // 相应读作 factory.Create(...)。
    public sealed record Factory(FauxResponseFactory Create) : FauxResponseStep;
}
