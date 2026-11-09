# S7 Agent Loop 设计 spec（2026-11-09）

> **状态**：v1 已实现（Tasks 1–7 落于 `feature/s7-agent-loop`）；独立审计 FAIL→修复→新审计者复核 **PASS_WITH_REGISTERED_DEVIATIONS**（`docs/audits/2026-11-09-S7-deviation-audit.md` / `-recheck.md`）；已合 main（`a6fc807`）。
> **范围**：S7 = 低层 agent 循环：turn 调度、助手流式响应、工具调用批次执行（串/并行）、事件序列、tool-call 单调用管线与 hooks 调用点；同时承接 S6 遗留的 D16（流式参数 partial 快照 + repairJson）。不做 Agent 门面/双队列/abort 集成（S8）、上下文组装/system prompt（S9）、会话持久化（S10）。
> **参考版本**：pi `v1.5.0` / `f9a263655`；pi.net `main` / `2d6757b`；UnityLlmTransport `4c0cc4b` / `0.6.0`（仅通讯事实，S7 不引用）。
> **上位规则**：[`docs/03-开发方法-拆解学习与移植约定.md`](../../03-开发方法-拆解学习与移植约定.md) §1.3；[`docs/05-教学阶段路线.md`](../../05-教学阶段路线.md) §S7；[`docs/SOP/阶段偏离审查-SOP-2026-11-08.md`](../../SOP/阶段偏离审查-SOP-2026-11-08.md)。
> **前置**：S6 工具契约已合 main（`AgentTool`/`AgentToolResult`/`ToolValidation`/D21）。
> **目标**：`agentLoop` 可无网运行——Faux 驱动"文本→工具→文本"多轮，事件顺序/批次语义/错误编码/调度钩子与 pi 一致；D16 转已完成。

---

## 0. 背景

S6 只定了工具契约：执行器、循环、事件都还没有。S7 把 `packages/agent/src/agent-loop.ts`（959 行）的循环机制落到 `PiUnityAgent.Core`：

- `agentLoop` / `agentLoopContinue`：以新 prompt 启动 / 从既有上下文续跑；
- `runLoop`：外层 follow-up × 内层 tool-call/steering 的双层循环；
- `streamAssistantResponse`：AgentMessage → LLM Message 的边界转换 + 助手流式事件转发；
- 工具批次：串行 / 并行（预检顺序、执行并发、end 按完成序、result message 按源序）、`length` 截断整批失败、`terminate` 全批语义；
- 单调用管线 `runToolCall`：prepareArguments → validate → before hook → execute → after hook → outcome；
- 事件：`agent_start/turn_start/message_*/tool_execution_*/turn_end/agent_end`；
- 工具装载变更公告（`toolsAdded/toolsRemoved` 系统消息）。

同时，S6 台账把 **D16（流式工具参数 partial 快照 + repairJson）** 目标阶段定为 S7，因此本阶段还包含 Ai 层的流式 JSON 解析与三处协议适配器接线（见 §4.6）。

S8 的 `Agent` 门面提供双队列/abort/用户级钩子注册与状态（`agent.ts`）；S7 只做低层循环与其配置回调的调用点。

---

## 1. 目标 / 非目标

### 1.1 目标

- Core 新增 `AgentLoop` 低层循环公共 API：`Loop`/`Continue`（事件流）与 `RunAsync`/`RunContinueAsync`（异步结果）；
- 新增 `AgentContext`、`AgentLoopConfig`、turn/prepare/finish 回调形状、`AgentEvent` 10 变体、`AgentEventSink`、tool hook 形状、`AgentToolCallOutcome`；
- 工具批次执行与单调用管线完整落地（S6 契约消费 + hooks 调用）；
- 工具装载变更公告（系统消息）与 `convertToLlm`/`transformContext` 边界；
- `DefaultStreamFn` 全局默认流函数（宿主可注入）；
- **D16 承接**：`StreamingJsonParser` + `parseJsonWithRepair` + `ToolCallArgumentsStream` 移植；`ToolCallArguments` 升级为 delta 期间暴露 partial、终点 repair；Anthropic/OpenAI Completions/OpenAI Responses 三适配器在 delta 时回填 partial 快照；`ToolCallArguments_Does_Not_Expose_Partial_Json` 现势钉改为部分快照钉；
- Core 单测（Faux 驱动）+ Ai D16 单测；Ai/Core 全部测试绿。

### 1.2 非目标

