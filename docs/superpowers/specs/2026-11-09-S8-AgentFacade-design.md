# S8 Agent 门面设计 spec（2026-11-09）

> **状态**：v1 设计完成（2026-11-09，待用户 review；未实现）。
> **范围**：S8 = `Agent` 有状态门面：持有 transcript、发出生命周期事件、双队列（steer/followUp + `QueueMode`）、abort、状态订阅与运行生命周期；同时承接台账 **D3**（Deferred stream/fetch/cancel）、**D17**（助手轮次重试公共模块）、**D23**（QueueMode）。不做 `proxy.ts`（D22）、图片/分类（D4/D5）、S9 上下文组装、S10 会话树。
> **参考版本**：pi `v1.5.0` / `f9a263655`；pi.net `main` / `2d6757b`；UnityLlmTransport `4c0cc4b` / `0.6.0`（仅通讯事实，S8 不引用）。
> **上位规则**：[`docs/03-开发方法-拆解学习与移植约定.md`](../../03-开发方法-拆解学习与移植约定.md) §1.3；[`docs/05-教学阶段路线.md`](../../05-教学阶段路线.md) §S8；[`docs/SOP/阶段偏离审查-SOP-2026-11-08.md`](../../SOP/阶段偏离审查-SOP-2026-11-08.md)。
> **前置**：S7 已合 main（`a6fc807`）：`AgentLoop` 双层循环、10 类 `AgentEvent`、钩子调用点、`DefaultStreamFn`、`AgentToolCallOutcome` 已就位；S6 工具契约；S3 `IDeferredProvider`/`ProviderStreams`/`DeferredHandle` 形状。
> **目标**：Faux 驱动下 `Agent` 可无网跑通 prompt / continue / steer / followUp / abort / 订阅 / 状态；D3/D17/D23 转 `已完成`，M1（S0–S8）收口。

---

## 0. 背景

S7 只做了低层循环：入口 `AgentLoop.Loop/Continue/RunAsync/RunContinueAsync` 在给定 `AgentContext` + `AgentLoopConfig` 上跑完双层循环，本身不持有 transcript、不提供队列存储、不管运行生命周期。pi 把这层放在 `packages/agent/src/agent.ts`（623 行）的 `Agent` 类：

- 有状态的 transcript / model / thinkingLevel / tools；
- steering 与 follow-up 双队列及其排空模式；
- 单飞运行（`activeRun`）、abort、`waitForIdle`、`reset`；
- 事件订阅与状态归约（`processEvents`）；
- 把 `AgentOptions` 的钩子/流选项组装成 `AgentLoopConfig`。

S7 独立审计的 U-9 已把 `QueueMode` 登记为 **D23**（目标 S8）；S3 台账把 **D3**、S4 把 **D17** 也排到 S8。因此 S8 是 M1 的收口阶段：门面 + 三个后置能力一次到位。

### 0.1 方案取舍

| 方案 | 内容 | 取舍 |
|---|---|---|
| **A（采用）** | `Agent` 门面 + 独立 `AgentState`/`AgentOptions`/`QueueMode` 文件；状态按 pi `AgentState` 对象暴露；D3/D17/D23 同阶段落地 | 文件聚焦、形状与 pi 逐项对齐；代价=S8 是 M1 最大的一批收尾工作 |
| B | 沿用 pi.net 的扁平写法（`Agent.Model/Messages/Tools` 直接挂 Agent，无 `AgentState`/`InitialState`） | 少一层间接，但 `AgentState`/`AgentInitialState` 是 pi 公共形状，扁平化=Shape gap；否决 |
| C | 门面先做，D3/D17 再拆两个阶段 | 违背台账目标阶段与 M1 收口；阶段门禁会挡住；否决 |

---

## 1. 目标 / 非目标

### 1.1 目标

- Core 新增 `Agent` 门面公共 API：`State`、`Subscribe`、`Steer`/`FollowUp` 及清空/预览、`Signal`/`Abort`/`WaitForIdle`/`Reset`、`Prompt`/`Continue`；
- 新增 `AgentState`、`AgentInitialState`、`AgentOptions`、`QueueMode`、`PrepareNextTurnContext`/`PrepareNextTurnWithContext`；
- 双队列存储 + `QueueMode` 排空语义（D23），接 S7 的 `GetSteeringMessages`/`GetFollowUpMessages` 排空点；
- 运行生命周期：单飞、abort、`waitForIdle`（等 `agent_end` listener 结算）、失败合成长度完整的事件序列；
- 事件订阅与 `processEvents` 状态归约（streamingMessage / pendingToolCalls / errorMessage / messages）；
- 钩子/流选项从 `AgentOptions` 全量组装进 `AgentLoopConfig`（S7 形状零回改）；
- **D3 承接**：`Models.StreamDeferred/FetchDeferredAsync/CancelDeferredAsync` 真实现；`ProviderFactory`/`StaticProvider` 暴露 deferred 分派；Faux 补 deferred 全行为；`DeferredRequest.Enabled.Window` 校验；
- **D17 承接**：`Ai/Utils/Retry.cs` 公共模块（分类/退避/取消/回调）+ 验收测试；不接入 Agent 循环；
- Core/Ai 单测全绿；D3/D17/D23 台账转 `已完成`。

### 1.2 非目标

- `proxy.ts` 服务端代理流（D22，后续扩展）；
- 图片/分类 provider（D4/D5，2B）；
- Telemetry（D8，后续扩展）；S10 会话持久化、S11 压缩、S12 扩展/资源；
- system prompt 组装与 `getCurrentTools`/`collapseSystemMessages` 的**公开**重放辅助（D11/S9）；S8 只在 Core internal 实现 Agent 自身所需的重放子集；
- 真实 provider 的 deferred 实现：pi v1.5.0 只有 `faux.ts` 导出 `fetchDeferred`/`cancelDeferred`（`grep` 确认），S8 验收走 Faux；
- 在 Agent 循环内自动重试助手轮（D17 只做公共模块，调用方是 2A 的 coding-agent/compaction）；
- Unity 宿主与真实网络（S13）；S8 全部测试走 Faux/Mock。

---

## 2. 工程落点与依赖

```
PiUnityAgent.Core（引用 Ai）
└─ Agent/
   ├─ Agent.cs                 （门面：状态/队列/生命周期/订阅/config 组装）
   ├─ AgentState.cs            （AgentState + AgentInitialState）
   ├─ AgentOptions.cs          （AgentOptions + PrepareNextTurnContext/WithContext）
   ├─ QueueMode.cs             （QueueMode 公共枚举 + internal PendingMessageQueue）
   └─ AgentTranscript.cs       （internal：初始 system 消息 + 当前 system 重放子集）

PiUnityAgent.Ai
├─ Utils/Retry.cs              （D17：RetryPolicy/RetryCallbacks/RetryDelayMs/IsRetryableAssistantError/RetryAssistantCall）
├─ Providers/Models.Capabilities.cs（D3：StreamDeferred/FetchDeferredAsync/CancelDeferredAsync 真实现）
├─ Providers/ProviderCapabilities.cs（D3：IDeferredProvider 增 SupportsFetchDeferred/SupportsCancelDeferred）
├─ Providers/ProviderFactory.cs（D3：CreatedProvider 实现 deferred 分派）
├─ Providers/StaticProvider.cs （D3：可选 deferred 委托；Faux / 宿主静态 provider 用）
├─ Providers/Faux/Faux.cs      （D3：FauxDeferredOptions + 状态计数）
├─ Providers/Faux/FauxProvider.cs（D3：deferred 响应存储/fetch/cancel/请求侧句柄）
└─ Types/StreamOptionTypes.cs  （D3：DeferredRequest.Enabled.Window 构造校验）
```

