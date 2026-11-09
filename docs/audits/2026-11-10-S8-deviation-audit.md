# S8 偏离审查（2026-11-10）

> **审计者**：独立 subagent（GLM，独立会话，非实现者）
> **参考版本**：pi `v1.5.0`（`packages/agent/src/{agent.ts,types.ts,stream-fn.ts}`、`packages/ai/src/{models.ts,types.ts,utils/retry.ts,utils/transcript.ts,utils/text.ts,providers/faux.ts}`，本地 pi-study 检出）；pi.net `main`（`src/Pi.Agent/{Agent.cs,AgentOptions.cs,Types/AgentTypes.cs}`、`src/Pi.Ai/{Models/Models.cs,Models/CreateProvider.cs}`）；UnityLlmTransport 未涉及（S8 不引用通讯层）。版本与 spec §0 声明一致，未发现版本漂移。
> **范围**：`59475c8`（S8 分支基点，S7 收尾）.. `926b6b1`（HEAD）。Core `Agent/*` 门面、Ai `Utils/Retry.cs`（D17）、`Providers/{Models.Capabilities,Models.Streaming,ProviderCapabilities,ProviderFactory,StaticProvider}.cs`、`Providers/Faux/*`、`Types/StreamOptionTypes.cs`（D3）、S7 形状零回改核对；测试 `tests/PiUnityAgent.Core.Tests/Agent/*`、`tests/PiUnityAgent.Ai.Tests/{Providers/DeferredTests,Utils/RetryTests,Types/DeferredWindowTests}.cs`。
> **方法**：逐文件对照 pi 原版行为/形状（唯一真相源），pi.net 仅作 C# 写法佐证；spec §6 三方偏离表当作待核声明逐行回查；本审计者亲跑门禁（Release 构建 + Core/Ai 全量单测 + check-deferred），未复用控制器数字。

---

## 结论：**FAIL（仅登记层面；零 Shape gap、零 Critical/Important 代码缺陷）**

| 维度 | 结果 |
|---|---|
| Shape gap（公共形状丢失/语义篡改且不可归类 A/B/C/D） | **0** |
| Critical | **0** |
| Important | **0** |
| Minor（未登记偏离 U-1..U-8 + 已登记项的登记瑕疵 N-1..N-6） | **14** |
| spec §6 偏离表逐行与代码一致性 | 26 行中 25 行与代码一致；1 行（D3 不支持文案"逐字"）在 provider 层不成立（U-3） |

FAIL 判定依据：SOP §8 门禁要求"未登记偏离已补进 spec §8"——本轮发现 8 条未登记偏离（全部 Minor、全部为 A/B/D 可归类），按控制器指令"anything unregistered = FAIL item"记 FAIL。**修复动作全部是文档级（补 spec §6 行 / 订正 1 行表述），不需要改代码即可转 PASS_WITH_REGISTERED_DEVIATIONS**；其中 U-1/U-3 亦可选代码侧对齐（见各条建议）。

---

## 1. 公共形状矩阵（pi → pi.net → 我们）

判定口诀逐项走查；"一致"= 形状与语义均对齐，"登记"= spec §6 有对应行且与代码相符。