- `Agent` 门面、`AgentState`、双队列（steer/followUp 的**存储**）、abort 集成、`AgentOptions`、事件订阅器（S8）；
- system prompt 组装、`getCurrentSystemMessage`/`collapseSystemMessages` 公开重放辅助（D11/S9）；S7 只实现循环所需的 internal 装载差异；
- 会话 JSONL/branch/fork（S10）、压缩（S11）、扩展/资源（S12）；
- `proxy.ts` 服务端代理流函数（server RPC 路线；登记 D22 后续扩展）；
- Deferred 工具/fetch（D3/S8）、图片/分类（D4/D5）；
- 真实网络/Unity 宿主（S13）；S7 全部测试走 Faux。

---

## 2. 工程落点与依赖

```
PiUnityAgent.Core（引用 Ai）
└─ Agent/
   ├─ AgentEvent.cs             （10 变体 + AgentEventSink）
   ├─ AgentContext.cs
   ├─ AgentLoopConfig.cs        （config + prepare/finish/turn 形状）
   ├─ AgentLoopTypes.cs         （AgentTurnContext/Decision/TurnUpdate/PrepareRequestContext）
   ├─ ToolCallHooks.cs          （Before/After context/result）
   ├─ AgentToolCallOutcome.cs
   ├─ AgentLoop.cs              （公共入口 Loop/Continue/RunAsync/RunContinueAsync）
   ├─ AgentLoop.Run.cs          （runLoop 双层循环）
   ├─ AgentLoop.Stream.cs       （streamAssistantResponse）
   ├─ ToolExecution.cs          （批次 + runToolCall；internal batch，public RunToolCallAsync）
   ├─ ToolLoadout.cs            （declareToolChanges + internal declaration diff）
   └─ DefaultStreamFn.cs

PiUnityAgent.Ai（D16）
├─ Utils/StreamingJson.cs      （internal，StreamingJsonParser + ToolCallArgumentsStream）
├─ Utils/JsonParse.cs          （internal，repairJson/parseJsonWithRepair/parseToolCallArguments）
├─ Api/ToolCallArguments.cs    （升级：Append→partial JsonObject；Finish→repair）
└─ Api/{AnthropicMessages,OpenAiCompletions,OpenAiResponses}.cs（delta 回填 partial）
```

- Core 只引用 Ai；Ai 不反向依赖 Core；无 UnityEngine/transport/HTTP/GameData。
- Ai 不新增 NuGet；Core 不新增依赖。
- D16 的 `StreamingJsonParser`/`JsonParse` 参考 pi.net 已有 C# 移植（`src/Pi.Ai/Utils/StreamingJson.cs`、`JsonParse.cs`），行为以 pi TS 为准。

---

## 3. 公共形状

### 3.1 AgentMessage / AgentContext

pi `AgentMessage = Message | CustomAgentMessages`（声明合并扩展）；C# 无声明合并。**AgentMessage 直接用 Ai `Message`**：自定义消息 = 继承 `Message` 的应用子类（实现 `Role`/`Timestamp`），由 `convertToLlm` 过滤或转换（A/B；pi.net `ChatMessage` 同路线）。

```csharp
public sealed record AgentContext
{
    /// <summary>模型可见的 transcript；循环内原地追加。对应 pi AgentContext.messages（数组）。</summary>
    public required List<Message> Messages { get; init; }

    /// <summary>本次运行可执行的工具集；对应 pi AgentContext.tools。</summary>
    public IReadOnlyList<AgentTool>? Tools { get; init; }
}
```

### 3.2 AgentEvent 与事件槽

```csharp
public abstract record AgentEvent
{
    private AgentEvent() { }

    public sealed record AgentStart : AgentEvent;
    public sealed record AgentEnd(IReadOnlyList<Message> Messages) : AgentEvent;

    public sealed record TurnStart : AgentEvent;
    public sealed record TurnEnd(AssistantMessage Message, IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent;

    public sealed record MessageStart(Message Message) : AgentEvent;
    public sealed record MessageUpdate(AssistantMessageEvent AssistantMessageEvent, AssistantMessage Message) : AgentEvent;
    public sealed record MessageEnd(Message Message) : AgentEvent;

    public sealed record ToolExecutionStart(string ToolCallId, string ToolName, JsonObject Args) : AgentEvent;
    public sealed record ToolExecutionUpdate(string ToolCallId, string ToolName, JsonObject Args, AgentToolResult PartialResult) : AgentEvent;
    public sealed record ToolExecutionEnd(
        string ToolCallId,
        string ToolName,
        AgentToolResult Result,
        bool IsError,
        long? DurationMs) : AgentEvent;
}

/// <summary>事件槽；agent_end 是运行最后一个事件。对应 pi AgentEventSink。</summary>
public delegate Task AgentEventSink(AgentEvent @event);
```