测试：

```
tests/PiUnityAgent.Core.Tests/Agent/
├─ AgentStateTests.cs
├─ AgentQueueTests.cs
├─ AgentLifecycleTests.cs
├─ AgentFacadeTests.cs          （prompt/continue/subscribe/hooks/config）
└─ AgentThreadingTests.cs       （Unity 侧跨线程控制调用）

tests/PiUnityAgent.Ai.Tests/
├─ Providers/DeferredTests.cs   （D3 Models + Faux；替换现势钉 ModelsCapabilityTests.Deferred_*）
├─ Utils/RetryTests.cs          （D17）
└─ Types/DeferredWindowTests.cs （Window 校验）
```

- Core 只引用 Ai；Ai 不反向依赖 Core；无 UnityEngine/transport/HTTP/GameData。
- 不新增 NuGet。`System.Text.RegularExpressions`、`System.Threading`、`System.Threading.Tasks` 均在 netstandard2.1。
- S7 的 `AgentLoop`/`AgentLoopConfig`/`AgentEvent`/钩子 delegate 形状一律不改；S8 只新增门面层类型与 Ai 的 deferred/retry 实现。

---

## 3. 公共形状

### 3.1 QueueMode 与待处理队列

```csharp
/// <summary>队列排空模式。对应 pi types.ts:55 的 "all" | "one-at-a-time"。</summary>
public enum QueueMode
{
    /// <summary>一次排空全部排队消息。</summary>
    All,
    /// <summary>每次只投递最旧的一条，其余留给下个排空点。</summary>
    OneAtATime
}

/// <summary>pi agent.ts:149-180 PendingMessageQueue 的 C# 等价物；internal（pi 未导出）。</summary>
internal sealed class PendingMessageQueue
{
    public QueueMode Mode { get; set; }
    public void Enqueue(Message message);
    public bool HasItems();
    public IReadOnlyList<Message> Peek();   // All=快照全部；OneAtATime=首条
    public IReadOnlyList<Message> Drain();  // Peek 后移除已取部分
    public void Clear();
}
```

- 默认模式：`OneAtATime`（pi agent.ts:255-256）。
- 公开枚举名用 C# 命名，wire 无表示；映射登记 §6。

### 3.2 AgentInitialState / AgentState

```csharp
/// <summary>构造期初始状态；对应 pi AgentInitialState（AgentState 的可写子集）。</summary>
public sealed record AgentInitialState
{
    public string? SystemPrompt { get; init; }
    public Model? Model { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
    public IReadOnlyList<AgentTool>? Tools { get; init; }
    public IReadOnlyList<Message>? Messages { get; init; }
}

/// <summary>公开运行状态；对应 pi AgentState。读写面按 pi：tools/messages 赋值复制顶层数组。</summary>
public sealed class AgentState
{
    /// <summary>从 transcript 的 system 消息回读的当前 prompt（只读）。</summary>
    public string SystemPrompt { get; }

    public Model Model { get; set; }
    public ModelThinkingLevel ThinkingLevel { get; set; }

    public IReadOnlyList<AgentTool> Tools { get; set; }
    public IReadOnlyList<Message> Messages { get; set; }

    public bool IsStreaming { get; }
    public Message? StreamingMessage { get; }
    public IReadOnlyCollection<string> PendingToolCalls { get; }
    public string? ErrorMessage { get; }
}
```

- `SystemPrompt` 只读；改 prompt 走追加 system 消息（pi 注释语义）。
- `PendingToolCalls` 用 `IReadOnlyCollection<string>`：netstandard2.1 无 `IReadOnlySet<T>`（.NET 5 引入），映射登记 §6；返回值是快照。
- `Messages` getter 返回只读快照，不暴露内部可变 `List`；setter 复制输入。`Tools` 同。
- 默认模型 = pi `DEFAULT_MODEL`：`Id/Name/Api/Provider="unknown"`、`BaseUrl=""`、`ContextWindow=0`、`MaxTokens=0`、`Input=[]`、`Cost=0`。默认 thinking=`Off`。

### 3.3 AgentOptions

```csharp
public delegate Task<AgentLoopTurnUpdate?> PrepareNextTurnWithContext(
    PrepareNextTurnContext context,
    CancellationToken signal);

/// <summary>pi PrepareNextTurnContext（extends AgentTurnContext，空扩展）；C# 用同字段独立 record。</summary>
public sealed record PrepareNextTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    AgentContext Context,
    IReadOnlyList<Message> NewMessages);

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
    public PrepareNextTurn? PrepareNextTurn { get; init; }
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
```

- 除 `InitialState` 外，其余字段与 pi `AgentOptions` 一一对应；`onPayload/onResponse/onProviderStreamEvent` 采用 S3 `StreamOptions` 的 delegate 形状。
- `StreamFn` 可空：省略时回退 `DefaultStreamFn.Get()`（pi 构造器的运行期兜底；见 §6）。
- `PrepareNextTurn` 保持 S7 的 loop 形状（只带 context）；`PrepareNextTurnWithContext` 是门面新增入口（pi `AgentOptions.prepareNextTurnWithContext`），Agent 用闭包捕获当前 run 的 CancellationToken。
- `Transport` 省略=Auto；Agent 门面的公开字段默认 `TransportKind.Auto`（pi `"auto"`）。

### 3.4 Agent 门面

