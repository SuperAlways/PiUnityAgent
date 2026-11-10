# M1（S1–S8）累计偏离审计（2026-11-10）

> **审计者**：独立审计 subagent（GLM / glm-5.3-flash，Claude Code agent 会话；非实现者、非 S1–S8 各阶段既有 13 份报告的审计者；只读代码/文档/参考源，未改任何代码/tests/index/HEAD/分支，唯一写入为本报告）
> **参考版本（本轮实核，非凭记忆）**：
> - pi 原版 `v1.5.0` / `f9a263655`（`D:\shuoshu\taiwuask\pi-study\pi`，`packages/ai`、`packages/agent`；`packages/schema` 按需）——行为/形状/wire 唯一真相源
> - pi.net `main` / `2d6757b`（`D:\shuoshu\taiwuask\pi-study\pi.net`）——仅 C# 写法参考（本轮用于 cancel 路径旁证与 AgentState 对照，其偏离 pi 处**不采信**，见 §3 U-1）
> - UnityLlmTransport：vendored `third_party/UnityLlmTransport`，`VERSION 0.6.0` / 登记锁 `4c0cc4b`（本轮仅核 Hosts.Net8 引用边界，未涉其代码）
> - 平台：Unity 2022.3 + netstandard2.1——仅 A 类硬约束引用
> **范围**：M1 = S1–S8（pi-ai + pi-agent-core 全量公共形状/行为）。审计对象 = 当前 main HEAD `6e9b46a` 的代码现状（非增量 diff）。代码面 = `src/PiUnityAgent.Ai/**`（98 文件）+ `src/PiUnityAgent.Core/**`（21 文件）+ `src/PiUnityAgent.Hosts.Net8`（边界）+ 测试锚点存在性。HEAD `6e9b46a` 与 S8 复核点 `33d837f` 之间经 `git diff --stat` 亲核**仅 2 个 docs 提交**（5 个 .md 文件），故 S8 复核亲跑的门禁数字对 HEAD 代码有效（见 §6）。
> **方法**：跨阶段接缝（S6→S7→S8 链、S3 StreamOptions 透传、S1 Usage↔S4/S5、D16→S6→S7、S7 事件↔S8 归约、S8 AgentState）由审计者亲读双侧源码逐行核对；S6/S7/S8 Core 行为面、Ai 层 S6–S8 增量（D16/D17/D3）、Dn 台账与 spec 表现势由 3 个只读采集 subagent 逐行对比后，**全部 Critical/Important 级声明由审计者亲自到 pi 原版与代码行复核**（其中 1 条 Important 级采集结论被驳回，见 §6）。已登合并的 5 份阶段审计 + 5 份复核 + S1–S5 累计审计作为输入线索，不作为真相源。
>
> **结论**：**FAIL**
>
> | 维度 | 计数 |
> |---|---|
> | Shape gap（未登记的公共形状缺失） | **1**（U-S1，含 4 个成员） |
> | Critical | **0** |
> | Important | **3**（含上述 Shape gap 1 条；另 U-1 行为偏离未登记、U-2 PORTING.md 基表失实） |
> | Minor | **9**（N-1..N-9） |
>
> M1 八个阶段的公共形状骨架（消息/事件/Provider/Auth/Models/4 协议 wire/Fetch 端口/AgentTool/循环/门面/队列）与行为主干（事件顺序、终态、队列排空、abort/idle、工具批终止、argumentsError 链、CreateLoopConfig 21 字段透传、ProcessEvents 归约）逐项与 pi 对齐良好；发现的问题集中在**三类**：① pi-ai 公共导出面有 4 个 util 从未被任何阶段/Dn 认领（跨阶段"无人认领"缝隙，S1–S5 累计审计已列但未处置）；② S8 D3 的 cancel 路径漏掉 S3 applyAuth 管线的两步（单阶段审计各自的盲区，接缝处才可见）；③ 文档现势性（PORTING.md 基表 / 05 路线头部 / 4 处 spec 行号）。FAIL 依据 SOP §8 门禁：存在未处置 Shape gap。**修复动作全部廉价**（§5），无任何一项需要改动协议 wire 或形状设计。

---

## 1. 公共形状矩阵

判定口径按 SOP §4：A=语言/生态强制；B=借 pi.net 的 C# 表达且不改 pi 语义；C=行为后置但形状保留（须 Dn）；D=主动加固须明示；**Shape gap=删/简化公共形状或改语义且不在 A/B/C/D**。

### 1.1 Ai（S1–S5 主体 + S6–S8 增量）