| 区域 | pi 原版（v1.5.0） | pi.net | 我们 | 判定 | 类别 |
|---|---|---|---|---|---|
| `QueueMode` | `"all" \| "one-at-a-time"`（types.ts:55） | 无此类型 | `enum QueueMode { All, OneAtATime }`（QueueMode.cs:9-16），wire 无表示 | 一致 | A（登记✓） |
| `PendingMessageQueue` | class 未导出（agent.ts:149-180）：enqueue/hasItems/peek/drain/clear + mode | public class | `internal sealed class`（QueueMode.cs:22-94），Peek=快照、Drain=Peek 后移除，语义逐点等价 | 一致 | B（登记✓） |
| `AgentState` | interface（types.ts:382-421）：systemPrompt(ro)/model/thinkingLevel/tools(get+set 复制)/messages(get+set 复制)/isStreaming/streamingMessage/pendingToolCalls(ReadonlySet)/errorMessage | 扁平挂在 Agent 上（Agent.cs:66-96，无 AgentState 对象） | 独立 `AgentState` 类（AgentState.cs:23-211），读写面按 pi；getter 快照、setter 复制顶层数组 | 一致（未照抄 pi.net 扁平化） | B/D（登记✓） |
| `AgentInitialState` | `Partial<Omit<AgentState, …>>` 5 字段（agent.ts:79-81） | 扁平进 AgentOptions | `sealed record` 5 字段同序（AgentState.cs:10-17） | 一致 | B/D（登记✓） |
| `pendingToolCalls` 类型 | `ReadonlySet<string>` | `IReadOnlyCollection` | `IReadOnlyCollection<string>` 快照（AgentState.cs:127-133） | 一致 | A/B（登记✓，netstandard2.1 无 IReadOnlySet） |
| 默认 model / thinking | `DEFAULT_MODEL` id/name/api/provider="unknown"、baseUrl=""、input=[]、cost 全 0、contextWindow/maxTokens=0（agent.ts:58-69）；thinking 默认 "off" | 同 | `DefaultModel()`（AgentState.cs:199-210，Reasoning 不设=false 同 pi）+ Off（AgentState.cs:38） | 一致 | B |
| `AgentOptions` | 22 成员（agent.ts:115-147） | 扁平 SystemPrompt/Messages/Model/Tools + required StreamFn | 逐成员对应（AgentOptions.cs:23-58），含 `InitialState`、两层 `PrepareNextTurn`/`PrepareNextTurnWithContext`、`SteeringMode/FollowUpMode` 默认 OneAtATime | 一致 | B/D（登记✓） |
| 门面可变选项 | 19 个 public 字段（agent.ts:200-236） | 同扁平 | 全部存在且类型对齐（Agent.cs:55-73）；`StreamFunction` 缺省回退 `DefaultStreamFn.Get()`（Agent.cs:31；pi agent.ts:244 同兜底）；Transport 默认 Auto、ToolExecution 默认 Parallel（Agent.cs:41,43 = pi agent.ts:259,261） | 一致 | B/D（登记✓） |
| `PrepareNextTurnContext` | `extends AgentTurnContext`（空扩展，types.ts:191；AgentTurnContext 4 字段 types.ts:135-144） | 同字段 | 同 4 字段 record（AgentOptions.cs:16-20） | 一致 | B（登记✓） |
| 两层 prepareNextTurn | 门面层只收 signal（agent.ts:128-130）；loop 层只收 context（agent.ts:494-502 包装） | 无 signal 变体 | 门面 `Func<CT, Task<AgentLoopTurnUpdate?>>`（AgentOptions.cs:41）+ S7 loop 层 `PrepareNextTurn(AgentTurnContext)` 零回改；包装器按 pi 归一（Agent.Run.cs:125-148，WithContext 优先、否则 signal 版，`Signal ?? None`） | 一致 | B/D（登记✓） |
| `Agent` 方法面 | state/subscribe/steer/followUp/clear*/hasQueued/peekQueued/signal/abort/waitForIdle/reset/prompt×3/continue（agent.ts:275-420） | 同 | 全部齐备（Agent.cs、Agent.Queues.cs、Agent.Run.cs）；无缺员、无降级 | 一致 | — |
| `Subscribe` 去重 | `Set.add` 天然去重、返回删除函数（agent.ts:275-278） | IDisposable | `_listeners.Contains` 去重 + `IDisposable`（Agent.cs:76-91,303-323） | 一致 | A/B（登记✓） |
| `Signal` | `AbortSignal \| undefined`（agent.ts:344-347） | `CancellationToken?` | `CancellationToken?`（Agent.cs:94-103） | 一致 | A（登记✓） |
| `Abort` | `abortController.abort()`（agent.ts:350-352） | CTS.Cancel | CTS.Cancel（Agent.cs:106-125） | 一致 | A/B（登记✓） |
| `WaitForIdle` | `activeRun?.promise ?? Promise.resolve()`；idle 等 agent_end listener 结算（agent.ts:359-361, 560-566） | Task | TCS `RunContinuationsAsynchronously`；FinishRun 先清 activeRun 后 TrySetResult（Agent.cs:128-134,227-243） | 一致 | A/B（登记✓） |
| `Reset` | 抛精确文案；baseline=`getCurrentSystemMessage`（折叠非取首条）；清运行态+两队列（agent.ts:363-377） | 同 | 同（Agent.cs:137-155），文案逐字同 pi；折叠走 `AgentTranscript.GetCurrentSystemMessage` | 一致 | B |
| `Prompt` 三重载 | string+images / 单条 / 批次（agent.ts:380-439）；单飞检查+精确文案 | 三重载 | 三重载（Agent.Run.cs:14-46）；文案逐字；images 空列表不追加块（等价 pi `length>0` 判空） | 一致 | B（登记✓） |
| `Continue` 分支 | 四分支+三条精确文案（agent.ts:393-420） | 同 | 同四分支+同文案（Agent.Queues.cs:55-113）；"全部 system"判定 `All(is SystemMessage)` = pi `every(role==="system")` | 一致 | B |
| `defaultConvertToLlm` | 按 role 字符串过滤四角色（agent.ts:39-47） | 同 | 按 `Role is system/user/assistant/toolResult`（Agent.cs:162-165） | 一致 | B（登记✓） |
| system 重放 | `createInitialSystemMessage`/`getCurrentSystemMessage`/`getCurrentSystemPrompt`（transcript.ts:11-95）+ `contentText`/`getSystemMessageText`（text.ts:6-21）公开 | Transcript 公开 | `internal AgentTranscript` 同算法：content 按 text 块 `\n` 连接、`\n\n` 累积、sections 按 name 覆盖/null 删、tools 用 `ToolLoadout.GetCurrentTools` 重放、空判定 `timestamp===null && tools==0`（AgentTranscript.cs:15-88）；`getSystemMessageText` 的"content+非 null section 值、滤空、`\n\n` 连接"逐点等价（AgentTranscript.cs:69-84） | 一致 | C/D11（登记✓，S9 公开） |
| 线程模型 | 单线程无锁 | 无锁 | `_gate` 保护同步段、锁内不 await（Agent.cs:18,167-187,245-295；spec §4.10） | 一致 | D（登记✓） |
| `IDeferredProvider` | 可选方法存在性 `provider.fetchDeferred?`（types.ts:327-332） | `SupportsFetchDeferred/SupportsCancelDeferred` 布尔（CreateProvider.cs:147-150） | 接口 + 两布尔 + 三方法（ProviderCapabilities.cs:26-48）；`Supports*` = 任一 Api/ApiByApi 实现非空（ProviderFactory.cs:85-89 = pi models.ts:1164/1177 `streams.some(...)`） | 一致 | B（登记✓） |
| `Models` deferred 面 | `streamDeferred/fetchDeferred/cancelDeferred`（models.ts:950-987） | 同 | `StreamDeferred/FetchDeferredAsync/CancelDeferredAsync`（Models.Capabilities.cs:11-78）；FetchDeferredAsync=Stream+WaitForDoneAsync（=pi `.result()`，models.ts:974） | 一致 | B |
| `DeferredHandle` | provider/modelId/api/id/expiresAt?/pollAfterMs?/data?（types.ts:539-547） | 同 | 7 字段全齐（Types/DeferredHandle.cs） | 一致 | — |
| `DeferredRequest` | `boolean \| {window?: "15m"\|"1h"\|"24h"}`（types.ts:382，运行时宽松） | 无 | `Disabled`/`Enabled(Window)` 枚举化 + 构造校验（StreamOptionTypes.cs:14-53）；Enabled↔truthy、Disabled↔falsy 语义等价 | 一致 | A/D（登记✓ Window 校验） |
| Faux deferred 选项/状态 | `deferred?: {pendingFetches?, pollAfterMs?}`；state `{callCount, deferredFetchCount, cancelledDeferred}`（faux.ts:472,484-496） | 同 | `FauxDeferredOptions{PendingFetches,PollAfterMs}`（Faux.cs:85-91）+ `FauxProviderState` 三件套+单调计数器（Faux.cs:93-118） | 一致 | B/D（登记✓确定性 id） |
| D17 `RetryPolicy/RetryCallbacks` | retry.ts:141-195：enabled/maxRetries/baseDelayMs/maxAgentDelayMs?；三回调 void\|Promise | Retry.cs | 形状逐字段对应（Retry.cs:11-25） | 一致 | A/B（登记✓） |
| D17 正则 | 两组 pattern（retry.ts:7-125）逐条 | 同 | **50 条 retryable + 11 条 non-retryable 全部逐字、同序**（Retry.cs:32-100，本审计逐条比对）；先 non-retryable 后 retryable、IgnoreCase（retry.ts:293-295 = Retry.cs:129-130） | 一致 | B（登记✓） |
| D22 proxy / D4/D5/D8/D11 | 不在 S8 范围 | — | 未动（ProviderFactory.cs:141-152 D4/D5 stub 原样；LazyStream observer 缺省 null 与 stream/streamSimple 同口径） | 一致 | C（台账在案） |