```csharp
public sealed class Agent
{
    public Agent(AgentOptions options);

    /// <summary>当前状态；对应 pi agent.state。</summary>
    public AgentState State { get; }

    /// <summary>按订阅顺序 await；返回取消订阅句柄。对应 pi subscribe。</summary>
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener);

    // 构造后可变选项（pi Agent 公开字段）
    public Func<IReadOnlyList<Message>, Task<IReadOnlyList<Message>>> ConvertToLlm { get; set; }
    public Func<IReadOnlyList<Message>, CancellationToken, Task<IReadOnlyList<Message>>>? TransformContext { get; set; }
    public StreamFn StreamFunction { get; set; }
    public Func<string, Task<string?>>? GetApiKey { get; set; }
    public BeforeToolCall? BeforeToolCall { get; set; }
    public AfterToolCall? AfterToolCall { get; set; }
    public FinishTurn? FinishTurn { get; set; }
    public PrepareRequest? PrepareRequest { get; set; }
    public PrepareNextTurn? PrepareNextTurn { get; set; }
    public PrepareNextTurnWithContext? PrepareNextTurnWithContext { get; set; }
    public string? SessionId { get; set; }
    public ThinkingBudgets? ThinkingBudgets { get; set; }
    public TransportKind Transport { get; set; }          // 默认 Auto
    public int? MaxRetryDelayMs { get; set; }
    public ToolExecutionMode ToolExecution { get; set; }  // 默认 Parallel
    public ClientIdentity? Identity { get; set; }
    public Func<JsonNode?, ProviderModel, Task<JsonNode?>>? OnPayload { get; set; }
    public Func<ProviderResponse, ProviderModel, Task>? OnResponse { get; set; }
    public Func<JsonNode, ProviderModel, Task>? OnProviderStreamEvent { get; set; }

    // 队列
    public QueueMode SteeringMode { get; set; }
    public QueueMode FollowUpMode { get; set; }
    public void Steer(Message message);
    public void FollowUp(Message message);
    public void ClearSteeringQueue();
    public void ClearFollowUpQueue();
    public void ClearAllQueues();
    public bool HasQueuedMessages();
    public IReadOnlyList<Message> PeekQueuedMessages();

    // 运行控制
    public CancellationToken? Signal { get; }
    public void Abort();
    public Task WaitForIdle();
    public void Reset();

    // 启动
    public Task Prompt(string input, IReadOnlyList<ImageContent>? images = null);
    public Task Prompt(Message message);
    public Task Prompt(IReadOnlyList<Message> messages);
    public Task Continue();
}
```

- `Prompt` 三个重载对应 pi `prompt(input: string | AgentMessage | AgentMessage[], images?)`（C# 无联合类型）。
- `Subscribe` 返回 `IDisposable`（pi 返回取消订阅函数）。
- 事件 listener 可能在非主线程执行；宿主（S13）负责 marshal 到 Unity 主线程（§4.10）。

### 3.5 D3 的 Ai 公共面

```csharp
public interface IDeferredProvider
{
    /// <summary>任一 api 实现导出 fetchDeferred 时为 true；pi 以方法存在性表达。</summary>
    bool SupportsFetchDeferred { get; }

    /// <summary>任一 api 实现导出 cancelDeferred 时为 true。</summary>
    bool SupportsCancelDeferred { get; }

    IAssistantMessageEventStream StreamDeferred(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options);

    Task<AssistantMessage> FetchDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options);

    /// <summary>取消；取消令牌从 options.CancellationToken 读取（与 S3 形状一致）。</summary>
    Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options);
}

public sealed partial class Models
{
    // S3 签名不变，S8 填实现
    public IAssistantMessageEventStream StreamDeferred(Model model, DeferredHandle handle, DeferredFetchOptions? options = null);
    public Task<AssistantMessage> FetchDeferredAsync(Model model, DeferredHandle handle, DeferredFetchOptions? options = null);
    public Task CancelDeferredAsync(Model model, DeferredHandle handle, DeferredCancelOptions? options = null);
}

public sealed record FauxDeferredOptions
{
    /// <summary>返回原 handle 的次数；之后一次取回返回最终结果。</summary>
    public int PendingFetches { get; init; }
    public int? PollAfterMs { get; init; }
}
```

- `FauxProviderOptions` 新增 `FauxDeferredOptions? Deferred { get; init; }`。
- `IDeferredProvider` 新增 `SupportsFetchDeferred`/`SupportsCancelDeferred`：C# 无法用"可选方法存在性"表达能力，参照 pi.net `SupportsFetchDeferred/SupportsCancelDeferred` 的布尔面（§6）。
- `ProviderStreams` 形状不变（S3 已有 `FetchDeferred`/`CancelDeferred` 委托）。

### 3.6 D17 Retry

```csharp
public sealed record RetryPolicy
{
    public bool Enabled { get; init; }
    /// <summary>最大重试次数（0=不重试）；首次调用不计入。</summary>
    public int MaxRetries { get; init; }
    public int BaseDelayMs { get; init; }
    /// <summary>单次退避上限；缺省 60s。</summary>
    public int? MaxAgentDelayMs { get; init; }
}

public sealed record RetryCallbacks
{
    public Func<int, int, int, string, Task>? OnRetryScheduled { get; init; }   // attempt, maxAttempts, delayMs, errorMessage
    public Func<Task>? OnRetryAttemptStart { get; init; }                       // 退避结束、重试调用前
    public Func<bool, int, string?, Task>? OnRetryFinished { get; init; }       // success, attempt, finalError
}

public static class Retry
{
    public const int DefaultMaxAgentRetryDelayMs = 60_000;

    /// <summary>base * 2^(attempt-1)，向上封顶 maxAgentDelayMs（缺省 60s）。</summary>
    public static int RetryDelayMs(int baseDelayMs, int? maxAgentDelayMs, int attempt);
    public static int RetryDelayMs(RetryPolicy policy, int attempt);

    /// <summary>按 pi 两组正则分类：先排不可重试配额/计费文案，再匹配瞬态错误。</summary>
    public static bool IsRetryableAssistantError(AssistantMessage message);

    /// <summary>单次助手调用 + 有界重试；取消/成功/耗尽都返回 AssistantMessage，不抛网络错。</summary>
    public static Task<AssistantMessage> RetryAssistantCall(
        Func<Task<AssistantMessage>> produce,
        RetryPolicy? policy,
        CancellationToken signal,
        RetryCallbacks? callbacks = null);
}
```

- 位置：`PiUnityAgent.Ai.Utils`；`Api/ProviderRetry.cs`（HTTP 请求级重试）保持不变，不合并。

---

## 4. 行为语义

### 4.1 初始化与 system 基线（`AgentTranscript` internal）

按 pi `utils/transcript.ts:10-100`：

1. 构造：
   - `Tools`/`Messages` 从 `InitialState` 复制（顶层数组）；
   - `createInitialSystemMessage(systemPrompt, tools.map(toToolDeclaration))`：仅当 `systemPrompt` 非空或 tools 非空时产生 `SystemMessage { Content = "" or prompt, ToolsAdded = declarations, Timestamp = 0 }`；
   - 若 `Messages[0]` 不是 `SystemMessage`，把头消息 unshift 到最前（`messages[0]?.role !== "system"`）。
2. `AgentState.SystemPrompt`：
   - 重放所有 system 消息：content 文本按 `contentText` 规则（text 块以 `\n` 连接）依次累积；后条 content 以 `\n\n` 追加；`Sections` 按 name 覆盖/删除；tools 用 `ToolLoadout.GetCurrentTools` 重放；
   - 渲染文本 = content + 非 null section 值，过滤空串后以 `\n\n` 连接（`getSystemMessageText`）；
   - 无 system 消息且无工具 → 空串。
3. `Reset`：
   - 有 activeRun → `InvalidOperationException("Agent is already processing. Wait for completion before resetting.")`；
   - `baseline = getCurrentSystemMessage(messages)`（全部 system 消息折叠成一条，不是取第一条）；`messages = baseline is null ? [] : [baseline]`；
   - 清 `isStreaming/streamingMessage/pendingToolCalls/errorMessage` 与两个队列。
- 本阶段的 `AgentTranscript` 是 Core internal：D11（`getCurrentSystemMessage/getCurrentTools/collapseSystemMessages` 公开）仍归 S9；S9 提升/迁移时按 D11 验收（§6 登记）。

