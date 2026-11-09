# S7 偏离审查（2026-11-09）

> 审计者：独立 subagent（GLM，经 Claude Code Agent 会话；非实现者，只读审计）
> 参考版本（实核 `git log -1`，非采信声明）：
> - pi 原版 `v1.5.0` / `f9a263655`（`D:/shuoshu/taiwuask/pi-study/pi`，实核 = `f9a263655 Release v1.5.0`）——行为/形状/wire 唯一真相源
> - pi.net `main` / `2d6757b`（实核 = `2d6757b chore(memory): …`）——仅 C# 表达参考
> - UnityLlmTransport `4c0cc4b` / `0.6.0`——S7 零引用，仅核边界（grep 实证见 §2.3）
> - Unity 2022.3 + netstandard2.1——仅 A 类硬约束依据
> 范围：`e7ef6c0..cf1ab0d`（分支 `feature/s7-agent-loop`，10 commits；HEAD 实核 `cf1ab0d`）
> 门禁复跑（本审计亲跑，非转抄）：Release 构建 0 警告 0 错误；Ai 1096/1096；Core 57/57；`check-deferred --current-stage S7 --gate exit` OK（registry 22，D16 无 stub，D22=1）
> **结论：FAIL**（2 项 Important：1 项 Shape gap + 1 项行为偏离未登记进 spec §6；均修复/登记成本低，见 §5）

---

## 1. 公共形状矩阵

对照 pi `packages/agent/src/types.ts`（551 行）、`agent-loop.ts`（959 行）、`stream-fn.ts`（20 行）、`packages/ai/src/utils/streaming-json.ts`（559 行）、`json-parse.ts`（149 行）。"我们"行号以 HEAD `cf1ab0d` 为准。

### 1.1 Core 形状（spec §3）