**结论：零 Shape gap。** 门面/Ai 公共面没有发现"pi 有而我们没有"的成员，也没有"我们有而语义被砍"的成员。

## 2. 行为 / wire / 生态

已核行为与 pi 对齐的证据（file:line）：

1. **初始化折叠**：`AgentState` ctor（AgentState.cs:35-47）= pi createMutableAgentState（agent.ts:83-112）：tools/messages 顶层复制、`createInitialSystemMessage` 仅当 prompt 非空或 tools 非空、首条非 system 才 unshift。
2. **单飞**：`RunWithLifecycle`（Agent.cs:167-202）先查后建 run（文案逐字 `"Agent is already processing."`），`Prompt` 路径另有 prompt 专属文案前置检查（Agent.Run.cs:39-43 = pi agent.ts:383-387），双查结构与 pi 相同。
3. **失败四事件**：`HandleRunFailure`（Agent.cs:204-225）合成消息字段（空 text 块、api/provider/model 取当前 state.model、Usage 全 0、StopReason=aborted?Aborted:Error、ErrorMessage、now 时间戳）与事件序列 message_start→message_end→turn_end([])→agent_end([msg]) 与 pi agent.ts:542-558 逐项一致；测试 AgentLifecycleTests.cs:134-164 钉序列与字段。
4. **listener 结算才算 idle**：`FinishRun` 在所有 processEvents 返回后才置结果（Agent.cs:189-201 finally → 227-243）；AgentLifecycleTests.cs:235-259 用慢 agent_end listener 钉住。
5. **状态归约**：`ProcessEvents`（Agent.cs:245-295）九类事件的归约面与 pi agent.ts:575-613 一致（agent_start/turn_start/tool_execution_update 不归约）。
6. **队列排空**：`PeekQueuedMessages` steering 优先（Agent.Queues.cs:45-49 = pi agent.ts:339-342）；`GetSteeringMessages` 的 `skipInitialSteeringPoll` 首轮返空复位闭包（Agent.Run.cs:90,107-115 = pi agent.ts:477,506-512）；`Continue` 先排 steering（skipInitialSteeringPoll:true）再 followUp（Agent.Queues.cs:70-110 = pi agent.ts:403-417）。
7. **钩子/config 组装**：`CreateLoopConfig`（Agent.Run.cs:73-118）九项与 spec §4.6/pi agent.ts:476-515 一致（reasoning Off→null、GetApiKey 适配忽略 ct、Identity 直传、每次 run 读取可变字段）。
8. **D3 Models 管线**：能力检查在 auth 之前（Models.Capabilities.cs:18-30 → 33-40 = pi models.ts:959-963 同序）；不支持的 deferred 走 LazyStream 终态 Error 不抛（Models.Capabilities.cs:16 = pi lazyStream 包装）；CancelDeferredAsync 抛 ModelsError(Provider)（Models.Capabilities.cs:52-78 = pi models.ts:977-987）；auth.BaseUrl 覆盖、ApplyAuthOptions（保 Wait）→ ApplyIdentity → ApplyTransformHeaders 末段剥离，组合等价 pi applyAuth（models.ts:856-915，transformHeaders 剥离 = `TransformHeaders=null`，Models.Streaming.cs:246-250）。
9. **D3 Faux**：请求侧存档→发射 Deferred 终态（FauxProvider.cs:136-143 = faux.ts:548-566）；fetch 侧 OnResponse(200,空头)→查 entry（provider/modelId/api 三键匹配）→cancelled→pendingFetches→final 单次解析缓存、submission 剥离 Deferred/CancellationToken/OnResponse（FauxProvider.cs:493-567,575-580 = faux.ts:593-634）；两条精确文案逐字（FauxProvider.cs:519,524 = faux.ts:612,615）；cancel 侧克隆 handle 入 CancelledDeferred+标 cancelled+OnResponse（FauxProvider.cs:603-620 = faux.ts:637-645）。
10. **D17 退避/取消/回调**：`RetryAssistantCall`（Retry.cs:133-221）的 aborted 直返、非 Error 直返、耗尽/不可重试直返、调度→睡→AttemptStart 顺序、睡眠取消归一为 `Aborted + ErrorMessage=null`、回调异常不吞——与 pi retry.ts:216-283 逐分支等价；`retryDelayMs` 指数+60s 封顶等价（Retry.cs:105-116 = retry.ts:162-172）。
11. **wire/生态**：无新 NuGet；Core→Ai 单向；netstandard2.1 API 纪律（无 IReadOnlySet/Task.WaitAsync/Lock）；Release 构建 0 警告 0 错误（本审计亲跑）。