### 4.2 运行生命周期（`runWithLifecycle`）

按 pi `agent.ts:517-566`：

1. 入口检查 `activeRun`：已在跑 → `InvalidOperationException("Agent is already processing.")`；
2. 新建 `CancellationTokenSource` 与 `TaskCompletionSource`（`RunContinuationsAsynchronously`）；写入 `activeRun`；
3. 置 `isStreaming=true`、`streamingMessage=null`、`errorMessage=null`；
4. 执行 `executor(signal)`：
   - `Prompt` → `AgentLoop.RunAsync(prompts, snapshot, config, ProcessEvents, signal, StreamFunction)`；
   - `Continue` → `AgentLoop.RunContinueAsync(snapshot, config, ProcessEvents, signal, StreamFunction)`；
5. `catch` → `handleRunFailure(error, aborted = cts.IsCancellationRequested)`；
6. `finally` → `finishRun()`：`isStreaming=false`、`streamingMessage=null`、`pendingToolCalls` 清空、TCS 置结果、`activeRun=null`。

**失败收场**（`handleRunFailure`，pi agent.ts:542-558）：合成一条 assistant 失败消息并依次 `processEvents`：

- 消息：`Content=[TextContent("")]`、`Api/Provider/Model` 取当前 state.model、`Usage=new Usage()`（含零成本）、`StopReason = aborted ? Aborted : Error`、`ErrorMessage = error.Message`、`Timestamp=now`；
- 事件顺序：`message_start` → `message_end` → `turn_end(message, [])` → `agent_end([message])`。

- `Signal`：无 run → null；有 run → `activeRun.Token`。
- `Abort()`：取消 activeRun 的 CTS；无 run 时 no-op。
- `WaitForIdle()`：无 run → `Task.CompletedTask`；有 run → activeRun 的 TCS Task。**注意**：`agent_end` 只是最后事件，idle 要等该事件的 listener 全部 await 完成（`finishRun` 在 listener 结算后才置位）。

### 4.3 事件归约与订阅（`processEvents`）

按 pi `agent.ts:575-622`：

1. 先归约状态，再 await listeners：
   - `message_start` → `streamingMessage = event.Message`；
   - `message_update` → `streamingMessage = event.Message`；
   - `message_end` → `streamingMessage = null`；`messages.Add(event.Message)`；
   - `tool_execution_start` → `pendingToolCalls` 加入 `event.ToolCallId`；
   - `tool_execution_end` → 移除；
   - `turn_end` → 若 `event.Message` 是 assistant 且 `ErrorMessage` 非空，记 `state.errorMessage`；
   - `agent_end` → `streamingMessage = null`；
   - 其余事件（`agent_start`/`turn_start`/`tool_execution_update`）不归约。
2. listener 调用：
   - 取当前 `activeRun` 的 token；无 run → `InvalidOperationException("Agent listener invoked outside active run")`；
   - 按订阅顺序 `await listener(event, signal)`；listener 异常向 run 传播（pi 同）。
3. `subscribe` 去重：同一 listener 重复订阅只保留一个（pi `Set` 语义），返回的 `IDisposable` 只移除该 listener。

### 4.4 双队列与 QueueMode（D23）

按 pi agent.ts:149-180, 289-342, 404-513：

- `PendingMessageQueue.Peek()`：`All` → 全部快照；`OneAtATime` → 仅首条；`Drain()` = Peek 后移除。
- `Steer`/`FollowUp` 入对应队列；`ClearAllQueues` 两个都清；`HasQueuedMessages` 任一非空；`PeekQueuedMessages` **steering 优先**：steering 非空返回 steering，否则返回 followUp。
- `createLoopConfig`：
  - `GetSteeringMessages`：`skipInitialSteeringPoll` 为 true 时首个轮询返回空并复位（用于 `Continue` 从 assistant 末条取 steering 的场景，避免同一回合双投递）；否则 `steeringQueue.Drain()`；
  - `GetFollowUpMessages`：`followUpQueue.Drain()`；
  - 模式字段 `SteeringMode`/`FollowUpMode` setter 直接改队列 Mode（运行中改变从下个排空点生效）。
- 默认 `OneAtATime`（D23 验收：one-at-a-time 不双投递 / all 全排空）。

### 4.5 Prompt / Continue

按 pi agent.ts:379-467：

- **Prompt**：
  - 已有 activeRun → `InvalidOperationException("Agent is already processing a prompt. Use steer() or followUp() to queue messages, or wait for completion.")`；
  - `string` 输入 → `UserMessage { Content = Blocks([TextContent(input), ...images]), Timestamp = now }`（无 images 时可只放文本；空 images 不追加）；
  - `Message` → 单条；`IReadOnlyList<Message>` → 原列表（pi 不复制）；
  - 调 `runPromptMessages`：快照 context（`Messages = state.messages.ToList()`、`Tools = state.tools.ToList()`），`config = createLoopConfig()`，进 `AgentLoop.RunAsync`。
- **Continue**：
  - 已有 activeRun → `InvalidOperationException("Agent is already processing. Wait for completion before continuing.")`；
  - 无消息或全部 system → `InvalidOperationException("No messages to continue from")`；
  - 末条是 assistant：
    - steering Drain 非空 → `runPromptMessages(steering, skipInitialSteeringPoll: true)`；
    - 否则 followUp Drain 非空 → `runPromptMessages(followUp)`；
    - 否则 `InvalidOperationException("Cannot continue from message role: assistant")`；
  - 末条非 assistant → `runContinuation`（`AgentLoop.RunContinueAsync`）。
- `runPromptMessages` 复用同一生命周期包装；两条路径都单飞。

### 4.6 钩子与 `AgentLoopConfig` 组装

按 pi agent.ts:476-515：

1. `model = state.model`；
2. `reasoning`：`state.thinkingLevel == Off ? null : ToThinkingLevel(value)`（复用 S7 `ApplyThinkingLevelUpdate` 的映射口径）；
3. 其余选项进 `SimpleStreamOptions`：`SessionId`、`OnPayload`、`OnResponse`、`OnProviderStreamEvent`、`Transport`、`ThinkingBudgets`、`MaxRetryDelayMs`；
4. `Identity` 直接给 config；
5. `ToolExecution`、`BeforeToolCall`、`AfterToolCall`、`FinishTurn`、`PrepareRequest`；
6. `ConvertToLlm`（缺省 = `defaultConvertToLlm`：保留 `Role in {system,user,assistant,toolResult}` 的消息；返回 `Task.FromResult`）；
7. `TransformContext`、`GetApiKey` 直接给 config（`GetApiKey` 适配为 S7 的 `(provider, ct)` 委托，忽略 ct，与 pi 无 signal 的签名一致）；
8. `PrepareNextTurn`：若 `PrepareNextTurnWithContext` 或 `PrepareNextTurn` 有值，构造一个 loop 级委托：
   - 先转为 `PrepareNextTurnContext`；
   - 有 WithContext → `await PrepareNextTurnWithContext(ctx, activeSignal)`；
   - 否则 → `await PrepareNextTurn(activeSignal)`（pi 的 `prepareNextTurn(this.signal)` 语义）；
   - 返回 `AgentLoopTurnUpdate?`。