| # | 区域 | pi 原版 | pi.net | 我们 | 判定 | 类别/缺口 |
|---|---|---|---|---|---|---|
| 1 | `AgentContext` | `types.ts:514-519`：`messages: AgentMessage[]`（可变数组）+ `tools?: AgentTool[]` | 同构 record | `src/PiUnityAgent.Core/Agent/AgentContext.cs:6-10`：`required List<Message> Messages` + `IReadOnlyList<AgentTool>? Tools` | 一致；`AgentMessage` 联合折叠为 `Message`（C# 无声明合并），自定义消息=继承 `Message` 子类 | **A/B**（spec §6 行1 已登记） |
| 2 | `AgentLoopConfig extends SimpleStreamOptions` | `types.ts:193-342`：结构继承 + 全部回调字段（convertToLlm/transformContext/getApiKey/finishTurn/prepareRequest/prepareNextTurn/getSteeringMessages/getFollowUpMessages/toolExecution/beforeToolCall/afterToolCall） | `StreamOptions` 组合 | `AgentLoopConfig.cs:6-22`：12 字段一一对应 + `StreamOptions` 组合（行20）+ `Identity`（行21，对应 pi `config.identity`，types.ts 无此字段但 agent-loop.ts:415 消费） | 逐字段核对无缺漏；`toolExecution` 默认 parallel 一致（行17 vs `types.ts:317`） | **B**（spec §6 行2 已登记） |
| 3 | `AgentEvent` 10 变体 | `types.ts:528-551`：agent_start/agent_end(messages)/turn_start/turn_end(message,toolResults)/message_start/message_update(message,assistantMessageEvent)/message_end/tool_execution_start(toolCallId,toolName,args)/tool_execution_update(+partialResult)/tool_execution_end(+result,isError,durationMs?) | 抽象 record + sealed 变体 | `AgentEvent.cs:8-23`：10 变体字段一一对应；`DurationMs` long?、`Args` JsonObject、`Result`/`PartialResult` AgentToolResult | 完整；判别记录替代字面量联合 | **A/B**（spec §6 行5 已登记） |
| 4 | `AgentEventSink` | `agent-loop.ts:35` `(event) => Promise<void> \| void` | 同 | `AgentEvent.cs:25` `delegate Task AgentEventSink(AgentEvent)` | Task 化（不可同步） | **A/B**（spec §6 行4 覆盖） |
| 5 | `AgentTurnDecision` | `types.ts:147` `{action:"continue"\|"end"}`，undefined=正常调度 | 枚举 | `AgentLoopTypes.cs:5` `enum { Continue, End }` + 委托返回 `Task<AgentTurnDecision?>`（行30），null=undefined | 一致 | **A**（spec §6 行6 已登记） |
| 6 | `AgentTurnContext` | `types.ts:135-144`：message/toolResults/context/newMessages | 同 | `AgentLoopTypes.cs:7-11` 四字段对应 | 一致 | 无偏离 |
| 7 | `PrepareRequestContext` / `AgentRequestUpdate`（= Omit<TurnUpdate,"messages">） | `types.ts:173-189` | 同 | `AgentLoopTypes.cs:13` / 行23-28（无 Messages 字段） | 一致 | 无偏离 |
| 8 | `AgentLoopTurnUpdate` | `types.ts:161-170`：context?/messages?/model?/thinkingLevel? | 同 | `AgentLoopTypes.cs:15-21` 四可空属性 | 一致 | 无偏离 |
| 9 | `FinishTurn`/`PrepareRequest`/`PrepareNextTurn` 签名 | `types.ts:155-158/186-189/278-280`（AbortSignal 可选） | CT 委托 | `AgentLoopTypes.cs:30-32`：CT 必填（非可空） | AbortSignal→CancellationToken | **A**（spec §6 行3 已登记） |
| 10 | `BeforeToolCallContext/Result`、`AfterToolCallContext/Result` | `types.ts:66-132`：before{block?,reason?,terminate?}；after{content?,details?,structuredContent?,isError?,usage?,terminate?}；contexts 含 assistantMessage/toolCall/args/context(+result/isError) | 同 | `ToolCallHooks.cs:7-18`：字段一一对应（args 为 `JsonNode?` = TS `unknown` 的擦除） | 完整 | **A/B**（spec §6 行4 覆盖） |
| 11 | `AgentToolCallOutcome` | `types.ts:449-455`：toolCall/result/isError/durationMs?（execute 未跑则缺省） | 同 | `AgentToolCallOutcome.cs:6-9`：位置参数 + `long? DurationMs`；immediate 路径 DurationMs=null（`ToolExecution.cs:62`） | 一致 | 无偏离 |
| 12 | `DEFAULT_AGENT_IDENTITY` | `agent-loop.ts:41` `{attribution: PI_ATTRIBUTION}` | 同 | `AgentLoop.Identity.cs:8-9` `Attribution = ClientIdentityHelpers.PiAttribution`；`PiAttribution` 内容实核 = `("pi","https://pi.dev",["cli-agent"])`（`ClientIdentityHelpers.cs:19-20`）= pi `utils/identity.ts:24` | 一致 | 无偏离 |
| 13 | `agentLoop` / `agentLoopContinue` 入口 | `agent-loop.ts:47-70/80-109`：返回 EventStream，终态=agent_end，终值=messages | 同 | `AgentLoop.cs:17-43`：`Loop`/`Continue`，`CreateAgentStream`（行120-123）终态判定/取值同构；Continue 同步守卫先于建流（行39）= pi 行86-91 同步 throw | 一致（守卫文案逐字，见 §2.1） | 无偏离 |
| 14 | `runAgentLoop` / `runAgentLoopContinue` | `agent-loop.ts:111-160` | 同 | `AgentLoop.cs:46-90`：initialMessages 声明→新 context（原数组不动）→agent_start→turn_start→初始消息 start/end→RunLoop；续跑不复制消息数组（pi 行153 浅拷贝同义） | 一致 | 无偏离 |
| 15 | `runToolCall`（公共） | `agent-loop.ts:801-829`：`RunToolCallOptions{tools,assistantMessage,context,signal?,onUpdate?,beforeToolCall,afterToolCall}`——**`tools` 是独立必填项**，解析 against `options.tools`（行823），可与 `context.tools` 不同 | 位置参数、tools 折进 AgentContext | `ToolExecution.cs:51-73`：`RunToolCallAsync(toolCall, assistantMessage, context, config, signal, onUpdate=null)`，`PrepareToolCall` 用 `context.Tools`（行273） | **`tools` 选项无对应成员**；hooks 折进 config 尚可用 record `with` 克隆表达，tools 与 context 无法再分离 → **Shape gap**（详见 §3 U-1） | **Shape gap（Important）** |
| 16 | `ExecuteToolCalls` internal | pi 未导出（模块内函数） | pi.net 导出 | `ToolExecution.cs:25` internal | 一致；但嵌套 record `ExecutedToolCallBatch`/`ParallelSlot`/`PreparedCall` 为 public（行20/151/247-254），见 §3 U-6 | **B/内部面**（spec §3.5"批次 internal"声明与嵌套类型可见性不符，Minor） |
| 17 | `DefaultStreamFn` | `stream-fn.ts:3-20`：模块级单例 setDefaultStreamFn/getDefaultStreamFn（未配置 throw Error） | 同 | `DefaultStreamFn.cs:11-26`：Set/Get/IsConfigured；未配置抛 InvalidOperationException | 一致；错误文案方法名片段不同，见 §3 U-5 | **B**（spec §6 行15 已登记；文案声明过强，Minor） |
| 18 | `ToolLoadout`（declareToolChanges） | `agent-loop.ts:342-372` + `ai/utils/transcript.ts:58-70/123-165` | pi.net 简化版（弃用） | `ToolLoadout.cs:17-62`：systemIndex 倒扫→baseline 剥意图→getCurrentTools→差异→pending system 替换 / 首个非 system 前插入 / 快路径返回原列表 | 逐分支等价（含 `DeclaresNoToolChanges` 行65-67 对 TS `!pending.toolsAdded?.length` 的 null/空表双语义，行44）；diff 比较=规范化 JSON（行121-159，四字段键序固定） | **D/C**（spec §6 行9 已登记；JSON 相等 B 行11 一致） |
| 19 | `QueueMode` | `types.ts:47-55`：`"all"\|"one-at-a-time"` 公共类型（队列排空点注入数） | pi.net 无 | 无对应类型 | 队列存储归 S8（spec §1.2 非目标），但**任何台账/Dn 均未跟踪**该形状 | **C 类缺口风险**（Minor，见 §3 U-9） |
| 20 | `AgentState`/`Agent` 门面 | `types.ts:382-421`、`agent.ts`（623 行） | 未做 | 未做 | spec §1.2 明确 S8 非目标 | 范围内无偏离 |