---

## 3. 未登记偏离 / Shape gap（U-1..U-8，全部 Minor，无一构成 Shape gap）

### U-1 · Faux cancel 在取消令牌下抛 OCE，pi 的 cancel 永不抛
- **severity**：Minor（未登记 D 类加固）
- **证据**：我们 `FauxProvider.CancelDeferredAsync` 末行 `cancellationToken.ThrowIfCancellationRequested()`（src/PiUnityAgent.Ai/Providers/Faux/FauxProvider.cs:628）；经 `Models.CancelDeferredAsync` 直接外抛（Models.Capabilities.cs:76）。pi `cancelDeferred` 只做记录克隆+标记+onResponse，**不读 signal、永不抛**（faux.ts:637-651）。
- **为何不是 A/B/C**：pi 明确把 cancel 定义为 best-effort（types.ts:282-283 "best-effort"）；预先取消的令牌在 pi 下照样完成记录与回调。这是我们的 C# 取消约定加固，属 D 类，但 spec §6/§4.7.4 均未登记。
- **影响**：调用方在取消竞态下会收到 OperationCanceledException 而非正常完成；语义可辩护但与 pi 不同。
- **建议**：二选一——(a) 删 :628 行回到 pi 语义；(b) 在 spec §6 补一行登记为 D（"cancel 预取消令牌抛 OCE；pi best-effort 永不抛；代价=调用方需处理 OCE"）。