9. 所有字段在每次 run 开始时读取（构造后可变字段的运行中改值从下一 run 生效，pi 同）。

### 4.7 D3 Deferred（Ai）

#### 4.7.1 Models 管线

按 pi `models.ts:937-990`：

- `StreamDeferred(model, handle, options)`：
  1. `LazyStream.Run(model, async factory)`（同步返回流；setup 失败为终态 Error）；
  2. factory 内：`provider = RequireProvider(model.Provider)`；未知 → `ModelsError(Provider, "Unknown provider: X")`；
  3. provider 不是 `IDeferredProvider` 或 `SupportsFetchDeferred == false` → `ModelsError(Provider, $"Provider {model.Provider} does not support deferred responses")`（**在 auth 之前**，pi 顺序）；
  4. `ResolveAuthForStreamAsync(model, options)` → `requestModel`（auth.BaseUrl 覆盖）→ `ApplyAuthOptions/ApplyIdentity/ApplyTransformHeaders`（保留 `DeferredFetchOptions.Wait`）→ provider.StreamDeferred。
- `FetchDeferredAsync` = `StreamDeferred(...).WaitForDoneAsync(options?.CancellationToken ?? default)`（pi `.result()`）。
- `CancelDeferredAsync`：provider 存在 + `SupportsCancelDeferred` → auth → `provider.CancelDeferredAsync(requestModel, handle, applied)`（取消令牌从 `options.CancellationToken` 读取）；不支持 → `ModelsError(Provider, ...)`。
- 不支持的 deferred 不再抛 `NotSupportedException`；`ModelsCapabilityTests.Deferred_Methods_Are_Deferred_To_S8` 现势钉删除，改为行为测试。

#### 4.7.2 ProviderFactory / StaticProvider 分派

- `CreatedProvider` 实现 `IDeferredProvider`：
  - `SupportsFetchDeferred` = 任一 `ProviderStreams.FetchDeferred` 非空（`Api` 或 `ApiByApi` 任一）；
  - `SupportsCancelDeferred` 同；
  - `StreamDeferred` 按 `model.Api` 取 `ProviderStreams`，委托 `FetchDeferred`；缺失 → `ModelsError(Provider, ...)`；
  - `FetchDeferredAsync` = `StreamDeferred(...).WaitForDoneAsync(options?.CancellationToken ?? default)`；
  - `CancelDeferredAsync` 委托 `ProviderStreams.CancelDeferred(model, handle, options, options?.CancellationToken ?? default)`。
- `RefreshableCreatedProvider` 继承同一实现。
- `StaticProvider` 构造器追加可选 `fetchDeferred`/`cancelDeferred` 委托（默认 null，不影响既有调用方）；实现 `IDeferredProvider`，`Supports*` 按委托是否非空；方法缺失委托时抛 `ModelsError(Provider, ...)`。

#### 4.7.3 Faux 请求侧

按 pi `faux.ts:548-566`：

- `Stream` 收到 `SimpleStreamOptions.Deferred is DeferredRequest.Enabled` 时：
  1. 生成 `DeferredHandle { Provider, ModelId, Api, Id = "deferred:{n}", PollAfterMs = options.Deferred.PollAfterMs }`；`n` 用 provider 级单调计数（**确定性**，不复刻 pi 的 `Date.now()`/`Random`）；
  2. 把 `(step, context, options, model, pendingFetches = max(0, Deferred.PendingFetches), cancelled=false)` 存入 deferred map（按 handle.Id）；
  3. 发射 `createDeferredMessage`：content=[]、`StopReason=Deferred`、`Deferred=handle`、usage 空、timestamp=clock；走现有 `EmitMessageAsync`（空 content → start + done/end）。
- `Deferred` 为 `Disabled`/null → 正常响应，不建句柄。
- map 访问加锁（Faux 可能被多线程调用；Unity 侧）。

#### 4.7.4 Faux 取回/取消

按 pi `faux.ts:593-670`：

- `FetchDeferred(requestModel, handle, fetchOptions)`：
  1. 返回流同步；`state.DeferredFetchCount++`；fire-and-forget 异步体；
  2. 若 `fetchOptions.OnResponse` 非空 → `await OnResponse(new ProviderResponse(200, 空头), requestModel)`；
  3. 按 `handle.Id` 查 entry；缺失或 provider/modelId/api 不匹配 → 错误终态，文案逐字 `Unknown faux deferred response: <id>`；
  4. `entry.cancelled` → 错误终态，文案逐字 `Faux deferred response was cancelled: <id>`；
  5. `pendingFetches > 0` → 减一并发射另一条 deferred 消息；
  6. 否则解析最终结果（仅一次并缓存）：
     - 用 entry 的 step/context/model；`Factory` 的 options 去掉 `Deferred`/`CancellationToken`/`OnResponse`（pi 剥离 deferred/signal/onResponse）；
     - 工厂抛错 → 缓存错误消息（pi 同样缓存）；
     - 归一化 Api/Provider/Model/Timestamp/Usage 后发射；后续 fetch 复用缓存；
  7. 错误/取消都转为流的 Error 终态（不抛出）。
- `CancelDeferred(requestModel, handle, cancelOptions)`：
  1. 把 handle 的克隆（Data 深拷贝）追加到 `state.CancelledDeferred`；
  2. 若 entry 存在 → `cancelled = true`；
  3. `cancelOptions.OnResponse` 非空 → `await OnResponse(200, 空头)`；
  4. 不抛出；令牌从 `cancelOptions.CancellationToken` 读取。
- `state.CancelledDeferred` 与 entry map 由同一把锁保护；读取方在操作完成后观察。

### 4.8 D17 助手轮次重试

按 pi `utils/retry.ts`：

1. `IsRetryableAssistantError`：
   - `StopReason != Error` 或 `string.IsNullOrEmpty(ErrorMessage)` → false；
   - 先匹配 **不可重试** 正则（配额/计费/订阅限制：`GoUsageLimitError`、`FreeUsageLimitError`、`Monthly usage limit reached`、`available balance`、`insufficient_quota`、`out of budget`、`quota exceeded`、`billing`、`subscription_sharing_usage_limit_exceeded`、`subscription_sharing_workplace_individual_usage_limit_exceeded`、Z.ai 1308、`subscription_sharing_usage_unavailable`、`subscription_sharing_user_unavailable`）；命中 → false；
   - 再匹配 **可重试** 正则（过载/限流/5xx/网络/流中断/WebSocket/上游转发等，逐字照搬 pi `retry.ts:37-125`）；命中 → true；否则 false。
   - 两组正则用 `RegexOptions.IgnoreCase`；不可重试优先。
2. `RetryDelayMs(baseDelayMs, maxAgentDelayMs, attempt)`：
   - `base * 2^(attempt-1)`（attempt<=1 视为 1）；`maxAgentDelayMs ?? 60_000` 封顶；溢出按 long 计算后钳制，不产生负值。