### 1.2 Ai/D16 形状（spec §4.6）

| # | 区域 | pi 原版 | pi.net | 我们 | 判定 | 类别/缺口 |
|---|---|---|---|---|---|---|
| 21 | `StreamingJsonParser` | `streaming-json.ts:109-445`：append/snapshot；open-path 复制+拷贝信用（256/8 字符）；MAX_REPORTED_DEPTH=32；MAX_PENDING_SCALAR_LENGTH=64；NUMBER 两条正则 | pi.net 版存在 | `src/PiUnityAgent.Ai/Utils/StreamingJson.cs:47-679`：逐常量对齐（行50-66 两个上限与信用值同值）；读串/转义/容器/标量/快照复制逐规则等价（实核 ReadString/ContinueEscape/OpenContainer/CloseContainer/Place/CopyOpenPath 与 TS 逐一对应） | 忠实移植；`__proto__` 防御（TS 行552-558）在 C# Dictionary 下天然成立（无原型链），非偏离 | **B**（spec §6 行19 已登记；"以 pi TS 为准"落实） |
| 22 | `JsonChange` | `streaming-json.ts:51-58`：set/append | 同 | `StreamingJson.cs:16-23`：Set/Append + Path | set 变更延长语义（行342-347 ReferenceEquals 尾检查）与 TS 行318-322 等价 | **B** |
| 23 | `ToolCallArgumentsStream` | `streaming-json.ts:452-500`：text/append/replace/finish/#partial | 同 | `StreamingJson.cs:685-737`：Replace 前缀接续（行713）= TS 行475；非对象快照显示为 {}（行734）= TS 行493-497 | 一致 | **B** |
| 24 | `repairJson` | `json-parse.ts:33-84` | 同 | `JsonParse.cs:33-91`：控制字符转义/非法转义加倍/`\u` 四位校验逐字对应（行65-74 vs TS 行61-74） | 一致；`next=='\0'` 复用 NUL 判尾与 TS `undefined` 判尾在输出上等价（实核两分支同输出 `\\`+原位续读） | **B** |
| 25 | `parseJsonWithRepair` / `parseToolCallArguments` | `json-parse.ts:86-120`：非对象/数组/null → "Tool arguments must be a JSON object"；错误文案 `${message}\n\nReceived arguments:\n${json}` | 同 | `JsonParse.cs:94-132`：异常类型 InvalidOperationException（行123）；文案模板逐字（行130）；空/空白→空对象（行116 vs TS 行107） | 一致（底层 parser 报错文本为 .NET JsonException 措辞，A 类不可比文案） | **A/B**（spec §6 行14 已登记） |
| 26 | `parseStreamingJson` | `json-parse.ts:129-149`：**partial-json npm 库**容错解析 | pi.net 有实现 | `JsonParse.cs:138-248`：自制括号补全/悬挂修剪候选链（ParsePartial/ParsePartialTrimmed） | **非 pi 行为**：自制补全器远弱于 partial-json。但 S7 主链（ToolCallArgumentsStream）不消费它——实核其唯一调用面是遗留兼容；pi 侧 parseStreamingJson 在 v1.5.0 主链同样已让位 StreamingJsonParser | **B/D 观察项**（见 §6 附注 4；不涉 S7 行为面） |
| 27 | `ToolCallArguments`（适配器态） | pi 无此类（各 provider 直持 ToolCallArgumentsStream） | — | `src/PiUnityAgent.Ai/Api/ToolCallArguments.cs:12-52`：包装为 JsonObject 视图（S5 既有内部件升级） | 形状=pi ToolCallArgumentsStream 语义 + JsonObject 投影；非有限数值降级为 D（§3 U-4） | **B + D（U-4 未登记）** |
| 28 | 三适配器接线 | anthropic-messages.ts:795/829；openai-completions.ts:655/455；openai-responses-shared.ts:559-564/730/735-737/775-776 | — | `AnthropicMessages.cs:672/711-713`；`OpenAiCompletions.cs:595/447-448`；`OpenAiResponses.cs:1379-1388/1605/1619-1624/1692-1699` | 与 pi 七个调用点逐一核对一致：append 回填块、finish=Object.assign 等价、Responses `arguments.done` replace 回填 partial+条件推 delta（行1624 空串不推=pi 行737 真值判断）、`output_item.done` replace 不回填（行1693=pi 行775） | **B**（spec §6 行19 覆盖） |

