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

    /// <summary>
    /// 工厂辅助：<c>id</c> 可省（spec §6.4）；省略时 Id 置空串，
    /// 由 FauxProvider 发射前按 provider 单调计数器规范化为 <c>tool:{n}</c>（spec §6.7）。
    /// </summary>
    public static ToolCall ToolCall(string name, JsonObject arguments, string? id = null)
        => new() { Id = id ?? string.Empty, Name = name, Arguments = arguments };

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
    public ModelInputLimits? InputLimits { get; init; }
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
    private int _deferredFetchCount;
    private long _toolCallIndex;
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>
    /// D3 形状锚点：延迟取回计数仅保形状，计数递增随 S8 Deferred 行为一起落地。
    /// </summary>
    public int DeferredFetchCount => Volatile.Read(ref _deferredFetchCount);

    /// <summary>
    /// D3 形状锚点：已取消的延迟句柄收集列表仅保形状，填充随 S8 一起落地。
    /// </summary>
    public List<DeferredHandle> CancelledDeferred { get; } = new();

    internal void IncrementCallCount() => Interlocked.Increment(ref _callCount);
    internal void IncrementDeferredFetchCount() => Interlocked.Increment(ref _deferredFetchCount);

    /// <summary>
    /// Provider 级单调计数器，支撑确定性 ToolCall id <c>tool:{n}</c>（spec §6.7：
    /// 不用 DateTime.Now/Random）。
    /// </summary>
    internal long NextToolCallIndex() => Interlocked.Increment(ref _toolCallIndex);
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