### 3.3 AgentLoopConfig 与 turn 形状

```csharp
public sealed record AgentLoopConfig
{
    public required Model Model { get; init; }

    /// <summary>AgentMessage → LLM Message；不得抛出（pi 契约）。</summary>
    public required Func<IReadOnlyList<Message>, Task<IReadOnlyList<Message>>> ConvertToLlm { get; init; }

    public Func<IReadOnlyList<Message>, CancellationToken, Task<IReadOnlyList<Message>>>? TransformContext { get; init; }

    public Func<string, CancellationToken, Task<string?>>? GetApiKey { get; init; }

    public FinishTurn? FinishTurn { get; init; }
    public PrepareRequest? PrepareRequest { get; init; }
    public PrepareNextTurn? PrepareNextTurn { get; init; }

    public Func<Task<IReadOnlyList<Message>>>? GetSteeringMessages { get; init; }
    public Func<Task<IReadOnlyList<Message>>>? GetFollowUpMessages { get; init; }

    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;

    public BeforeToolCall? BeforeToolCall { get; init; }
    public AfterToolCall? AfterToolCall { get; init; }

    /// <summary>基础流式选项（pi config extends SimpleStreamOptions → C# 组合）。</summary>
    public SimpleStreamOptions? StreamOptions { get; init; }

    /// <summary>请求身份；逐字段覆盖 DefaultAgentIdentity。对应 pi config.identity。</summary>
    public ClientIdentity? Identity { get; init; }
}

public delegate Task<AgentTurnDecision?> FinishTurn(AgentTurnContext turn, CancellationToken signal);
public delegate Task<AgentRequestUpdate?> PrepareRequest(PrepareRequestContext request, CancellationToken signal);
public delegate Task<AgentLoopTurnUpdate?> PrepareNextTurn(AgentTurnContext turn);

public enum AgentTurnDecision { Continue, End }

public sealed record AgentTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    AgentContext Context,
    IReadOnlyList<Message> NewMessages);

public sealed record PrepareRequestContext(AgentContext Context, Model Model, ModelThinkingLevel ThinkingLevel);

public sealed record AgentLoopTurnUpdate
{
    public AgentContext? Context { get; init; }
    public IReadOnlyList<Message>? Messages { get; init; }
    public Model? Model { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
}

/// <summary>prepareRequest 的替换状态（pi AgentRequestUpdate：不含 messages）。</summary>
public sealed record AgentRequestUpdate
{
    public AgentContext? Context { get; init; }
    public Model? Model { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
}
```

- `FinishTurn` 返回 null=正常调度；`Continue`=保证一次下一请求；`End`=立刻结束运行。error/aborted 恒硬退出。
- 思考档位：pi `reasoning` 字符串含 `"off"`；我们 config 用 `SimpleStreamOptions.Reasoning`（`ThinkingLevel?`，null=关）送给 provider，turn/update 用 `ModelThinkingLevel?`（含 Off）表达"显式关/不改"（A 映射）。
- 所有回调 `AbortSignal → CancellationToken`；异步回调 → `Task` delegate（A/B）。
- `DefaultAgentIdentity` 与 `config.Identity` 逐字段合并（`ClientIdentityMerger.Merge`，请求级覆盖），作为流请求 identity。

### 3.4 工具钩子形状

```csharp
public sealed record BeforeToolCallContext(
    AssistantMessage AssistantMessage,
    ToolCall ToolCall,
    JsonNode? Args,
    AgentContext Context);

public sealed record BeforeToolCallResult(bool? Block = null, string? Reason = null, bool? Terminate = null);

public sealed record AfterToolCallContext(
    AssistantMessage AssistantMessage,
    ToolCall ToolCall,
    JsonNode? Args,
    AgentToolResult Result,
    bool IsError,
    AgentContext Context);

public sealed record AfterToolCallResult(
    IReadOnlyList<ContentBlock>? Content = null,
    JsonNode? Details = null,
    JsonNode? StructuredContent = null,
    bool? IsError = null,
    Usage? Usage = null,
    bool? Terminate = null);

public delegate Task<BeforeToolCallResult?> BeforeToolCall(BeforeToolCallContext context, CancellationToken signal);
public delegate Task<AfterToolCallResult?> AfterToolCall(AfterToolCallContext context, CancellationToken signal);

public sealed record AgentToolCallOutcome(
    ToolCall ToolCall,
    AgentToolResult Result,
    bool IsError)
{
    /// <summary>execute 耗时（单调钟毫秒）；未执行时 null。对应 pi durationMs。</summary>
    public long? DurationMs { get; init; }
}
```