3. `RetryAssistantCall(produce, policy, signal, callbacks)`：
   - `maxAttempts = policy?.Enabled == true ? policy.MaxRetries : 0`；首次调用不计次；
   - 循环：
     - `response = await produce()`；
     - `StopReason == Aborted`：若有重试记录 → `OnRetryFinished(false, lastAttempt)`，返回 response；
     - `StopReason != Error`：若有重试记录 → `OnRetryFinished(true, lastAttempt)`，返回 response；
     - `attempt >= maxAttempts` 或不可重试 → 若有重试记录 → `OnRetryFinished(false, lastAttempt, ErrorMessage)`，返回 response；
     - 否则 `attempt++`；记录 errorMessage（空则 `"Unknown error"`）；`delay = RetryDelayMs(policy, attempt)`；`await OnRetryScheduled(attempt, maxAttempts, delay, message)`；
     - `await Task.Delay(delay, signal)`：取消 → `OnRetryFinished(false, attempt, message)`，返回 `response with { StopReason = Aborted, ErrorMessage = null }`（pi 的 sleep-abort 归一化）；
     - `await OnRetryAttemptStart()`；继续循环。
   - 回调异常不吞（pi 同）；不自动接入 Agent。
4. D17 在 M1 无消费者：验收=模块行为测试；2A 的 coding-agent/compaction 直接调用。

### 4.9 `DeferredRequest.Enabled.Window` 校验

- `DeferredRequest.Enabled` 改为显式构造器 + `Window` 属性：
  - 允许：`null`、`"15m"`、`"1h"`、`"24h"`（ordinal，区分大小写）；
  - 其他值 → `ArgumentException("Unknown deferred window: <value>. Expected 15m, 1h, or 24h.")`；
  - `new DeferredRequest.Enabled("1h")`、`with { Window = ... }`、模式匹配均保持可用。
- 这是 S3 spec 遗留的 S8 项（D3 验收一部分）；pi 只有 TS 类型约束、运行时宽松；我们主动加固（§6 D 类）。

### 4.10 线程模型（Unity / netstandard2.1）

- pi 假设单线程；Unity 宿主（S13）会从主线程调 `Steer/Abort/Prompt`，而 `AgentLoop` 的续体可能在线程池。
- 设计：`Agent` 内部用一把 `_gate` 保护 **同步状态变更**：
  - `activeRun` 的建立/清除、`State` 的字段写入、队列 enqueue/drain、`Reset`；
  - `processEvents` 先在锁内归约状态，释放锁后再 await listeners（**不在锁内 await**）；
  - `WaitForIdle`/`Signal`/`Abort` 在锁内读取 `activeRun` 后立即释放。
- listener 本身可能在后台线程执行；宿主负责 marshal 到 Unity 主线程（S13 文档登记）。
- 这是相对 pi 的 D 类加固（pi 单线程无需锁）；代价=少量锁开销，收益=UI 线程可安全 steer/abort。
- netstandard2.1 注意：`IReadOnlySet<T>`、`Task.WaitAsync`、`System.Threading.Lock` 均不可用；用 `IReadOnlyCollection<T>`、`SemaphoreSlim`/`lock`、`Task.Delay(ms, token)`。

---

## 5. 测试与验收

### 5.1 Core（Faux 驱动，无网）

`tests/PiUnityAgent.Core.Tests/Agent/`：

- `AgentStateTests`：
  - 空 initialState → 默认 model/thinking=Off、messages=[]、tools=[]、isStreaming=false；
  - systemPrompt + tools → 首条 system 消息含 content + ToolsAdded；messages 已以 system 开头时不插入；
  - `SystemPrompt` 重放：多条 system 的 content 拼接、sections 覆盖/删除、tools 累积；
  - tools/messages setter 复制顶层数组；getter 快照不可改内部；
  - `Reset` 保留折叠后的 system 基线、清队列与运行状态；activeRun 时抛精确文案。
- `AgentQueueTests`：
  - `QueueMode.OneAtATime`：入 2 条 steer，首个排空点只投递 1 条，第二个排空点投递第 2 条（无同回合双投递）；
  - `All`：一次排空全部；
  - followUp 只在本应停止时投递；steering 优先的 `PeekQueuedMessages`；`ClearAllQueues`/`HasQueuedMessages`。
- `AgentLifecycleTests`：
  - `Prompt` 单飞：运行中再 Prompt → 精确异常文案；
  - `Abort`：流中取消 → `StopReason.Aborted` 终态、`turn_end` + `agent_end`、`WaitForIdle` 完成；
  - 运行异常 → `handleRunFailure` 的四事件序列（`message_start`/`message_end`/`turn_end`/`agent_end`）与失败消息字段；
  - `WaitForIdle` 在 `agent_end` listener 中慢 await 时仍等到 listener 结算；
  - `Signal` 有/无 run；`Reset` 运行中抛。
- `AgentFacadeTests`：
  - `Prompt(string, images)` 生成正确 UserMessage（时间戳/内容）；`Prompt(Message)`/批量；
  - `Continue` 四种分支的精确文案与事件；
  - `Subscribe` 按订阅序 await、取消订阅后不再收到；重复订阅去重；
  - hooks 全量接线：`ConvertToLlm`/`TransformContext`、`PrepareRequest`、`PrepareNextTurn` 与 `PrepareNextTurnWithContext` 的调用与返回值生效；
  - `StreamFn` 省略时走 `DefaultStreamFn`；未配置时抛 pi 文案；
  - 构造后可变字段在下一次 run 生效。
- `AgentThreadingTests`：
  - run 中从另一线程 `Steer`/`Abort`，不抛且消息/取消生效（锁模型冒烟）。

### 5.2 Ai（D3 / D17）

- `Providers/DeferredTests.cs`（替换现势钉 `ModelsCapabilityTests.Deferred_Methods_Are_Deferred_To_S8`）：
  - 不支持的 provider：`StreamDeferred` 终态 Error（文案 `Provider X does not support deferred responses`）、`FetchDeferredAsync` 返回错误消息、`CancelDeferredAsync` 抛 `ModelsError(Provider)`；
  - Faux `DeferredRequest.Enabled` + `PendingFetches=1`：首次 stream → `StopReason.Deferred` + handle；第一次 fetch → 仍是 deferred；第二次 fetch → 最终消息；再 fetch 复用缓存；
  - `Cancel` → `CancelledDeferred` 收到克隆、后续 fetch 报 `... cancelled ...`；`DeferredFetchCount` 正确；
  - 未知 handle / provider / modelId / api 不匹配 → 精确错误文案；
  - `OnResponse` 在 fetch/cancel 时被调用（200/空头）；auth 在 provider 调用前生效；
  - 取消令牌：fetch 中取消 → Aborted 终态；
  - `FauxProviderOptions.Deferred.PollAfterMs` 落入 handle。
- `Utils/RetryTests.cs`：
  - 不可重试配额/计费样例 → false；过载/429/500/网络/流中断 → true；非 Error/空 ErrorMessage → false；
  - `RetryDelayMs` 指数与 60s 封顶、attempt<=1、溢出；
  - `RetryAssistantCall`：成功直返；error→重试成功（回调时序：Scheduled→AttemptStart→Finished(true)）；maxRetries 耗尽（Finished(false, 最终错误)）；不可重试快速失败；取消背退避 → 返回 `StopReason.Aborted` + `ErrorMessage=null` + Finished(false)；policy null/disabled = 直接返回首次响应。