### U-2 · Faux fetch 错误终态的消息标识与时间戳口径与 pi 不同
- **severity**：Minor（继承自早期 Faux 约定，但 S8 deferred 错误路径复用了它）
- **证据**：我们 `EmitError(stream, model, …)` 用请求侧 `model.Api/Provider` 且 `Timestamp = 0`（FauxProvider.cs:197-208）；pi fetch 错误终态用 **provider 级** api/provider 常量 + `requestModel.id`、`timestamp: Date.now()`（faux.ts:678-681 + createErrorMessage faux.ts:322-336）。
- **为何不是 A/B/C**：Faux 默认配置下两者值相同，非默认 Api/Provider 的 Faux 实例会显现差异；也不属语言强制。
- **建议**：登记为已知口径差（备注在 spec §6 D3 Faux 行），或在 Faux 错误构造改用 provider 常量+时钟。

### U-3 · 工厂/静态 provider 层的 deferred 不支持文案与 pi 工厂文案不一致；spec §6 "逐字"声明在 provider 层不成立
- **severity**：Minor（错误文案 + spec 登记准确性）
- **证据**：pi 工厂层 fetch：``Provider ${input.id} does not support deferred responses for "${model.api}"``、cancel：``Provider ${input.id} cannot cancel deferred responses for "${model.api}"``（models.ts:1167-1172,1180-1185）；我们两处均为 `Provider {Id} does not support deferred responses`（ProviderFactory.cs:99-102,122-125；StaticProvider.cs:72-74,88-90）。**Models 层**文案我们与 pi 逐字一致（Models.Capabilities.cs:23,29,62,68 = models.ts:961,984，测试 DeferredTests.cs:193,202 钉住）。
- **为何不是 A/B/C/D**：这是文案语义差异，不属语言强制；spec §6 行"D3 不支持文案 | `Provider X does not support deferred responses` | 同 | 同 | B | 逐字"只覆盖 Models 层，provider 层与 pi 不逐字——该行**与代码/事实不符**，违反 SOP §8"每行与代码一致"。
- **建议**：改 spec §6 该行为两层表述（Models 层逐字 ✓；provider 层文案合并且不带 `for "<api>"`、cancel 不用 "cannot cancel"，登记为 B 偏离），或对齐 pi 工厂文案（需同步改 DeferredTests 断言）。