### 3.5 公共入口

```csharp
public static partial class AgentLoop
{
    /// <summary>以新 prompt 启动；prompt 推入 context 并各发 message_start/end。</summary>
    public static EventStream<AgentEvent, IReadOnlyList<Message>> Loop(
        IReadOnlyList<Message> prompts,
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken signal,
        StreamFn? streamFn = null);

    /// <summary>从既有上下文续跑；最后一条不得为 assistant。</summary>
    public static EventStream<AgentEvent, IReadOnlyList<Message>> Continue(
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken signal,
        StreamFn? streamFn = null);

    public static Task<IReadOnlyList<Message>> RunAsync(
        IReadOnlyList<Message> prompts,
        AgentContext context,
        AgentLoopConfig config,
        AgentEventSink emit,
        CancellationToken signal,
        StreamFn? streamFn = null);

    public static Task<IReadOnlyList<Message>> RunContinueAsync(
        AgentContext context,
        AgentLoopConfig config,
        AgentEventSink emit,
        CancellationToken signal,
        StreamFn? streamFn = null);
}

public static class ToolExecution
{
    /// <summary>与模型发起调用同管线；永不因工具失败抛出。对应 pi runToolCall。</summary>
    public static Task<AgentToolCallOutcome> RunToolCallAsync(
        ToolCall toolCall,
        AssistantMessage assistantMessage,
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentToolUpdateCallback? onUpdate = null);
}

public static partial class AgentLoop
{
    /// <summary>请求身份缺省：UA 归属 pi。对应 pi DEFAULT_AGENT_IDENTITY。</summary>
    public static ClientIdentity DefaultAgentIdentity { get; } =
        new() { Attribution = ClientIdentityHelpers.PiAttribution };
}

public static class DefaultStreamFn
{
    public static void Set(StreamFn? streamFn);
    public static StreamFn Get();   // 未配置抛 InvalidOperationException，文案同 pi
    public static bool IsConfigured { get; }
}
```

- `Loop/Continue` 返回 `EventStream`：`agent_end` 为终态事件，终值=本次运行新增消息（prompt 运行含初始 prompt；续跑不含既有 context）。loop 异步异常 → `stream.Fail(error)`（pi.net PumpAsync 路线；pi `.then(end)` 会悬挂，取 pi.net B/D）。
- 批次执行 `ExecuteToolCalls` 保持 internal（pi 未导出；测试经 Loop 覆盖）。

---

## 4. 行为语义

### 4.1 双层循环（`runLoop`）

按 pi `agent-loop.ts:180-330`：

0. 入口事件：`agent_start` → `turn_start` → 初始 prompt 各发 `message_start/message_end`（prompt 先推入 context）；续跑不发 prompt 事件、初始 `turn_start` 照发；
1. 初始 steering 轮询；外层循环（follow-up 可续跑）× 内层循环（tool-call / steering）；
2. `prepareNextTurn`（仅上一回合完成后、下一回合前）→ 替换 context/model/thinking + 追加 prepared messages；准备期间补轮询 steering（仅当此前为空，避免 one-at-a-time 双投递）；
3. 每次请求前追加 prepared/pending 消息（经 `declareToolChanges`）并各发 `message_start/end`；
4. `prepareRequest` 可在每次请求前替换 context/model/thinking（首个请求也调用）；
5. 流式助手响应（§4.2）；`error/aborted` → 调 `finishTurn`、发 `turn_end` + `agent_end` 并返回；
6. 收集 `toolCall`：stopReason=`length` → 整批失败（§4.4），否则批次执行；
7. 工具结果按序追加 context/newMessages；`finishTurn` 决策：`end` → `agent_end` 结束；`continue` → 保证下一请求（若有 tool-result/steering/follow-up 已满足则不额外空转）；
8. 内层条件 `hasMoreToolCalls || pendingMessages.Count > 0`；内层退出后轮询 follow-up，有则作为 pending 继续外层；`explicitContinuation` 时允许一次 context-only turn；否则结束；
9. 结束时发 `agent_end(messages)`。**agent_end 是最后事件**。

### 4.2 助手流式响应（`streamAssistantResponse`）