- `Types/DeferredWindowTests.cs`：`null/15m/1h/24h` 通过；其他值抛 `ArgumentException`（含精确文案）。

### 5.3 验收口径

- Faux 可跑通 `Prompt → steer → followUp → abort → continue` 全链；无网络依赖。
- 事件顺序、单飞/abort/waitForIdle、队列排空、hooks 接线与 pi 对齐。
- D3/D17/D23 台账转 `已完成`（完成提交、验收测试锚点）；
- `check-deferred.py --root . --current-stage S8 --gate exit` 绿（D3/D17/D23 无 stub；D4/D5/D8/D11/D22 未到期）；
- Release 构建 0 警告 0 错误；既有 Ai 测试（除被替换的 deferred 现势钉）不回归；
- 独立偏离审计 = PASS / PASS_WITH_REGISTERED_DEVIATIONS，§6 表逐行与代码一致。

---

## 6. 三方偏离表（设计期登记；实现/审计后复核）

| 项 | pi 原版 | pi.net | 我们 | 类别 | 原因 / 代价 |
|---|---|---|---|---|---|
| AgentMessage 联合 + 自定义消息 | `Message \| CustomAgentMessages`（声明合并） | `ChatMessage`（Ai Message） | Ai `Message`；自定义=继承 Message 子类 | A/B | S7 已登记；C# 无声明合并；代价=自定义消息少一层自由度 |
| `AgentState` 形状 | `interface AgentState`：systemPrompt/model/thinkingLevel/tools/messages/isStreaming/streamingMessage/pendingToolCalls/errorMessage | Agent 上平铺属性 | 独立 `AgentState` 类，读写面按 pi | B/D | pi.net 扁平化属 Shape gap；我们采用 pi 对象形状；`Agent.State` 返回该对象 |
| `AgentOptions.initialState` | `initialState?: AgentInitialState` | 扁平 `SystemPrompt/Messages/Model/Tools` | `InitialState` 记录 | B/D | 跟 pi 原版；不采用 pi.net 扁平化 |
| `AgentOptions.streamFn` | 类型必填；构造器运行期可省略→`getDefaultStreamFn()` | `required StreamFn` | 可空 + `DefaultStreamFn.Get()` 兜底 | D/B | 行为对齐 pi 兜底，编译期比 TS 宽松；代价=误配延迟到构造/运行时 |
| `AgentOptions.prepareNextTurnWithContext` | 有（context+signal） | 无 | 有独立 delegate + `PrepareNextTurnContext` | B | 补 pi 公共形状；`PrepareNextTurnContext extends AgentTurnContext` 为空扩展，C# 用同字段 record |
| `AgentLoopConfig.prepareNextTurn` | loop 层只带 context（agent-loop.ts:195） | 同（无 signal） | S7 保持 `PrepareNextTurn(AgentTurnContext)` 不变；门面闭包捕获 signal | B | 不回改 S7 签名；与 pi 原版 loop 调用一致 |
| `QueueMode` | `"all" \| "one-at-a-time"` 字符串联合 | 无 | `QueueMode` enum（All/OneAtATime） | A | C# 枚举；wire 无 |
| 待处理队列 | `PendingMessageQueue`（未导出） | 同（public class） | internal class | B | pi 未导出；仅 Agent 内部使用 |
| `AgentState.messages/tools` getter | 返回内部数组（可原地 push） | `IReadOnlyList` | 只读快照；setter 复制 | B/D | 防止外部改内部 transcript；代价=非 live 引用 |
| `AgentState.pendingToolCalls` | `ReadonlySet<string>` | `IReadOnlyCollection<string>` | `IReadOnlyCollection<string>` 快照 | A/B | netstandard2.1 无 `IReadOnlySet<T>`（.NET 5 引入） |
| `AgentState.systemPrompt` | 计算属性（重放 system） | `SystemPrompt` 属性 | 同（Core internal 重放） | B | 重放辅助本阶段 internal；D11/S9 再公开 |
| `Agent.subscribe` | 返回 `() => void` | `IDisposable` | `IDisposable` | A/B | C# 惯用法；Set 去重语义保留 |
| `Agent.signal` | `AbortSignal \| undefined` | `CancellationToken?` | `CancellationToken?` | A | C# 惯用法 |
| `Agent.abort/waitForIdle` | `abortController.abort()` / Promise | CTS.Cancel / Task | 同 | A/B | CTS 单次取消；TCS `RunContinuationsAsynchronously` |
| `Agent.prompt` 输入 | `string \| AgentMessage \| AgentMessage[] + images?` | 三个重载 | 三个重载 | B | C# 无联合类型；string 重载用 `ImageContent[]` |
| `defaultConvertToLlm` | 按 role 字符串过滤 | 同 | 按 `Message.Role` 过滤四角色 | B | 自定义 Message 子类同样按 Role 过滤 |
| `ConvertToLlm` 同步/异步 | `Message[] \| Promise<Message[]>` | `Task` | `Task` delegate | A/B | C# 统一 async 面；同步调用方包 `Task.FromResult` |
| `GetApiKey` | 无 signal | `Func<string,Task<string?>>` | 门面无 signal；config 适配忽略 ct | B | 与 pi 门面签名一致；S7 config 保留 ct |
| `AgentOptions.transport` | `Transport` 字符串联合 | string | `TransportKind?`；默认 Auto | A/B | S3 已定义 `TransportKind`；wire 映射已有 |
| `onPayload/onResponse/onProviderStreamEvent` | `SimpleStreamOptions[...]` | 同 | S3 `StreamOptions` delegate | B | 复用既有形状，无新语义 |
| `AgentState.thinkingLevel` | `ThinkingLevel`（含 off） | `ThinkingLevel` | `ModelThinkingLevel`（含 Off） | A/B | S7 已登记；config 流选项用 `ThinkingLevel?`，turn/update 用 `ModelThinkingLevel` |
| transcript 重放子集 | `createInitialSystemMessage/getCurrentSystemMessage/getCurrentSystemPrompt` 公开（pi-ai） | `Transcript` 公开 | Core internal `AgentTranscript`；D11/S9 公开 | C/D | 保持 S7“不提前扩 Ai 公共面”的边界；代价=S9 需提升/迁移；D11 目标不变 |
| 线程安全 | pi 单线程假设 | 无锁 | `_gate` 保护同步状态/队列/activeRun；listener 在后台线程 | D | Unity 主线程 steer/abort 与循环续体跨线程；代价=少量锁开销 |
| D3 provider 能力 | 可选方法存在性（`provider.fetchDeferred`） | `SupportsFetchDeferred/SupportsCancelDeferred` | 接口 + 两个布尔属性 | B | C# 无法用方法存在性表达；工厂/静态 provider 均实现 |
| D3 工厂分派 | createProvider 有任一 api 的 fetchDeferred 才导出 | CreateProvider 支持 Flags | CreatedProvider 实现 `IDeferredProvider` + Supports* | B | 与 pi 能力面等价；缺失时同文案 ModelsError |
| D3 不支持文案 | `Provider X does not support deferred responses` | 同 | 同（`ModelsError(Provider)`） | B | 逐字；错误类型 C# 化 |
| D3 Faux handle id | `Date.now()` + `Math.random()` | 同? | `deferred:{n}` provider 单调计数 | D | 测试确定性（S7 Faux 同纪律）；代价=id 不含时间 |
| D3 fetch/cancel onResponse | 200 + 空头 | 同 | 同 | B | ProviderResponse 形状已有 |
| D3 fetch 缓存 | entry.final 缓存一次 | 同 | 同 | B | 与 pi 一致 |
| D3 fetch 选项剥离 | 去 `deferred/signal/onResponse` | 同 | 去 `Deferred/CancellationToken/OnResponse` | B | C# 字段名映射 |
| D3 Window 校验 | TS 类型约束，运行时宽松 | 无 | `Enabled` 构造时严格校验 | D | S3 遗留项；收益=早失败；代价=与 pi 运行时宽松不同 |
| D17 重试模块 | `retry.ts` 公共工具；agent-core 不消费 | `Retry.cs` | `Ai/Utils/Retry.cs`；不接 Agent | B/scope | 与 pi 一致；调用方 2A |
| D17 回调 | `void \| Promise<void>` | Task | Task | A/B | C# async 面 |
| D17 退避取消 | AbortSignal + setTimeout | Task.Delay(token) | `Task.Delay(delay, token)` | A | C# 取消模型；归一化 aborted 消息 |
| D17 正则 | 两组正则（retry.ts:7-125） | 同 | 逐字移植 + IgnoreCase | B | 分类语义一致；代价=正则引擎差异（.NET 与 V8 对个别字符类可能不同，验收用行为语料） |
| `proxy.ts` | 395 行服务端代理流 | `Proxy.cs` | 不做 | C/D22 | 后续扩展；S8 不引入 server RPC |
| Deferred 真实 provider | 仅 faux 导出 fetchDeferred（v1.5.0） | faux 同 | 仅 Faux 验收 | scope | pi 同版本无真实 provider deferred；不虚构适配 |