| 区域 | pi 原版 | 我们 | 判定 | 本轮验证方式 |
|---|---|---|---|---|
| Message 四型 + `timestamp` 必填 | types.ts:561-659 | `Types/Message.cs:4-10` + 四派生；Role wire `system/user/assistant/toolResult` 逐字 | 等价（B） | 亲读（沿 S1-S5 审计结论复验） |
| Usage 全字段 + cost | types.ts:459-487 | `Types/Usage.cs:4-40` 逐字段 | 等价 | 亲读 + 采集 E1 |
| **totalTokens 算术** | completions `:1546 input+output+cacheRead+cacheWrite`；anthropic `:725/:878` 同式 | `OpenAiCompletions.cs:2703`、`AnthropicMessages.cs:528/:803-805` 同式 | 等价 | **审计者亲核**（前审计未明确列过该算式） |
| StopReason 七值 / ThinkingLevel 七值+Off / transport 四值 | types.ts:489、model-schema.ts:16-19、types.ts:147 | `StopReason.cs:26-48`、`ThinkingLevel.cs:8/:11`、`Fetch.cs:89-95` wire 逐字双向 | 等价 | 亲读 |
| AssistantMessage.thinkingLevel | types.ts:596 `ModelThinkingLevel`（含 off） | `AssistantMessage.cs:17 ModelThinkingLevel?` | 等价 | 亲读 |
| AssistantMessageEvent 12 变体 + EventStream/AssistantMessageEventStream/LazyStream | types.ts:823-839 + utils/event-stream.ts + api/lazy.ts | `Events/*` 六文件 | 等价 + 登记 D | **Events/ 目录在 0c6bbd3..HEAD 零改动**（采集 D2，git diff 实证） |
| Tool/ToolCall/ConstrainedSampling/DeferredHandle/NestedToolCalls/Diagnostic | types.ts:444-456/539-549/746-780 | `Types/Tool.cs`、`ToolCall.cs:7-30` 六成员、`DeferredHandle.cs:6-23` 七字段 | 等价 | 采集 E3 + S8 审计 |
| ToolCallArgumentsStream / StreamingJsonParser / repairJson（D16） | streaming-json.ts:452-500/109-445、json-parse.ts:33-120 | `Utils/StreamingJson.cs:47-737`、`JsonParse.cs:33-132`：常量/信用/快照身份/容错读法/修复规则/文案模板逐分支等价 | 等价（B） | 采集 A-1/A-2 逐行；审计者抽核 |
| D16 三适配器接线（7 个 pi 调用点） | anthropic :795/:829、completions :655/:455、responses-shared :559-564/:730/:735-737/:775-776 | `AnthropicMessages.cs:672/:712-714`、`OpenAiCompletions.cs:596/:448-450`、`OpenAiResponses.cs:1380-1388/:1605/:1621-1624/:1691-1702` | **全接通，无漏接**（含 `arguments.done` 空串不推 delta、`output_item.done` replace 不回填） | 采集 A-3 逐点 |
| D17 Retry（61 正则/退避/回调/取消） | utils/retry.ts:7-275 | `Utils/Retry.cs`：非重试 11 + 可重试 50 条**逐条逐序逐字**；`retryDelayMs` 指数+封顶；三回调同名同参；睡眠取消→aborted+errorMessage 剥离 | 等价 + 登记 D（负 delay 钳 0/CultureInvariant） | 采集 B 逐条 |
| D3 Deferred 面 | types.ts:274-283/539-549、models.ts:950-988、faux.ts deferred 段 | `StreamOptionTypes.cs:32-62`、`Models.Capabilities.cs:11-78`、`ProviderCapabilities.cs:26-48`、`ProviderFactory/StaticProvider/FauxProvider` deferred 段：能力检查→auth 顺序、handle 七字段、窗口三值构造校验、cancel best-effort 不读令牌 | 等价 + 登记 D | 采集 C 逐点；**cancel 管线例外见 §3 U-1** |
| Provider/Auth/Models 门面 | models.ts:158-382/856-916、auth/* | `IProvider.cs`、`ProviderCapabilities.cs`、`Auth/*`、`Models*.cs`：24 成员零缺失；applyAuth 三层头合并+OpenCode 会话头+identity 合并+transformHeaders 剥离（stream/streamSimple/streamDeferred 三路齐全） | 等价 | 采集 D-1/D-2 抽验成立 |
| FileCredentialStore 平铺 | auth/types.ts:24-34 顶层平铺 | 读并旧 `extra` 兼容+保留键过滤（`FileCredentialStore.cs:261-285`）、写平铺（:368-376） | 等价 | 采集 D-4 |
| **pi-ai index.ts 公共导出残面** | index.ts:33/39/42/45 `session-resources` / `utils/overflow` / `utils/string-enum` / `utils/uuid`（uuidv7） | **全仓零对应物、零 Dn、零 spec 行、PORTING.md 未列** | **Shape gap（U-S1，见 §3）** | **审计者亲核**（pi 侧消费点、我方 grep、台账/spec/PORTING 三处全查） |
| 其余 index 导出 | diagnostics/event-stream/identity/json-parse/streaming-json/retry/text/transcript/uuid/overflow/string-enum/validation/version/session-resources | 除上条 4 项外全部有对应物（text→internal AgentTranscript（C/D11，S9 公开）；PI_AI_VERSION→程序集版本（S4 §10:464 登记）；estimate→非 index 导出、逻辑以私有副本存在于适配器） | 等价 + 登记 | 亲核 index.ts 全表 |

### 1.2 Core（S6–S8）

| 区域 | pi 原版 | 我们 | 判定 | 本轮验证方式 |
|---|---|---|---|---|
| AgentTool 九字段 + Definition 投影 | agent types.ts:478-511 | `Tools/AgentTool.cs:10-47`（label/prepareArguments/outputSchema/execute/replay/executionMode + Tool 四字段；ConstrainedSampling 三态为登记偏离） | 等价 | 亲读 |
| AgentToolResult 六字段 / AgentToolUpdateCallback / Outcome | types.ts:424-455 | `AgentToolResult.cs`、`AgentToolUpdateCallback.cs`、`AgentToolCallOutcome.cs` | 等价 | 亲读 |
| validateToolCall/validateToolArguments | ai/utils/validation.ts:28-69 | `Utils/ToolValidation.cs:20-57`：首匹配/not found 文案/argumentsError 真值短路/WeakMap↔CWT/路径格式化/错误骨架逐字 | 等价（B） | S6 审计 + 采集 E3 |
| AgentContext/AgentLoopConfig/AgentEvent 10 变体/AgentTurnDecision/hooks 四 record | types.ts:66-132/135-189/193-342/514-551 | `AgentContext.cs`、`AgentLoopConfig.cs:6-22`（pi 接口 12 成员全在 + StreamOptions 组合 + Identity）、`AgentEvent.cs:8-23`、`AgentLoopTypes.cs`、`ToolCallHooks.cs` | 等价（登记 B/A 行在 S7 spec §6） | 亲读 |
| runToolCall 独立 `tools` 参数（S7 U-1 修复后） | agent-loop.ts:801-810/823 | `ToolExecution.cs:55-79` tools 独立必填位置参；批路径 `context.Tools ?? 空`（:145-146/:179-180）= pi 默认参 | 等价 | 亲读 |
| 循环主干（外层 followUp × 内层 steering）、事件顺序、终态、length 截断、finishTurn/explicitContinuation/context-only turn | agent-loop.ts:172-330 | `AgentLoop.Run.cs:12-158` 逐段同 | 等价 | 采集（区域 3/4 表） |
| 工具批（sequential/parallel、terminate 全 true、截断文案、源序消息、abort 跳过无消息） | agent-loop.ts:518-702 | `ToolExecution.cs:25-253/479-480` | 等价（end 收割序=完成序） | 采集 |
| **argumentsError→S6 校验→S7 错误结果链** | agent-loop.ts:727-787（catch-all）+ validation.ts:55-57 | `ToolExecution.cs:303` 调 `ValidateToolArguments`（`ToolValidation.cs:30` 短路）→ catch-all → isError 结果；上游 #10897 语义保住 | 等价 | **审计者亲核全链** |
| ToolLoadout/declareToolChanges/getToolStateChanges/declarationsEqual | agent-loop.ts:342-384 + ai/utils/transcript.ts:58-167 | `ToolLoadout.cs:17-160`：四分支/Map 后写胜/定义变化=移除+新增/Canonical 四字段键序固定 | 等价 | 亲读（含 pi declarationsEqual 注释语义对照） |
| StreamOptions 透传接缝 | `{...config, apiKey, identity, signal}`（agent-loop.ts:412-417）；SimpleStreamOptions 4 字段（types.ts:377-385） | `AgentLoop.Stream.cs:33-48` 覆盖 ApiKey/Identity/CancellationToken 三项 = pi 三项；`SimpleStreamOptions.cs:3-9` 四字段齐；`EffectiveThinkingLevel` 缺省 Off=pi `??"off"` | 等价 | **审计者亲核** |
| **门面→循环 config 组装（21 项）** | agent.ts:476-515 createLoopConfig | `Agent.Run.cs:73-118` **21 项逐字段零缺零多**（reasoning off→null、skipInitialSteeringPoll 闭包、prepareNextTurn 包装两层归一） | 等价 | **审计者亲核逐字段** |
| AgentState 九字段 + 初始折叠 + DEFAULT_MODEL | types.ts:382-421、agent.ts:58-112 | `AgentState.cs:10-210`：getter 快照/setter 复制（**已登记** S8 spec §6:664）、SystemPrompt 回读、pendingToolCalls 快照、DefaultModel 全字段同 pi | 等价（B/D 登记） | 亲读 + 采集 |
| Agent 门面 22 选项/19 可变字段/方法面/单飞四文案/失败四事件/waitForIdle/reset/subscribe | agent.ts:115-147/200-236/275-439/518-566 | `AgentOptions.cs:23-58`、`Agent.cs`、`Agent.Run.cs`、`Agent.Queues.cs`：全齐；四条文案/失败序列（message_start→end→turn_end→agent_end）逐字 | 等价 + 登记 D（Prepend/吞 ODE/快照迭代等 S8 §6 在案） | 采集（区域 2 表）+ 亲读关键段 |
| ProcessEvents 归约 ↔ 10 变体 | agent.ts:575-613 | `Agent.cs:245-295`：七类处理 + 三类忽略，逐分支同（errorMessage 空串不落已修 U-8） | 等价 | **审计者亲核** |
| QueueMode/PendingMessageQueue | types.ts:49-55、agent.ts:149-180 | `QueueMode.cs:9-94`（internal + Prepend 超集，S8 §6 登记） | 等价 | 采集 |
| proxy.ts | 395 行 | 无（**D22 合规登记**：台账行 + `DefaultStreamFn.cs:3` 锚 + gate 计数） | C 合规 | 亲核 |
| **StreamFn 委托宽度** | pi **agent** 层 `StreamFn = (model, ctx, options?: SimpleStreamOptions)`（agent types.ts:33-37）；pi-ai 层 `StreamFunction<TOptions extends StreamOptions>`（ai types.ts:398-402） | `Ai/Types/StreamFn.cs:6-9` 收基类 `StreamOptions?`；Core 无窄别名，循环传 SimpleStreamOptions 实例进基类型委托 | 微小形状差（N-1，Minor） | **审计者亲核** |

---

## 2. 行为 / wire / 生态核对结论

- **事件顺序与终态**：S7 循环 start/text/thinking/toolcall/done/error→message_start/update/end→turn_end→agent_end、终值取流终值、fallback 收尾补 start 不变量、error/aborted 硬退出（finishTurn→turn_end→agent_end）——与 pi agent-loop.ts 逐段一致（采集区域 3/4 逐点 + 审计者抽核 Stream.cs）。
- **队列排空**：steering 四排空点（起始/prepareNextTurn 后仅空补查/post-turn/followUp 前）与 pi :185/213-215/304/311 一致；one-at-a-time 不双投递、all 全排空（D23 验收测试在）。
- **取消**：CT 单入口（provider 收令牌产终态；循环不挂原始令牌——与 pi for-await 无 signal 等价）；工具钩子后/ready 前双探中止产 "Operation aborted"；OCE 落 catch-all 错误结果不沿管线 rethrow（S7 U-2 修复后=pi catch-all 语义，grep 实证 Core 无 OCE 特例）。
- **wire 常量**：StopReason/ThinkingLevel/transport/Role/ToolCall 字段/stop_reason 真值守卫（AnthropicMessages.cs:732 `is { Length: > 0 }` = pi :841 truthy）全部亲核一致；S8 期 Ai 侧 wire 无新增无变更。
- **生态**：Ai/Core 均 netstandard2.1（csproj 亲核）；Ai 仅 System.Text.Json 8.0.5 + System.Threading.Channels 8.0.0，Core 零 PackageReference、仅引用 Ai（单向）；`src/PiUnityAgent.Ai`+`Core` 全量 grep 零 `UnityEngine/UnityLlmTransport/GameData/HarmonyLib` 代码引用（仅 obj/ 生成物命中 System.Net.Http 全局 using，非代码）；Hosts.Net8 = net8.0 仅引用 Ai + transport.Abstractions/HttpClient，边界正确；D16/D22 的 internal 面经 IVT 仅对测试程序集。
- **门禁（引用，非复跑）**：S8 复核审计者 2026-11-10 亲跑于 `33d837f`：Release 构建 0 警告 0 错误、Ai 1133/1133、Core 96/96、`check-deferred --current-stage S8 --gate exit` OK；HEAD `6e9b46a` 与其仅 docs 差（本审计 `git diff --stat` 亲核），数字对 HEAD 有效。`check-deferred` 静态门禁本轮由采集 subagent 复跑：registry 23、exit 0（§4）。

---

## 3. 未登记偏离 / Shape gap

### Shape gap（Important）

**U-S1 · pi-ai index.ts 四个公共导出无对应物且零登记（overflow / string-enum / uuidv7 / session-resources）**
- pi：`packages/ai/src/index.ts:39` `export * from "./utils/overflow.ts"`（`isContextOverflow`/`isRecoverableLength`/`getOverflowPatterns`，21 家 provider 溢出判据 + length 恢复判定，oversight 消费方=2A 压缩）；`:42` `export * from "./utils/string-enum.ts"`（`StringEnum`，工具 schema 助手）；`:45` `export { uuidv7 }`（时间序 UUID，openai-codex-responses.ts:41 消费）；`:33` `export * from "./session-resources.ts"`（`registerSessionResourceCleanup`/`cleanupSessionResources`，openai-codex-responses.ts:9 消费）。
- 我们：全仓 `src/` grep `StringEnum|ContextOverflow|UuidV7|SessionResourceCleanup` **零命中**（本审计亲核）；台账 D1–D23 无行；11 份 spec §6/§10 无行（grep 亲核）；PORTING.md 未列。
- 为什么不是 A/B/C/D：C# 完全可以实现（overflow=正则数据表、StringEnum≈10 行、uuidv7≈40 行、session-resources≈25 行），无生态强制；也不是"行为后置但形状保留"——**形状本身缺席**；更无登记。docs/03 §1.3"形状不可裁剪：公共类型、接口、字段、可选成员必须存在"+ §8.1"pi-ai 全部公共类型/API/能力面…保留"直接覆盖。
- 为何累计两轮审计都没拦住：S1–S5 累计审计 §1.1 末行把它们列入"无独立公共等价物"缺口，但 §3 只给 retry/text/json-parse/streaming-json 四项做了处置（D16/D17/D11），**overflow/string-enum/uuid/session-resources 四项没有落到任何必需动作**；此后 S6–S8 各阶段 spec 均只覆盖自己模块，index 导出面从此无人认领。这正是单阶段审计结构性漏掉的"跨阶段累计"缺口。
- 影响：M1 对外承诺"pi-ai 全量公共面"存在一角缺口；2A 压缩（isContextOverflow/isRecoverableLength 是 compaction 的核心判据）与工具作者（StringEnum）会撞到；uuidv7/session-resources 当前仅 codex provider（暂缓）消费，风险最低。
- 建议（三选一，用户裁决）：(a) 补实现（总量约百行、无外部依赖）；(b) 按治理 SOP 补 4 条 Dn 行（形状锚点=目标文件路径、验收测试待补、目标阶段=2A/后续扩展）并在 PORTING.md 登记；(c) 用户明确批准裁剪为 D 并写入决策日志。**不得静默保留现状。**

### 未登记偏离（Important）

**U-1 · `Models.CancelDeferredAsync` 缺 `ApplyIdentity` 与 `ApplyTransformHeaders`（S3 applyAuth 管线 ↔ S8 D3 接缝）**
- pi：`cancelDeferred` 走 `this.applyAuth(model, options)`（models.ts:986），而 applyAuth 对**所有**请求路径统一做：`identity = mergeClientIdentity(this.identity, options?.identity)`（models.ts:888/895，**门面级 identity 必然合并**）+ `transformHeaders` 执行并从 provider 选项中剥离（:886/889-890）。
- 我们：`CancelDeferredAsync`（`Models.Capabilities.cs:52-78`）只调 `ApplyAuthOptions`（:75，ApiKey/Headers/Env 三项）——**`:75` 之后直接 `deferred.CancelDeferredAsync(requestModel, handle, applied)`，缺 `ApplyIdentity`（Models.Streaming.cs:125 门面级合并）与 `ApplyTransformHeaders`（:138 执行+剥 `TransformHeaders=null`）**。同文件 `StreamDeferred` 两步齐全（`Models.Capabilities.cs:37-40`），FetchDeferredAsync 复用 StreamDeferred 无此问题；唯 cancel 一条路径漏。附带后果：`applied.TransformHeaders` 未剥除即透传给 provider（pi 明确剥离）。
- 形状面无损：`DeferredCancelOptions : StreamOptions`（StreamOptionTypes.cs:60-61）经继承携带 Identity/TransformHeaders/Headers 全字段，与 pi `DeferredCancelOptions = ProviderRequestOptions<Model>`（types.ts:283）等宽——**是管线行为漏，不是字段缺**。
- 为什么不是 A/B/C/D：无语言/生态强制；pi.net 的 `Models.cs:369-378` cancel 路径同样漏 `MergeIdentity`（:291/:305/:356 三路都有）——**照抄 pi.net 对 pi 的偏离**，违反 docs/03 §1.3 B 条"pi.net 偏离 pi 时不能照抄"；无 Dn；无 spec §6 行；无 D 类收益声明。
- 为何前审计没抓到：S8 审计 §2.8 声明"cancel 侧…组合等价 pi applyAuth（models.ts:856-915）"，但该句的行证据实际只覆盖 StreamDeferred 的组合；cancel 路径当时未被逐字段对照（Faux cancel 忽略 options 头部字段，测试探不到）。
- 影响：今天无实害（唯一 deferred provider=Faux，其 cancel 不读这些字段）；2B 接入真实 deferred provider 时会**静默丢门面级 identity 头与 transformHeaders**，且 provider 会看到未剥离的 TransformHeaders 委托。
- 建议：`Models.Capabilities.cs:75` 后补两行（`applied = ApplyIdentity(applied); applied = await ApplyTransformHeaders(applied, ct)`，仿 :37-40 现成样板；需为 DeferredCancelOptions 提供两个重载或让其走 DeferredFetchOptions 同款泛型），或在 S8 spec §6 补登记 D 行。补代码后按 SOP 由新独立审计者复核。

**U-2 · PORTING.md 基表（§1/§2）与交付状态矛盾、S4/S5 无现势映射行**
- §1 行41/42：`openai-completions.ts`/`anthropic-messages.ts` 标 ⬜——S4/S5 已实现合 main 且两阶段审计 PASS；行41-47 区间**完全没有** `openai-responses.ts`/`google-generative-ai.ts` 行（S4-C/S5-C 两个协议适配器在映射表中不存在）；行47 `⛔ 非目标 provider` 把 `google*` 整族划出——与已实现的 `src/PiUnityAgent.Ai/Api/GoogleGenerativeAi.cs` 直接矛盾；行44 auth 行仍写"OAuth 行为 C 后置 D1/D7"——D1/D7 已完成（台账 :11/:17）。
- §2 行56-60：Core 基表全 ⬜ 且四条路径**不存在**（`Core/AgentLoop.cs`/`Core/Agent.cs`/`Core/StreamFn.cs`/`Core/Types/*.cs`，实际在 `Core/Agent/` 下）——与同文件 §3B/§3C 现势表（行100-114，✅ S7/S8）冲突且**无"已被取代"标注**，新会话按基表找文件会落空。
- 行38/39：`utils/estimate.ts`→`Tokens/Estimate.cs`、`utils/abort.ts`→`Reliability/Abort.cs` 两条规划路径**不存在**且无 Dn（estimate 的实际实现以私有副本在 3 个适配器内，`CharsPerToken=3.5` ×3；abort 逻辑由 CT 承载）——"在范围标 ⬜"的行从未转绿也从未注销。
- 为什么不是 A/B/C/D：纯文档现势性；docs/05 §五交付检查每阶段含"PORTING.md 状态更新"、docs/03 §3.8 门禁同——S4/S5/S6/S7/S8 五个阶段的该项均未落实（§3A/3B/3C 只补了 S6/S7/S8 增量段，基表未动）。
- 影响：与已清偿的前审 G-2（spec 表现势失效）同族；PORTING.md 是 §1.4/§3.0 约定的"我们的偏离登记位"之一，基表失实会让偏离登记失去载体。
- 建议：一次 docs 提交——§1 补 S4/S5 四适配器现势行（openai-completions/openai-responses/anthropic-messages/google-generative-ai → ✅ S4/S5）、删或改 `google*` ⛔ 行、auth 行刷新 D1/D7 状态；§2 基表整体标注"→ 已被 §3B/§3C 取代"或按实路径改 ✅；行38/39 注销（estimate 实现位置/abort 由 CT 承载）或补 Dn。

### 未登记偏离（Minor，N-1..N-9）

- **N-1 · Core 缺 pi-agent 层窄 `StreamFn` 别名**：pi agent `types.ts:33-37` `StreamFn` 收 `options?: SimpleStreamOptions`；我方 Core 复用 `Ai/Types/StreamFn.cs:6-9`（收基类 `StreamOptions?`），循环恒传 SimpleStreamOptions 实例（`AgentLoop.Stream.cs:41-48`）——运行时行为同，类型契约上 DefaultStreamFn 宿主实现需下转才能读 ToolChoice/Reasoning/Deferred/ThinkingBudgets。非登记项；建议 S7 spec §6 行15（DefaultStreamFn 行）补一句代价注，或 Core 增窄别名委托。
- **N-2 · Faux 残条族仍未登记（S1–S5 累计审计 U-4 在复核 §1.6 漏处置后持续在案）**：本轮逐条复验仍在——① 流路径不回调 `onResponse({status:200})`（pi faux.ts:534；我方 `FauxProvider.cs` EmitAsync 主路径无 OnResponse，仅 fetch :502-507/cancel :622-627 有）；② `GetModel` 未命中抛 `KeyNotFoundException`（:94-98）vs pi 返回 undefined；③ 缺 `unregister()`；④ **partial 工具参数恒 `{}` 不增长**（`EmitToolCallAsync :385` `Arguments = new JsonObject()` 到 ToolCallEnd 才换全量；pi faux.ts:429-443 用 `ToolCallArgumentsStream` 让 partial 随 delta 生长，注释明言"Partial arguments come from the same parser real providers use"）。其中 ④ 在 D16 落地后性质变了：真 provider 已产增长 partial（D16 七接线点），Faux 作为离线测试架反而产不出——**循环层 partial 快照消费路径（message_update/tool_execution_update）在单测里没有被 Faux 覆盖**。建议并入 S3 v2/v3 偏离表 Faux 行或补 Dn，④ 优先（解析器已在库，接线约 5 行）。
- **N-3 · Responses `arguments.done` 缺 `event.arguments` 的容忍未登记**：pi `responses-shared.ts:735` 直接 `.replace(event.arguments)`（缺失→TypeError→流 Error 终态）；我方 `OpenAiResponses.cs:1622` `JsonStr(...) ?? ""` 容忍。D 类加固，与已登记的 S4 §10"output_item 缺 item"同族，建议并入该行。
- **N-4 · spec 行号/现势漂移 4 处**：① S5 §10:243 引 `AnthropicMessages.cs:736`，守卫实码 :732；② S4 §10:472 引 `OpenAiCompletions.cs:870`，守卫在 :864、赋值 :869；③ S8 §6 U-2 行 pi 侧仍引 `faux.ts:322-336`（S8 复核 R-1 已认定实际 :327-336，复核只改了调用点半边）；④ S3 v2 §8:723 现势句过期（仍写"D3=S8…未开工"——D3 已于 2026-11-10 完成；且"登记 D1–D15"现台账 D1–D23）。均为引用级，一次 docs 批次。
- **N-5 · S8 spec header 行3 把 U-8 列入"补 §6 登记"集合**：实际 U-8 是代码修复（c8ce5aa）、无 §6 行；S8 复核 :43 的口径（"U-2..U-7 + N-1..N-6 共 12 项登记"）才准确。改 header 一行。
- **N-6 · docs/05-教学阶段路线.md 头部版本线与版本记录表滞后**：头部 v1.10（:3）停在 S5；§六版本记录表末条 2026-11-09 S5（:151-161）——同文件 :33-35 已现势声明 S6/S7/S8 完成合 main。刷头部与版本表各一行。
- **N-7 · docs/05 格式小项**：S7 行（:34）表格 4 列 vs 其余行 5 列（状态文案并入了"阶段"格）；§2.1 承接表（:46-55）缺 D23 行。顺手修。
- **N-8 · 台账 D2 备注引用 stale 锚**：`docs/待办-后置能力.md:12` 备注仍写"过期 OAuth 跳网络 `TODO(S5,D1)`"——该锚已随 D1 完成删除（grep 零命中）。备注句刷新。
- **N-9 · D16 观察项两条**：① argumentsError 内层文案为 .NET `JsonException.Message`（V8 `JSON.parse` 措辞族差异；模板 `JsonParse.cs:130` 逐字一致，仅内层 message 不同）——建议 S7 spec §6 D16 行代价列补一句；② `ToolCallArguments.ToObject` 每次 Append/Replace 新建 JsonObject（`ToolCallArguments.cs:39-43`）vs pi 快照不变时复用同引用（streaming-json.ts:491）——按引用判变的消费者可见，属已登记"记录 with 快照"族的延伸，登记或注释即可。

---

## 4. C 类 Dn 检查（D1–D23）

| 检查面 | 结论 | 证据（本轮采集 subagent 亲跑/亲查） |
|---|---|---|
| 台账行完整性 | **齐** | 23 行每行恰好 11 列（程序化逐行计数，`docs/待办-后置能力.md:11-33`）；`check-deferred.py` 解析 `registry entries: 23`、0 解析错误 |
| 已完成行（13：D1/D2/D3/D6/D7/D9/D10/D12/D13/D14/D16/D17/D23） | **全净** | 台账引用的 21 个完成提交 SHA 全部在 `git log` 实存；主 src 无残留 stub（全部 5 处 `NotSupportedException` 与 12 处 TODO/Deferred 锚点均属 10 个未开工行；D3/D16/D17 的旧钉已从主 src 消失，仅 `.worktrees/` 旧副本残留、不在门禁扫描范围）；验收测试锚点逐名实存（OAuthTests/S4RefreshTests/DeferredTests 三类+DeferredWindowTests/FileCredentialStoreTests/S4·S5ProviderProfileTests/RetryTests/AgentQueueTests+Integration 等）；D3 形状锚逐点实证（ProviderCapabilities.cs:26-44、Models.Capabilities.cs:11-76、StreamOptionTypes.cs:34-54 Window 构造校验等） |
| 未开工行（10：D4/D5/D8/D11/D15/D18/D19/D20/D21/D22） | **合规，无越期** | 锚点全实存（ProviderFactory.cs:146/:152、LazyStreamObserver.cs:15、TranscriptNormalization.cs:7、AnthropicProvider.cs:13/:87、OpenAiResponses.cs:53/:455、ModelCatalog.cs:52、AnthropicOAuth.cs:81-82、SchemaChecker.cs:16、DefaultStreamFn.cs:3）；目标阶段均为 S9/2B/后续扩展——M1（S1–S8）范围内**无"目标阶段已过仍未开工"项** |
| 孤儿锚点 | **零** | 代码 12 个标记与台账 10 个未开工行双向一致 |
| 门禁 | **exit 0** | `python scripts/check-deferred.py --root . --current-stage S8 --gate exit` → `code markers: D11=1, D15=2, D18=1, D19=1, D20=1, D21=1, D22=1, D4=2, D5=2, D8=1` / `registry entries: 23` / OK |
| 台账↔spec 偏离表对应 | S6 §6 23 行、S7 §6 25 行、S8 §6 约 50 行逐行与代码一致（采集逐行核） | 唯一残留=N-4/N-5 引用级瑕疵；S7 QueueMode 行为历史性声明、已由台账 D23 显式回指闭环 |

**结论**：Dn 治理机器可验面在 M1 终态**全部干净**。唯一缺陷是反向缺口——存在**台账外的后置**（U-S1 四 util；estimate/abort 两条 ⬜ 行无锚）。

---

## 5. 必需动作

**合并门禁项（FAIL → 非 FAIL 的清偿清单）**：

- [ ] **U-S1（Shape gap，用户裁决）**：overflow/string-enum/uuidv7/session-resources 四项三选一——补实现 / 补 4 条 Dn 行 + PORTING 登记 / 用户批准裁剪为 D 并入决策日志。
- [ ] **U-1**：`Models.CancelDeferredAsync` 补 `ApplyIdentity` + `ApplyTransformHeaders`（约 3 行 + 重载），或 S8 spec §6 补 D 行登记；修复后按 SOP 由**新的**独立审计者复核。
- [ ] **U-2**：PORTING.md 一次 docs 提交——§1 补 S4/S5 四适配器现势行、删/改 `google*` ⛔ 行、auth 行刷新；§2 基表标注被 §3B/3C 取代或改实路径；行38/39 estimate/abort 注销或补 Dn。

**Minor 批次（一次 docs 提交可全部清偿）**：

- [ ] N-1：S7 spec §6 DefaultStreamFn 行补 StreamFn 宽度代价注（或 Core 增窄别名）。
- [ ] N-2：Faux 残条族补登记（S3 v2/v3 偏离表或 Dn）；④ partial 不增长建议随 faux 补齐（解析器已在库）。
- [ ] N-3：S4 §10"output_item 缺 item"行并入 `arguments.done` 容忍句。
- [ ] N-4/N-5：spec 行号 4 处 + S8 spec header 一句订正。
- [ ] N-6/N-7：docs/05 头部版本线、版本记录表、S7 行格式、§2.1 补 D23。
- [ ] N-8：台账 D2 备注刷新。
- [ ] N-9：S7 spec §6 D16 行代价列补 .NET 文案注；ToObject 引用语义登记或注释。

---

## 6. 审计者附注

**误报驳回（采集 subagent 报告中被本审计者复核否决的结论）**：

1. **"AgentState.Messages/Tools getter 快照化必须登记（Important）"——驳回**：该偏离**已登记**。S8 spec §6 明有行："AgentState.messages/tools getter | 返回内部数组（可原地 push）| IReadOnlyList | 只读快照；setter 复制 | B/D | 防止外部改内部 transcript；代价=非 live 引用"（`2026-11-09-S8-AgentFacade-design.md:664`），且 §4 正文 :166-167、:602 同口径三处。采集者在错误位置找登记。
2. **PollAsync 吞异常 / 循环异常 stream.Fail / prepareArguments 强转 / AgentLoopConfig 组合 vs extends**——均非新发现：分别为 S7 spec §6:448/:441/:450/行2 已登记行，本轮仅复核其与代码仍一致。
3. **"pi PollAsync 抛错会走 handleRunFailure 四事件"与 S7 审计"pi 低层 agentLoop() 流悬挂"并存**——两者都对：门面路径（runWithLifecycle）catch 走 handleRunFailure（agent.ts:535-537），低层 agentLoop() `.then(end)` 无 catch 流悬挂；我方 PumpAsync 统一收场=登记过的 D。

**无法验证项**：

- pi 上游 `providers/data/*.json` 仍不在本地 checkout，19 份模型目录 JSON 只能与 pi.net 快照对数（沿 S1–S5 审计口径）；D9 补上游对数仍是开放项。
- Google 流中 `{"error":{}}` 在 pi TS 经 @google/genai SDK 的行为仍需实机（S5 §10.3 C/观察行维持）。
- D17 正则在 .NET 与 V8 引擎下的极端语料差异（沿 S8 审计口径：61 条逐字一致 + 10 组行为语料为证据底座）。
- vendored UnityLlmTransport `4c0cc4b` 仅能核对 VENDORED.md/VERSION 声明（拷贝排除 .git）。
- Unity 实机（Mono/IL2CPP）下 STJ 多态路由表现：静态审计不覆盖，归 S13 实机。

**版本与门禁说明**：pi `v1.5.0/f9a263655`、pi.net `2d6757b` 与 PORTING.md §0/docs/03 登记一致，本轮未发现版本漂移迹象（S8 期核过的 deferred/Faux/Retry 形状均与 spec 引用吻合）。门禁数字引用自 S8 复核审计（2026-11-10 亲跑于 `33d837f`），HEAD `6e9b46a` 经本审计 `git diff --stat` 亲核仅 2 个 docs 提交，数字对 HEAD 代码有效；`check-deferred` 为静态检查、本轮由采集 subagent 复跑 exit 0。本审计未重跑实现者单测。

**范围说明**：本审计对象为 M1（S1–S8）终态代码现状；普通代码质量/测试充分性/性能不在范围（如 CharsPerToken 常量在 3 个适配器重复，属常规 review 项，不构成对齐偏离——pi 的 estimate 逻辑本身未进 index 公共面）。测试未逐个验证断言强度，抽检沿既有各复核报告结论。

---

## 结论

**FAIL**

| 维度 | 数字 |
|---|---|
| Shape gap | **1**（U-S1：pi-ai index 四公共导出无对应物且零登记；涉及 overflow/string-enum/uuidv7/session-resources 4 个成员） |
| Critical | **0** |
| Important | **3**（U-S1、U-1 CancelDeferred 管线缺两步、U-2 PORTING.md 基表失实） |
| Minor | **9**（N-1..N-9，全部为登记/引用级或小接缝注） |

按 SOP §8 门禁（Shape gap 必须已修或经用户批准登记为 D，未登记偏离必须补入 spec），本轮为 FAIL；但三项 Important 的修复代价均为小时级（U-S1 最贵路径≈百行代码、U-1≈3 行、U-2 纯 docs），无任何协议 wire 或形状设计返工。M1 的形状/行为主干（§1/§2 全表）经三源（pi 原版/pi.net/代码）核对是**对齐良好**的——FAIL 反映的是"公共导出面认领 + 一条接缝管线 + 文档现势"的收尾缺口，不是设计性偏离。清偿 §5 后按 SOP 派**新的**独立审计者复核。