1. `transformContext`（可选）→ `convertToLlm` → `TranscriptNormalization.NormalizeContext`；
2. `getApiKey(provider)` 结果优先，回退 `StreamOptions.ApiKey`；
3. 构造流选项：`StreamOptions` + resolved ApiKey + identity（`DefaultAgentIdentity` 与请求 identity 逐字段合并）+ CancellationToken；
4. 消费助手事件：
   - `start`：把 partial 推入 context 并替换/追加、发 `message_start`；
   - text/thinking/toolcall 各 delta/end：把 `event.Partial` 写回 context 末条、发 `message_update`（携带快照）；
   - `done`/`error`：取流终值（`AssistantMessageEventStream` 结果），设置 `ThinkingLevel`（config 缺省→Off），替换 partial 或追加、确保有 `message_start`、发 `message_end`，返回结果消息；
   - 流无 done/error（异常路径）：仍取 result 并收尾（与 pi 收尾一致）。
5. 助手最终消息的 `ThinkingLevel` 记为本次请求档位（pi `thinkingLevel` 字段）。

### 4.3 工具装载变更公告（`declareToolChanges`）

按 pi `agent-loop.ts:330-410`：

- `context.Tools` → `toToolDeclaration`（剥离执行/展示字段，Parameters 深拷贝）；
- baseline=transcript 中已声明的工具状态（`getCurrentTools`：扫描 system 消息的 ToolsAdded/ToolsRemoved）；
- 差异 → `ToolsAdded`（新增或定义变化）/`ToolsRemoved`（移除或定义变化）；
- 若 pending 中有 system 消息，其 ToolAdded/Removed 视为意图，替换为相对 committed transcript 的 delta；否则在有差异时于首个非 system pending 前插入空文本 system 消息；
- 无差异且无 pending 差异 → 原样返回。
- 本阶段实现为 Core internal `ToolLoadout`；公共 `getCurrentTools`/`toToolDeclaration`/`getToolStateChanges` 仍归 D11/S9（形状锚 `TranscriptNormalization`），S7 不扩公共面。

### 4.4 工具批次

按 pi `agent-loop.ts:420-680`：

- 分发：任一工具 `ExecutionMode=Sequential` 或 config.ToolExecution=Sequential → 串行；否则并行（默认 Parallel）。
- 每次调用先发 `tool_execution_start`（args=原始 arguments）。
- **串行**：逐调用 预检→执行→finalize→发 `tool_execution_end`→创建 toolResultMessage→发 `message_start`+`message_end`；每步后检查 abort，abort 则停止后续调用。
- **并行**：预检严格顺序（含 before hook；immediate 结果立即 end）；通过预检的执行任务并发启动；`tool_execution_end` 按完成序发出；全部完成后按**助手源顺序**创建并 `message_start/end` 工具结果消息。
- `terminate`：最终化批次中**所有**结果 `Terminate=true` 才为真（空批 false）。
- **length 截断**：stopReason=`length` 时，整批 each 调用：发 start → 生成错误结果（文案逐字：`Tool call "<name>" was not executed: the response hit the output token limit, so its arguments may be truncated. Re-issue the tool call with complete arguments.`）→ 发 end + 消息事件；不执行。
- toolResultMessage：`Content = result.Content ?? []`（空数组归一）、`Details`、`Usage`、`IsError`、`DurationMs`、`Timestamp=now`；`Role="toolResult"`。

### 4.5 单调用管线（`RunToolCallAsync`）

按 pi `agent-loop.ts:680-960`：

1. 按名首匹配找工具；未找到 → 错误结果 `Tool <name> not found`（无引号，pi 文案）。
2. `prepareArguments`（若配置）替换 arguments；`ToolValidation.ValidateToolArguments(tool.Definition, preparedCall)`（含 argumentsError/coercion）。
3. `beforeToolCall`（参数校验后）：abort → `Operation aborted`；`Block=true` → 错误结果，reason 或默认 `Tool execution was blocked`；`Terminate` 传递。
4. `execute`：Stopwatch 计时；`onUpdate` 在 settle 前转发（settle 后忽略）；异常 → 错误结果（异常 message），duration 保留。
5. `afterToolCall`：逐字段合并（content/details/usage/terminate/isError）；`structuredContent`：after 给值用之，未给且 content 被替换 → 丢弃，否则保留；after 抛异常 → 错误结果。
6. outcome = (toolCall（prepare 后）, result, isError, durationMs?)；永不因工具失败抛。

### 4.6 D16：流式参数 partial 快照 + repairJSON（Ai）

按 pi `utils/streaming-json.ts`（559 行）+ `utils/json-parse.ts`（149 行）、pi.net 同名移植：