### U-4 · 工厂层 `StreamDeferred` 同步抛错，pi 工厂层包 lazyStream 得到终态 Error 流
- **severity**：Minor
- **证据**：pi 工厂 fetchDeferred 包 `lazyStream(model, async …)`，直接调 provider 即返回流、缺实现表现为流消费时的终态 Error（models.ts:1164-1176）；我们 `CreatedProvider.StreamDeferred` 直接 `throw new ModelsError`（ProviderFactory.cs:97-104），测试钉住同步抛（DeferredTests.cs:121）。经 `Models.StreamDeferred`（外层 LazyStream）消费时两者终态一致；**仅绕过 Models 直调 provider 时暴露差异**。
- **为何不是 A/B/C/D**：错误面编码差异，可归 B 但未登记。
- **建议**：在 spec §6 D3 工厂行补注"直调 provider 时同步抛 ModelsError；pi 为 lazyStream 终态 Error；经 Models 消费等价"。

### U-5 · `Continue` 排空竞态恢复（Prepend + 文案替换）是我们独有的 D 类加固，未登记
- **severity**：Minor
- **证据**：我们 `Continue` 在排空后启动失败时把消息插回队首并把异常文案换成 Continue 专属（Agent.Queues.cs:75-88,95-105），配套 internal API `PendingMessageQueue.Prepend`（QueueMode.cs:71-82，有测试 AgentQueueTests.cs:51-62）。pi 单线程无此路径（agent.ts:403-417 排空即跑）。
- **为何不是 A/B/C/D 之外**：属 D（并发加固，保消息不丢、错误更精确）；但 spec §6/§4.5 未登记，且 HEAD 提交 926b6b1 才引入。
- **影响**：与 pi 的可观察差异仅在并发竞态下出现（pi 不可能发生）；语义是加强而非削弱。
- **建议**：spec §6 补一行 D 登记（收益=并发 Continue 不丢消息；代价=新增 internal API 与文案替换路径）。

### U-6 · `Abort()` 吞 `ObjectDisposedException`
- **severity**：Minor
- **证据**：Agent.cs:117-124（提交 a2f78cc）。pi `AbortController.abort()` 可重复调用、无销毁概念（agent.ts:350-352）；C# CTS 在 FinishRun 中 Dispose（Agent.cs:241）后 Cancel 会抛。
- **建议**：spec §6 "线程安全"行补一句涵盖。

