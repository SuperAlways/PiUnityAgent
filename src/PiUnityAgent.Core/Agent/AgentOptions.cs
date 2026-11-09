using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>门面层 prepareNextTurnWithContext（context + signal）；pi AgentOptions 同名成员。</summary>
public delegate Task<AgentLoopTurnUpdate?> PrepareNextTurnWithContext(
    PrepareNextTurnContext context,
    CancellationToken signal);

/// <summary>pi PrepareNextTurnContext（extends AgentTurnContext，空扩展）；C# 用同字段独立 record。</summary>
public sealed record PrepareNextTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    AgentContext Context,
    IReadOnlyList<Message> NewMessages);

/// <summary>pi AgentOptions 的 C# 形状；全部可空，缺省走 pi 默认值。</summary>
public sealed record AgentOptions
{
    public AgentInitialState? InitialState { get; init; }

    public Func<IReadOnlyList<Message>, Task<IReadOnlyList<Message>>>? ConvertToLlm { get; init; }

    public Func<IReadOnlyList<Message>, CancellationToken, Task<IReadOnlyList<Message>>>? TransformContext { get; init; }

    public StreamFn? StreamFn { get; init; }

    public Func<string, Task<string?>>? GetApiKey { get; init; }

    public BeforeToolCall? BeforeToolCall { get; init; }
    public AfterToolCall? AfterToolCall { get; init; }
    public FinishTurn? FinishTurn { get; init; }
    public PrepareRequest? PrepareRequest { get; init; }

    /// <summary>pi AgentOptions.prepareNextTurn：门面层只收 signal（与 S7 loop 层只收 context 的同名回调不同）。</summary>
    public Func<CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; init; }

    public PrepareNextTurnWithContext? PrepareNextTurnWithContext { get; init; }

    public QueueMode SteeringMode { get; init; } = QueueMode.OneAtATime;
    public QueueMode FollowUpMode { get; init; } = QueueMode.OneAtATime;

    public string? SessionId { get; init; }
    public ThinkingBudgets? ThinkingBudgets { get; init; }
    public TransportKind? Transport { get; init; }
    public int? MaxRetryDelayMs { get; init; }
    public ToolExecutionMode? ToolExecution { get; init; }
    public ClientIdentity? Identity { get; init; }

    public Func<JsonNode?, ProviderModel, Task<JsonNode?>>? OnPayload { get; init; }
    public Func<ProviderResponse, ProviderModel, Task>? OnResponse { get; init; }
    public Func<JsonNode, ProviderModel, Task>? OnProviderStreamEvent { get; init; }
}