1. 新增 internal `StreamingJsonParser`：delta 追加、`Snapshot()` 返回当前 JSON 值；规则逐条对齐 pi（未闭合字符串/容器、转义挂起、字面量/数字前缀、快照不可变且共享已完成容器）。
2. 新增 internal `JsonParse`：`RepairJson`、`ParseJsonWithRepair`、`ParseToolCallArguments`（返回 `(JsonObject Arguments, string? ArgumentsError)`；错误文案 `message\n\nReceived arguments:\n<raw>`）。
3. `ToolCallArguments` 升级为 pi `ToolCallArgumentsStream` 语义：`Append(delta)→JsonObject partial`（变化才换对象）、`Replace(text)` 返回 delta+partial、`Finish()` 走 repair 解析；原始文本 `Text` 仍不进入 partial/transcript。
4. 三适配器接线（delta 回填，pi 行为）：
   - Anthropic `input_json_delta`：append → 更新块 `Arguments`；
   - OpenAI Completions `tool_calls.function.arguments` delta：append → 更新块；
   - OpenAI Responses `function_call_arguments.delta` + `output_item.done` 的 replace 路径：同步 partial；
   - Google 无流式参数（functionCall 整体）→ 不改。
5. `argumentsError` 语义：repair 后仍非法才落 ArgumentsError（错误文案含收到的原始参数）；可修复畸形不再判失败。
6. 现势钉更新：`ToolCallArgumentsTests.ToolCallArguments_Does_Not_Expose_Partial_Json` → 改为"部分快照生长"用例；新增 repair 用例；三适配器各一条 partial 快照事件用例。
7. D16 台账转 `已完成`，填完成提交与验收锚点。

---

## 5. 测试与验收

### 5.1 Core（Faux 驱动，无网）

`tests/PiUnityAgent.Core.Tests/Agent/`：

- `AgentLoopEventOrderTests`：单回合纯文本 → `agent_start/turn_start/message_start/message_update…/message_end/turn_end/agent_end`；`message_update` 只对助手；agent_end 最后且终值=新增消息。
- `AgentLoopToolTests`：Faux 脚本"toolCall→toolResult→文本"；工具结果消息进 context 且顺序正确；未知工具/参数非法/execute 抛异常 → isError 结果不炸循环，模型继续。
- `AgentLoopBatchTests`：串行 vs 并行（默认/任一 sequential 覆盖）；并行 end 完成序 vs 结果消息源序；`terminate` 全批语义；`stopReason=length` 整批失败文案。
- `AgentLoopHooksTests`：before block/reason/terminate；after 字段覆盖与 structuredContent 丢弃规则；after 抛异常→错误结果。
- `AgentLoopSchedulingTests`：`finishTurn` continue/end；`prepareRequest`/`prepareNextTurn` 替换 model/thinking/context；steering 在工具批后注入；follow-up 在内层退出后注入；one-at-a-time 不双投递；error/aborted 硬退出（turn_end+agent_end）。
- `AgentLoopLoadoutTests`：toolsAdded/toolsRemoved 系统消息；定义变化=removal+addition；无变化不插消息；pending system 字段替换。
- `RunToolCallTests`：同管线但无事件/无消息；onUpdate settle 后忽略；durationMs。
- `AgentLoopContinueTests`：空上下文/最后一条 assistant 的精确错误文案；续跑终值不含既有消息。
- `RunToolCallTests` 的 hook 直接调用；`DefaultStreamFn` 未配置错误文案/Set/Get。

### 5.2 Ai（D16）

- `StreamingJsonTests`：快照规则（未闭合字符串/容器/字面量/数字/转义）、快照不可变、增量与全量文本一致。
- `JsonParseTests`：repairJson 控制字符/非法转义；ParseToolCallArguments 成功/修复/失败文案（含 Received arguments）。
- `ToolCallArgumentsTests`：Append 返回 partial、Replace 两种分支、Finish repair 成功/失败。
- `OpenAiCompletionsTests`/`OpenAiResponsesTests`/`AnthropicMessagesTests`：toolcall delta 期间 partial 消息的 block.Arguments 渐进生长；repair 后无 argumentsError；不可修复仍落 argumentsError。
- 既有 1050 Ai 测试全绿（除被 D16 现势钉替换的那条）。

### 5.3 验收口径

- Faux 可跑通多轮 ReAct；无网络依赖。
- 事件顺序/批次语义/错误编码/hook 语义/S7 调度回调与 pi 对齐。
- D16 完成（台账状态、测试锚点更新）；`check-deferred --current-stage S7 --gate exit` 绿（D16 完成；D3 目标 S8 未到期）。
- S8 可基于 S7 的 `AgentLoop` + `AgentToolCallOutcome` 实现 `Agent` 门面，无需回改 S7 形状。

---

## 6. 三方偏离表（S7 收尾汇总；待独立审计核验）