### U-7 · listener 迭代用快照，pi 的 Set 迭代会把当前事件投递给派发期间新订阅的 listener
- **severity**：Minor（极端边缘）
- **证据**：我们锁内 `_listeners.ToArray()` 后迭代（Agent.cs:281-289）；pi `for (const listener of this.listeners)` 用 Set 活迭代（agent.ts:619-621），JS Set 在迭代期间新增元素会被当次迭代访问到。
- **为何不是 A/B/C/D**：C# 下活迭代遇并发修改会抛，快照是必要选择，可归 A；未单独登记（可并入 §6 "线程安全"行）。
- **建议**：并入线程安全行的代价描述。

### U-8 · `turn_end` 的 errorMessage 归约：空串口径与 pi 不同
- **severity**：Minor
- **证据**：pi `if (event.message.role === "assistant" && event.message.errorMessage)`——truthy 判断，空串**不**记录（agent.ts:604-607）；我们 `if (turn.Message.ErrorMessage is not null)`——空串**会**记录（Agent.cs:267-271）。role 检查我们由 `AgentEvent.TurnEnd(AssistantMessage,…)` 类型结构保证（AgentEvent.cs:15），等价；仅空串边界不同。
- **建议**：改为 `!string.IsNullOrEmpty(...)`（一处、零风险）或登记为 D。

---

## 4. 已登记 / 次要发现（N-1..N-6）

- **N-1**：`AgentState`/队列的 null 守卫抛 `ArgumentNullException`（AgentState.cs:60,78,95；QueueMode.cs:45）。pi 对 null 无此检查。C# 惯用加固，极次要；建议并入 §6 线程安全/D 行一笔带过。
- **N-2**：`RetryDelayMs` 把负 baseDelayMs/负 cap 钳 0（Retry.cs:108,114）；pi 透传负值由 setTimeout 钳 0。**净行为等价**（都不会真睡），代码注释已说明、台账 D17 备注已记"负 MaxAgentDelayMs 钳 0"，但 spec §6 无行。建议补一行 D。
- **N-3**：正则多加 `RegexOptions.CultureInvariant`（Retry.cs:226）；pi 只有 `"i"`。确定性加固，§6 行写"逐字移植 + IgnoreCase"未提 CultureInvariant——表述补全即可。
- **N-4**：provider 层新增 `FetchDeferredAsync`（ProviderFactory.cs:107-112；StaticProvider.cs:76-81）。pi provider 只有返回流的 `fetchDeferred`（`.result()` 便捷只在 Models 层）。纯增量，无形状损失；建议 §6 一行注明"additive"。
- **N-5**：`Models.StreamDeferred` 未传 LazyStream observer（pi 传 `observeModelRequest(model, options, true)`，models.ts:963）。与 stream/streamSimple 同口径（均 null），属 D8 遥测后置的既有边界，台账 D8 在案——无需动作，审计附注留痕。
- **N-6**：`DefaultStreamFn.IsConfigured`（DefaultStreamFn.cs:25）为 pi 没有的增量成员。纯增量；可在 PORTING/§6 顺带注明。

## 5. C 类 Dn 检查（台账真相）

| 项 | 台账声明 | 核验结果 |
|---|---|---|
| D3 | 状态 `已完成`；完成提交 `0f9f291`/`3e198c0`/`ca663e7`/`6f2ec37`；锚点=DeferredTests 三类 + DeferredWindowTests | 提交全部在 `git log`；四个测试类存在且断言实质（精确文案/计数/缓存/取消）；`check-deferred.py --current-stage S8 --gate exit` 本审计亲跑 **exit 0**，D3 无残留 stub；旧钉 `ModelsCapabilityTests.Deferred_Methods_Are_Deferred_To_S8` 已删（ModelsCapabilityTests.cs 仅剩 2B 钉） |
| D17 | 状态 `已完成`；完成提交 `67f3ad0`；锚点=RetryTests；"pi agent-core 不消费，调用方 2A" | 提交在；RetryTests 9 用例实质（含回调时序逐串断言 RetryTests.cs:109-118）；`Api/ProviderRetry.cs` 仅注释改动（引用 D17 去钉，a333e2c），请求级重试未合并 ✓ |
| D23 | 状态 `已完成`；完成提交 `5ef3e19`/`efe9fe1`；锚点=AgentQueueTests/AgentQueueIntegrationTests | 两测试类在，one-at-a-time 不双投递/all 全排空/Continue 排空均有实质断言 |
| D11/D22/D4/D5/D8 | 未到期 | check-deferred 输出 D11=1、D22=1、D4=2、D5=2、D8=1 个标记，均为登记在案的 TODO/注记，无越期项 |