---

## 2. 行为 / wire / 生态

### 2.1 行为语义（对照 pi `agent-loop.ts`）

- **双层循环分支**：`AgentLoop.Run.cs:27-155` 与 pi 行188-330 逐段核对——内层条件 `hasMoreToolCalls||pending>0`（行32=pi 行192）、prepareNextTurn 仅非首回合+空队列补轮询+turn_start（行35-53=pi 行194-217）、prepared/pending 经装载公告注入并各发 start/end（行56-66=pi 行220-225）、prepareRequest 首请求也调（行69=pi 行228）、error/aborted 硬退出（finishTurn→turn_end→agent_end，行86-96=pi 行254-265）、length 整批失败（行107-110=pi 行276-279）、finishTurn 在 turn_end 前（行122-124=pi 行295-296）、explicitContinuation 置位/清零（行132-135=pi 行303-307）、follow-up 续外层（行139-145=pi 行310-317）、context-only turn（行148-152=pi 行319-323）、agent_end 恒最后（行157=pi 行329）。**一致**。
- **thinking 档位三态**：`ApplyThinkingLevelUpdate`（Run.cs:179-192）= pi 行199-208/238-247 的 `undefined→保留 / "off"→undefined` 映射（Off→null）；`EffectiveThinkingLevel`（Stream.cs:157-158）= pi 行232 `config.reasoning ?? "off"`。**一致**（A/B，spec §6 行8）。
- **事件顺序与快照**：message_update 携带事件+快照（Stream.cs:108=pi 行445-449）；终态取流终值并保证 start/end 不变式（Stream.cs:76-100 与 fallback 行123-135=pi 行453-478 逐句一致）；**报告披露的"事件迭代不挂 CT"实为与 pi 等价**（pi `for await` 同样无 signal，signal 在流选项内；前提已验证：LazyStream/AnthropicMessages.cs:199-200/309 观察 `options.CancellationToken` 产出终态 Error/Aborted）。
- **getApiKey 回退**：`Stream.cs:33-37` JS 真值 `||` 语义（空串/None 回退）= pi 行409-410；测试 `GetApiKey_Empty_String_Falls_Back_To_StreamOptions_ApiKey`（AgentLoopTests.cs:699）钉住。e756911 修正核实到位。
- **工具批**：串行 end/消息逐调用后发+abort 截停（ToolExecution.cs:111-125=pi 行551-588）；并行预检顺序、immediate 内联 end（行170-179=pi 行606-626）、thunk 入口中止（行199-203=pi 行630-637）、end 按完成序（行217-227=pi 行656-658 完成序等价；发射点移到收割循环，序不变——task-4 报告披露，语义无差）、结果消息按源序（行232-240=pi 行660-664）、**预检中止跳过的调用整条丢弃无消息（行185 break=pi 行623-653 同款，非偏离）**、terminate 全 true（行471-472=pi 行700-702）、length 截断文案逐字（行87-89=pi 行503，测试 `TruncatedToolCallsAllFail` AgentLoopTests.cs:407 钉全文）。
- **runToolCall 管线**：未知工具文案 `Tool <name> not found` 无引号（行275=pi 行731）；prepareArguments→校验→before hook→abort 探测（钩子 await 后行297-298、返回 ready 前行311-312=pi 行748-753/767-772）→执行→after hook 逐字段合并+structuredContent 丢弃规则（行444-456=pi 行894-906）；onUpdate settle 后忽略（行372-409=pi 行836-866，含 duration 先测于等更新的顺序）。**一致**（3b2edff 补齐 abort 探测核实到位）。
- **continue 守卫**：`AgentLoop.cs:111-117` 两条文案与 pi 行87/91 逐字一致；`Continue_Rejects_Assistant_Last_Message`（SchedulingTests.cs:96）钉住。
- **装载公告**：快路径原对象返回（ToolLoadout.cs:44-45=pi 行364）、pending system 替换（行46-47=pi 行365）、插入位=首个非 system（行59-60=pi 行369-371）、定义变化=移除+新增（行103-108=transcript.ts:150-165）。
- **D16 partial/repair**：Append 返回 partial 快照、Replace 双分支、Finish 走 repair；argumentsError 仅在 repair 后仍非法时落（JsonParse.cs:114-132）；三适配器 delta 回填（见 §1.2 #28）。**一致**。