| 项 | pi 原版 | pi.net | 我们 | 类别 | 原因 / 代价 |
|---|---|---|---|---|---|
| AgentMessage 联合 + 自定义消息 | `Message \| CustomAgentMessages`（声明合并） | `ChatMessage`（Ai Message） | Ai `Message`；自定义=继承 Message 子类 | A/B | C# 无声明合并；自定义消息须满足 Message 形状，由 convertToLlm 过滤；代价=应用自定义消息少一层自由度 |
| `AgentLoopConfig extends SimpleStreamOptions` | 结构继承 | `StreamOptions` 组合字段 | `StreamOptions` 组合字段 | B | C# record sealed 不能继承；调用方经 config.StreamOptions 传选项 |
| AbortSignal | `signal?: AbortSignal` | CancellationToken | CancellationToken（所有入口/回调） | A | C# 惯用法 |
| 异步回调 | `Promise<...>` | Task 委托 | Task 委托 | A/B | 同上 |
| AgentEvent 联合 | 10 字面量对象 | 抽象 record + sealed 变体 | 同 | A/B | C# 判别记录；wire 无 |
| AgentTurnDecision 联合 | `{action:"continue"\|"end"}` | 枚举 | 枚举 | A | C# 枚举；无 wire |
| AgentContext.messages 数组 | mutable array | `List<ChatMessage>` | `List<Message>` | B | 循环内原地追加 |
| `reasoning` / thinkingLevel | 字符串含 `"off"` | ThinkingLevel? + null=off | 同 + turn 用 ModelThinkingLevel | A/B | 我们 SimpleStreamOptions.Reasoning 无 Off 枚举值；null=关；turn/update 用含 Off 的 ModelThinkingLevel 表达显式关/不改 |
| 工具装载 diff | Ai `getToolStateChanges/toToolDeclaration` 公开 | 同（AgentLoop 内部实现） | Core internal `ToolLoadout`（D11/S9 仍持公开重放辅助） | D/C | 避免 S7 提前扩 Ai 公共面；代价=S9 需提升/迁移内部实现 |
| JSON 声明相等 | `JSON.stringify` 串比较 | 同 | 规范化 JSON 比较 | B | 无 JS 串化语义依赖；代价=需稳定键序 |
| hooks 归属 | AgentLoopConfig 内置 before/after | 同 | S7 落钩子类型与调用点；S8 只做 Agent 门面注册/队列 | D/scope | 路线图 S8 的 before/after 指门面层；循环调用点必须随执行管线一起落，否则 runToolCall 形状断裂 |
| `runToolCall` 参数 | options 对象 | 位置参数 | 位置参数；`tools` 为独立必填参数（=pi `RunToolCallOptions.tools`，独立于 `context.tools`） | B | C# 无对象字面量；签名含 hooks/onUpdate；tools 独立性系 2026-11-09 审计 U-1 修复，修复后与 pi 无此偏离 |
| Error 类型 | `Error` | InvalidOperationException | InvalidOperationException | B | 文案逐字；类型 C# 化 |
| Agent 流失败收场 | `.then(end)`（run reject 时流悬挂） | `PumpAsync` catch→Fail | `stream.Fail(error)` | D/B | 避免消费者悬挂；代价=与 pi 字面不同（pi 契约要求回调不抛） |
| `DefaultStreamFn` | 模块级可变单例 | 同 | 同（静态 Set/Get/IsConfigured） | B | 宿主注入缝；代价=进程级共享 |
| 计时 | `performance.now()` | Stopwatch | Stopwatch | A | 单调钟 |
| 时间戳 | `Date.now()` | DateTimeOffset.UtcNow | 同 | A/B | 毫秒时间戳 |
| ToolResultMessage.durationMs | number | long? | long? outcome → int? message | B | S1 消息字段为 int?；代价=超长任务截断（实际不可达） |
| D16 流式解析引擎 | pi streaming-json/json-parse | pi.net 已移植 | 移植 pi.net internal + 适配器接线 | B | 与 pi 行为差分锁快照/文案；代价=适配器三处修改 |
| proxy.ts | 服务端代理流 | 已移植 | 不做 | C/D22 | server RPC 路线非 S7；登记后续扩展；代价=代理模式需另立项 |
| 事件快照 | `{...partialMessage}` 浅拷贝 | 记录不可变 | 记录 `with` 快照 | A/B | C# record 不可变；无别名风险 |
| PollAsync 吞异常 | `getSteeringMessages` 抛错即 reject runAgentLoop（`.then(end)` 模式下流悬挂） | 同吞（AgentLoopRunner） | 回调抛错→捕获返回空列表继续 | D | pi.net 硬化；代价=钩子异常被静默降级为空轮询（2026-11-09 审计 U-3 登记） |
| 非有限数值 partial 快照 | 快照直持 Infinity/NaN（JS Number） | — | `SerializeToNode` 抛 `ArgumentException`→catch 降级为空对象 | D/低 | C# JsonNode 不承载非有限数值；代价=partial 期间该字段缺省，终点 repair 同受限于 JsonNode（审计 U-4 登记） |
| prepareArguments 非对象返回 | 非对象交 schema 校验（校验语义报错） | 直传 object（无强转） | `(JsonObject)` 强转→InvalidCastException 消息进错误结果 | D/低 | 局部小改未做；代价=极端入参下错误文案不同（仍为 isError 结果、不执行）（审计 U-8 登记） |
| QueueMode/双队列排空模式 | `types.ts:49-55` `"all"/"one-at-a-time"` 公共类型 + 排空点注入 | 无 | 无对应类型 | scope | 队列存储归 S8（§1.2 非目标），非后置裁剪；S8 plan 须显式承接该形状，否则成 shape gap（审计 U-9 登记 → 台账 D23） |