---

## 7. 风险与缓解

1. **生命周期/单飞竞态**：`activeRun`、TCS、abort 与 listener await 交错。缓解=`AgentLifecycleTests` 逐分支；`_gate` 只保护同步段，不在锁内 await。
2. **队列双投递**：`Continue` 从 assistant 末条 + `skipInitialSteeringPoll` 与 S7 的排空点叠加。缓解=`AgentQueueTests` one-at-a-time 两排空点断言；S7 scheduling 测试保持绿。
3. **事件顺序**：失败收场四事件必须与正常路径收尾一致；listener 结算才算 idle。缓解=事件序列断言 + `WaitForIdle` 慢 listener 用例。
4. **D3 Faux 并发**：deferred map/cancelled 列表与 stream 前缀可能并发。缓解=单锁保护 map/列表；fetch 的异步体串行化 entry；测试顺序调用。
5. **D17 正则漂移**：文案语料多，漏一条会误判可重试。缓解=逐字复制 pi `retry.ts` 两组 pattern；测试覆盖不可重试/可重试各代表向量；实现后审计核对。
6. **Window 严格校验破坏调用方**：仅测试/宿主构造 `Enabled`；非法值应快速失败。缓解=精确异常文案 + 测试。
7. **S7 形状回改风险**：`AgentLoopConfig`/钩子 delegate 一律不动；`PrepareNextTurnWithContext` 只在门面层新增。缓解=实现前跑 S7 基线；审计核对“S7 未改”。
8. **netstandard2.1 API 误用**：`IReadOnlySet`/`Task.WaitAsync`/`Lock` 不可用。缓解=CI Release 构建 + 代码审查；纯 Ai/Core 单测不引 net10 专有 API。

---

## 8. 后置能力与台账影响

- **D3 承接**：三方法真实现 + 工厂/静态 provider 分派 + Faux 全行为 + Window 校验；台账状态 `已完成`，完成提交与验收锚点填新测试；删除 `ModelsCapabilityTests.Deferred_Methods_Are_Deferred_To_S8` 现势钉。
- **D17 承接**：`Ai/Utils/Retry.cs` 公共模块 + 验收测试；台账 `已完成`；不接入 Agent 循环（与 pi 一致）。
- **D23 承接**：`QueueMode` + 双队列 + 排空点注入；台账 `已完成`。
- **D11**：仍归 S9；S8 的 `AgentTranscript` 是 internal 子集，不构成 D11 完成。
- **D22**：仍为后续扩展；S7 已登记，S8 不动。
- `check-deferred.py --current-stage S8 --gate exit` 通过条件：D3/D17/D23 已完成且无 stub；D4/D5/D8 目标 2B/后续；D11 目标 S9；D22 后续。

---

## 9. 参考索引

| 内容 | pi 源 |
|---|---|
| Agent 状态/选项/队列/生命周期 | `packages/agent/src/agent.ts:49-623` |
| `QueueMode` / `PrepareNextTurnContext` / AgentState/AgentOptions | `packages/agent/src/types.ts:49-430` |
| 循环层 `prepareNextTurn` 调用点 | `packages/agent/src/agent-loop.ts:195` |
| 默认流函数 | `packages/agent/src/stream-fn.ts` |
| D17 重试/分类正则 | `packages/ai/src/utils/retry.ts:1-276` |
| D3 Models deferred 管线 | `packages/ai/src/models.ts:937-990` |
| D3 Faux deferred | `packages/ai/src/providers/faux.ts:313-322, 469-480, 548-566, 593-670` |
| D3 Deferred 类型 | `packages/ai/src/types.ts:282-320, 382, 489, 600`；`utils/event-stream.ts` |
| system 重放 | `packages/ai/src/utils/transcript.ts:10-130`；`utils/text.ts:6-21` |

pi.net 参考：`src/Pi.Agent/{Agent.cs,AgentOptions.cs,Types/AgentTypes.cs}`；`src/Pi.Ai/Utils/Retry.cs`；`src/Pi.Ai/Models/Models.cs`（deferred 段）；`src/Pi.Ai/Models/CreateProvider.cs`（Supports*）。形状以 pi 原版为准，pi.net 只作 C# 写法参考。

教学：`pi-agent-book/book/02-runtime.md` §2.7–2.8；`book/06-extensibility.md` §6.2；`docs/07-通讯层集成契约.md`（deferred 走同一 Fetch 端口）。

---

## 10. 完成定义

- Core `Agent/` 公共形状与 §3 一致；Ai deferred/retry 形状与 §3.5/§3.6 一致；Release 0 警告 0 错误；
- Core `Agent*` 测试全绿；Ai `DeferredTests`/`RetryTests`/`DeferredWindowTests` 全绿；既有 Ai 测试不回归；
- D3/D17/D23 台账 `已完成` + 完成提交；`check-deferred S8 --gate exit` 绿；
- 独立偏离审计（SOP）= PASS / PASS_WITH_REGISTERED_DEVIATIONS，§6 表逐行与代码一致；
- S7 公共形状零回改；
- M1 收口：S0–S8 全部交付，无未登记 Shape gap；教学篇/PORTING 状态更新。