## 6. S7 形状零回改 + 门禁证据（本审计亲跑）

- `git diff 59475c8..HEAD -- src/PiUnityAgent.Core/Agent/{AgentLoop,AgentLoop.Run,AgentLoop.Stream,AgentLoop.Identity,AgentLoopConfig,AgentEvent,AgentContext,AgentLoopTypes,DefaultStreamFn,ToolCallHooks,ToolExecution,ToolLoadout,AgentToolCallOutcome}.cs` → **空 diff**；Ai 侧仅 `Api/ProviderRetry.cs` 注释行（a333e2c）+ D3/D17 计划内文件。
- `dotnet build PiUnityAgent.slnx -c Release` → **0 警告 0 错误**（2026-11-10 本机重跑）。
- `dotnet test tests/PiUnityAgent.Core.Tests/…` → **95/95 通过**；`dotnet test tests/PiUnityAgent.Ai.Tests/…` → **1132/1132 通过**。
- `python scripts/check-deferred.py --root . --current-stage S8 --gate exit` → **OK / exit 0**。
- 测试非空洞抽检：AgentStateTests（快照/折叠/重放）、AgentQueueTests（两排空点文本断言）、AgentLifecycleTests（四事件序列+精确文案+慢 listener）、AgentFacadeTests（钩子计数/可变字段次 run 生效/Signal 有无）、DeferredTests（句柄/缓存计数/取消/精确错误文案）、RetryTests（回调序列逐串）、DeferredWindowTests（精确异常文案）——均为行为断言，非编译期摆设。

## 7. 必需动作

- [ ] **spec §6 补登记**：U-1（cancel 预取消抛 OCE）、U-2（Faux 错误终态标识/时间戳口径）、U-4（provider 层同步抛错）、U-5（Continue 竞态恢复+Prepend）、U-6（Abort 吞 ObjectDisposedException）、U-7（listener 快照迭代，可并入线程安全行）、N-1/N-2/N-3/N-4/N-6（一笔带过即可）。
- [ ] **spec §6 订正 1 行**：U-3（D3 不支持文案行改为两层表述，"逐字"仅 Models 层成立）。
- [ ] **可选代码侧（非阻塞）**：U-1 删 FauxProvider.cs:628；U-8 改 `!string.IsNullOrEmpty`；U-3 对齐 pi 工厂文案。任一改动后按 SOP 由**新的**独立审计者复核。
- [ ] 复核通过后按 SOP §8 完成门禁，再走实机/合并批准。

## 8. 审计者附注

- 版本：pi 参考=本地 pi-study 检出（spec 声明 v1.5.0/f9a263655）；pi.net=main（spec 声明 2d6757b）。本次核对未发现版本标记冲突（faux deferred 只有 faux 导出、Supports* 形状等均与 spec 引用一致）。
- 门禁数字为本审计者亲跑，与控制器声明一致；测试均为 Debug 配置默认运行（与计划门禁命令一致）。
- 误报说明：实现者 spec §3.2 声明"默认模型 Cost=0"——已核 `UsageCost`/`ModelCost` 默认构造即全 0，成立；spec §4.7.1"剥 DeferredFetchOptions.Wait 之外全保留"与代码 `options with {ApiKey,Headers,Env}` 一致，成立。
- 无法验证项：D17 正则在 .NET 与 V8 引擎下的极端语料差异（§6 已登记"验收用行为语料"）；本审计以 61 条 pattern 逐字一致 + 10 组行为语料（RetryTests.cs:31-43）为证据。