---

## 7. 风险与缓解

1. **循环状态机复杂**：双层循环 + explicitContinuation + 队列排空点是 S7/S8 最易错处。缓解=逐分支测试（scheduling 组）+ 与 pi 代码逐段比对；S8 只加队列存储。
2. **事件顺序**：并行批次 end/结果消息两种顺序。缓解=专项 BatchTests 双序断言。
3. **D16 适配器接线**：partial 快照可能破坏既有部分消息断言。缓解=先跑既有 1050 全绿基线，只改三类适配器 delta 分支；现势钉替换显式记录。
4. **Shape 漂移**：AgentLoopConfig/回调签名不可随 S8 改。缓解=形状先按 pi 全量落，S8 只加 Agent 门面。
5. **thinkingLevel 映射**：null/Off/不改三态易混。缓解=PrepareRequest/Update 用 ModelThinkingLevel，流选项用 ThinkingLevel?，转换函数单点 + 测试。
6. **计时/时间戳**：Stopwatch/UtcNow 在测试中不确定。缓解=只断言 duration 存在与 ≥0、timestamp 单调；不钉具体值。

---

## 8. 后置能力与台账影响

- **D16 承接**：本阶段完成 → 台账状态 `已完成`，填完成提交；现势钉测试改名/替换；D21 不受影响。
- **D22 新增**：`proxy.ts` 服务端代理流函数（目标=后续扩展）；形状锚=pi `agent/src/proxy.ts`；验收=Proxy 协议测试（待补）；S7 plan 落台账行与锚。
- D3（Deferred）目标 S8、D11 目标 S9、D17 目标 S8：均不到期；`check-deferred --current-stage S7` 通过。
- `ToolLoadout` internal 实现不构成 D11 子项完成；S9 提升时按 D11 验收。

---

## 9. 参考索引

| 内容 | pi 源 |
|---|---|
| 循环入口/双层循环 | `packages/agent/src/agent-loop.ts:47-330` |
| 工具装载公告 | `agent-loop.ts:330-410` |
| 助手流式响应 | `agent-loop.ts:410-500` |
| 截断/批次执行 | `agent-loop.ts:500-690` |
| 单调用管线/hooks | `agent-loop.ts:680-960` |
| 配置/事件/上下文形状 | `packages/agent/src/types.ts:47-551` |
| 默认流函数 | `packages/agent/src/stream-fn.ts` |
| D16 流式 JSON/repair | `packages/ai/src/utils/streaming-json.ts`、`utils/json-parse.ts` |
| 教学 | `docs/learning/S6-工具契约与注册-概念篇.md`；`how-pi-agent-works/source/agent-loop.md`；`pi-book/agent/02-end-to-end.md` |

pi.net 参考：`src/Pi.Agent/{AgentLoop.cs,AgentLoopRunner.cs,ToolExecution.cs,StreamFn.cs,Types/AgentTypes.cs}`；`src/Pi.Ai/Utils/{StreamingJson.cs,JsonParse.cs}`。

---

## 10. 完成定义

- Core `Agent/` 形状与 §3 一致；Release 0 警告 0 错误；
- Faux 循环测试全绿；Ai D16 测试全绿；既有 Ai 测试不回归；
- D16 台账 `已完成` + D22 行登记；`check-deferred S7 --gate exit` 绿；
- 独立偏离审计（SOP）= PASS / PASS_WITH_REGISTERED_DEVIATIONS，§6 表逐行与代码一致；
- S8 可仅凭 S7 公共形状实现 `Agent` 门面，无需回改。