### 2.2 wire 契约

无新增 wire：`AgentEvent`/回调均为 C# 进程内形状；工具声明仍走 SystemMessage 的 `ToolsAdded/ToolsRemoved`（S6 定案，pi `SystemMessage` 同名字段）；截断/未找到/阻断/中止四类工具错误文案与 pi 逐字一致（ToolExecution.cs:89/275/301-307/202/258）；StopReason 枚举 wire 映射未触碰（StopReason.cs:26-38，S4 既有）。**无偏离**。

### 2.3 生态边界

- Ai/Core 均无 `UnityEngine`/`UnityLlmTransport`/`GameData`/`HarmonyLib` 引用；grep 命中的 4 处 `HttpClient` 全为注释（Providers/Auth/*.cs），无代码引用。**通过**。
- TFM：`PiUnityAgent.Ai.csproj:4` / `PiUnityAgent.Core.csproj:4` 均 netstandard2.1。**通过**。
- 无新增 NuGet：Ai 仅既有 `System.Text.Json 8.0.5` + `System.Threading.Channels 8.0.0`；Core 零 PackageReference。**通过**。
- D16 类型 internal：`StreamingJson.cs:47/685`、`JsonParse.cs:11`、`ToolCallArguments.cs:12` 全 internal；IVT 仅对测试程序集（`PiUnityAgent.Ai/Properties/AssemblyInfo.cs:3`、`PiUnityAgent.Core/Properties/AssemblyInfo.cs:3`）。**通过**。
- netstandard2.1 适配真实：`Stopwatch.GetElapsedTime`→实例计时（ToolExecution.cs:360-368，注释说明）、`GeneratedRegex`→静态 Regex（StreamingJson.cs:60-66）、`Task.WhenEach`→WhenAny 收割（ToolExecution.cs:217-227）。均为 A 类强制，非偷懒。

---

## 3. 未登记偏离 / Shape gap

> 判定口诀逐条过：pi 有→我们有（等价表达=A/B）；有但我们没实现=C（需 Dn）；有但我们删/改了语义且不在 A/B/C/D=Shape gap。

### Shape gap（Important）

- **U-1 `runToolCall` 的独立 `tools` 选项被折叠进 `AgentContext.Tools`**
  - pi：`agent-loop.ts:801-810` `RunToolCallOptions.tools`（必填、独立于 `context`），行823 `prepareToolCall(..., options.tools)`；pi 自身消费者即以不同值使用（coding-agent `agent-session.ts:764-765`：`tools: this._getCallableTools()` vs `context.tools: this.agent.state.tools`）。
  - 我们：`ToolExecution.cs:51-59` 签名无 tools 参数，行273 固定取 `context.Tools`——调用方无法"针对与 context 不同的工具集解析调用"，且 hooks 收到的 `context` 与解析所用工具集被迫绑定。
  - 为什么不是 A（C# 无此强制）、不是 B（pi.net `ToolExecution.cs:49-58` 同样折叠——照抄 pi.net 对 pi 的偏离，违反 docs/03 §1.3 B 条"pi.net 偏离 pi 时不能照抄"）、不是 C（无 Dn）、不是 D（spec §6 行12 只登记了"options 对象→位置参数"，未登记 tools 折叠；task-4/5 报告亦未披露）。
  - 代价：S9/coding-agent 移植时嵌套工具调用无法复用该 API 表达 callable 子集；S8 若不补，将被迫回改 S7 形状（违反 spec §10"无需回改"完成定义）。
  - 建议：`RunToolCallAsync` 增加可选参数 `IReadOnlyList<AgentTool>? tools = null`（null→`context.Tools`），或将折叠登记为 D（代价=上述）并由用户批准。

### 未登记行为偏离（Important）

- **U-2 `OperationCanceledException` 沿工具管线 rethrow，取消时整次运行以流失败收场（pi 是错误结果+正常退出）**
  - pi：`agent-loop.ts:855-866`（execute）与行780-786（prepare）catch **所有**错误（含 abort 错误）→ isError 结果；批完成后循环继续、下一次请求立即 aborted → 硬退出，**turn_end/agent_end 正常发出、已完成调用结果不丢**。
  - 我们：`ToolExecution.cs:316-320`（PrepareToolCall）与行391-397（ExecutePrepared）对 OCE 单独 `throw;` → 逐层上抛 → `AgentLoop.cs:93-105` PumpAsync `stream.Fail(OCE)`——**无 turn_end/agent_end、批内已完成结果丢失、消费者 await foreach 直接抛**。遵守 CT 的工具（C# 惯用法即抛 OCE）触发的是这条路径而非 pi 的错误结果路径。
  - 性质：实现者在 task-4 报告（.superpowers/sdd/task-4-report.md:89）明确披露并称"记录于偏离表"，但 **spec §6 无此行**（task 报告不是 docs/03 §1.3 要求的登记位）；亦不在控制器转述的四项报告披露之列。属 D 类（C# 化裁定），**必须补 spec §6 行（pi/pi.net/我们/类别/原因/代价）**，或改回 pi 语义（OCE 同样落 FromError）。
  - 注：`Parallel` 阶段三 `await finished` 解包原异常使 OCE 传播（ToolExecution.cs:221-222）是同一裁定的组成部分，一并登记。

### 未登记偏离（Minor）

- **U-3 `PollAsync` 吞掉所有异常（含 OCE）返回空表**——`AgentLoop.Run.cs:161-173`。pi：`getSteeringMessages?.()` 抛错会 reject runAgentLoop（pi 的 `.then(end)` 模式下流悬挂——pi 自身此路径即坏）；pi.net 同款吞（AgentLoopRunner.cs:165-176）。C# 采 pi.net 硬化。task-5 报告（task-5-report.md:59）已披露，spec §6 无行。建议登记 D（代价=钩子异常被静默降级为空轮询）。
- **U-4 非有限数值 partial 降级**——`ToolCallArguments.cs:41-50` catch `ArgumentException` 返回空 JsonObject。pi 快照保留 Infinity（streaming-json.ts 无此约束——JS Number 直持）；C# JsonNode 块无法承载。异常类型已亲测核实：`JsonSerializer.SerializeToNode` 对 `double.PositiveInfinity` 抛 `System.ArgumentException`（临时 scratch 程序验证，v8.0.5），catch 有效；测试 `Append_NonFinite_Number_Does_Not_Throw`（ToolCallArgumentsTests.cs:74-81）钉住。控制器转述的报告披露之一，spec §6 无行。建议登记 D。
- **U-5 `DefaultStreamFn.Get()` 文案方法名片段**——`DefaultStreamFn.cs:22` "…call DefaultStreamFn.Set()." vs pi stream-fn.ts:17 "…call setDefaultStreamFn()."；spec §3.5 声明"文案同 pi"过强，测试（AgentLoopTests.cs:569-577）钉的是 C# 文案。方法名随 C# 命名变化属 A/B，但声明与实现不一致，建议在 §6 行15 备注或改文案。
- **U-6 `ToolExecution` 嵌套类型 public**——`ExecutedToolCallBatch`（ToolExecution.cs:20）、`ParallelSlot`（行151）、`PreparedCall`（行247-254）为 public 嵌套 record；spec §2/plan Task 4 均声明"批次 internal"。无公共成员返回它们，无实际泄漏，但与自身声明不符。建议改 internal 或在 spec 备注实现细节类型面。
- **U-7 before-hook `Reason=""` 空串边界**——`ToolExecution.cs:301` `decision.Reason ?? "Tool execution was blocked"`：C# `??` 仅吃 null，空串透传；pi 行756 `beforeResult.reason || "Tool execution was blocked"` 空串也回退默认文案。极端入参下错误文案不同。建议 `string.IsNullOrEmpty` 或登记接受。
- **U-8 prepareArguments 返回非对象时错误语义**——`ToolExecution.cs:285` `(JsonObject)preparedArgs` 强转，非 JsonObject（如 JsonArray）→ InvalidCastException 消息进错误结果；pi 行708-716 把任意对象交给 schema 校验，错误为校验语义。建议先 `is JsonObject` 判型走校验报错，或登记。
- **U-9 `QueueMode` 形状无跟踪**——pi `types.ts:47-55` 公共类型；spec §1.2 把队列归 S8，但台账 D1–D22 均未含此形状，S8 plan 不显式承接即成 shape gap。建议在台账补行或在 S8 spec 显式列明。
- **U-10 D16 完成提交记账**——台账 D16"完成提交"填 `f9470fd`（非有限数值修复），plan Task 6 Step 1 指定填 Task 2 提交 `90422e6`（三适配器接线）。两提交均在范围内、D16 链完整，仅记账与计划字面不符。

### 报告披露四项的核验结论

1. **事件迭代不挂 CT**（task-5-report.md:36 表行7）——与 pi 等价（pi for-await 同无 signal），前提"provider 必经 options.CancellationToken 产终态"已实证（AnthropicMessages.cs:199-200/309；FauxProvider.cs:153-155/275）。**非偏离**；task-5 报告自己建议的"在偏离表显式登记该契约面"未做，随 U-3/U-4 一并补即可。
2. **fallback 收尾取流终值/条件 message_start**——逐句=pi 行470-478。**非偏离**（e756911 已归正 pi.net 两处走样）。
3. **非有限数值 partial 降级**——真实 D 偏离，见 U-4。
4. **parallel abort 丢调用无消息**——=pi 行623-653 同款。**非偏离**。

### S7/S8 钩子边界自洽性

`BeforeToolCall/AfterToolCall` 类型与调用点（ToolExecution.cs:295-307/437-456）随 S7 落地，Agent 门面/队列/订阅器无任何实现（grep 实证 Core 无 Agent.cs/队列类型）——与 spec §6 行11"D/scope"声明一致。自洽。**唯一缝隙是 U-1**：S8 门面把 config（含 hooks）传给 `RunToolCallAsync` 可行，但"tools 独立于 context"的表达缺失会让 S8/coding-agent 层受阻。

---

## 4. C 类 Dn 检查

| Dn | 检查项 | 证据 | 判定 |
|---|---|---|---|
| **D16** | 台账状态=`已完成`；完成提交在范围内；stub 清零；验收锚点存在 | 台账行（docs/待办-后置能力.md）状态列=已完成、提交 `f9470fd`（实核在 e7ef6c0..cf1ab0d 内）；`check-deferred` D16 无 stub 标记；测试锚点实存：`ToolCallArgumentsTests.ToolCallArguments_Exposes_Growing_Partial_Json`（:53）/`ToolCallArguments_Finish_Repairs_Recoverable_Json`（:84）/`Append_NonFinite_Number_Does_Not_Throw`（:74）、`StreamingJsonTests`（387 行）、`JsonParseTests`、三适配器 partial 用例（AnthropicMessagesTests/OpenAiCompletionsTests/OpenAiResponsesTests 各 +1，实核 diff） | **齐**（记账字面差异见 U-10） |
| **D22** | 三件套：台账行 + 代码锚点 + gate 计数 | 台账 D22 行（形状锚=pi proxy.ts、目标=后续扩展、状态=未开工）；代码锚 `DefaultStreamFn.cs:3` `TODO(后续扩展, D22)`；gate 输出 `D22=1`、registry 22 | **齐**（验收测试锚"待补"与未开工行惯例一致） |
| D1–D15/D17–D21 | 状态未被误改 | `git diff e7ef6c0..cf1ab0d -- docs/待办-后置能力.md` 实核：仅 D16 行变更 + D22 行新增 | **通过** |
| D3/D11/D17（S8/S9 目标） | 未到期、无 stub 残留要求 | gate OK；`ToolLoadout.cs` 头注释明确"S9/D11 的公开重放辅助不在本阶段"（行13），与 spec §4.3 一致 | **通过** |

---

## 5. 必需动作

- [ ] **U-1（Shape gap）**：给 `RunToolCallAsync` 补可选 `tools` 参数（或经用户明确批准登记为 D 并写入 spec §6 行12 扩注）——二选一，不得静默保留现状。
- [ ] **U-2**：spec §6 补行：`OperationCanceledException 工具管线传播 | TS catch-all→错误结果+正常硬退出 | OCE rethrow→stream.Fail，无 turn_end/agent_end | D | C# 取消惯用法；代价=取消时已完成批结果丢失、事件序中断`。或改回 pi 语义（OCE 落 FromError，保持批完整+正常硬退出）。
- [ ] U-3/U-4：spec §6 各补一行（PollAsync 吞异常 D；非有限数值降级 D）。
- [ ] U-5/U-6/U-7/U-8：spec §6 备注或在 Minor 批次择一修复（均为局部小改）。
- [ ] U-9：台账补 `QueueMode` 行（目标 S8）或在 S8 plan 显式承接。
- [ ] U-10：台账 D16 完成提交按 plan 口径改 `90422e6`（或备注 f9470fd 为终态修正提交）。
- [ ] 处置后按 SOP §5.5 派**新的**独立审计者复核（不复用本报告结论）。

---

## 6. 审计者附注

1. **门禁亲测**：本审计复跑 Release 0/0、Ai 1096/1096、Core 57/57、check-deferred OK（22 条），与控制器转述一致。
2. **非有限数值异常类型**：为验证 U-4 的 catch 有效性，在系统临时目录（repo 外）建 scratch 控制台程序实测 `JsonSerializer.SerializeToNode({x: Infinity})` → `System.ArgumentException`（System.Text.Json 8.0.5），验证后已删除 scratch。该测试路径真实有效，非虚报。
3. **ParseStreamingJson 弱化观察**（§1.2 #26）：自制括号补全器与 pi 的 partial-json 库不等价，但在 v1.5.0 主链中 `parseStreamingJson` 已非流式参数路径（pi providers 走 ToolCallArgumentsStream），且我们主链同样不消费它。当前无行为影响；若 S9+ 复用此函数于主链，需按 pi 补齐或显式登记。
4. **spec §6 逐行核对结论**：20 行中 16 行与代码一致（含行1-8/10-11/13-20）；行9（工具装载 diff D/C）一致；行12（runToolCall 位置参数 B）**不完整**——漏 tools 折叠（U-1）；行15（DefaultStreamFn B）文案声明过强（U-5）。其余行无虚报。
5. **测试真实性**：Core 57 测试全为 Faux/回调驱动无网络；并行完成序用 TCS 编排（ToolExecutionBatchTests.cs:85-107）、截断文案全字断言（AgentLoopTests.cs:407-435）、显式 continuation 单回合计数（SchedulingTests.cs:64-94）——覆盖声称与实核一致，未发现空转测试。
6. **无法验证项**：S8"无需回改 S7 形状"的完成定义只能就被 U-1 削弱的部分给出风险判断，无法在 S8 缺席下实证；真机 Unity 宿主行为归 S13。
7. **版本确认**：pi/pi.net 版本以实核 git log 为准，无未确认项。
