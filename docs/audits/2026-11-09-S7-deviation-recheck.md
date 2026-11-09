# S7 偏离审查复核（2026-11-09）

> 审计者：**独立复核 subagent**（GLM，经 Claude Code Agent 会话；非原审计者、非实现者，只读复核，仅写本报告）
> 参考版本（实核 `git log -1`，非采信声明）：
> - pi 原版 `v1.5.0` / `f9a263655`（`D:/shuoshu/taiwuask/pi-study/pi`，实核 = `f9a263655 Release v1.5.0`）——行为/形状/wire 唯一真相源
> - pi.net `main` / `2d6757b`（实核 = `2d6757b chore(memory): …`）——仅 C# 表达参考
> - Unity 2022.3 + netstandard2.1——仅 A 类硬约束依据（UnityLlmTransport S7 零引用，沿用原审计 §2.3 结论未重查）
> 范围：`e7ef6c0..e740b94`（分支 `feature/s7-agent-loop`；HEAD 实核 `e740b94`；复核焦点 = 修复提交 `4db0af0`（U-1/U-2 + Minor）与 `e740b94`（U-9/U-10 文档））
> 门禁复跑（**本复核亲跑**，非转抄）：Release 构建 0 警告 0 错误；Ai 1096/1096；Core 62/62；`check-deferred --current-stage S7 --gate exit` OK（registry **23**，D22=1，D16 无 stub）
> **结论：PASS_WITH_REGISTERED_DEVIATIONS**（U-1..U-10 全部核销；无 Shape gap、无未登记偏离；剩余偏离均已在 spec §6 / 台账登记）

---

## 1. 逐项核销矩阵

> 行号以 HEAD `e740b94` 为准（特别注明"修复前"者以 `4db0af0~1` = `cf1ab0d` 为准）。每条结论均给 file:line。

| # | 原判定 | 处置 | 复核证据 | 判定 |
|---|---|---|---|---|
| **U-1** | Shape gap：`runToolCall` 独立 `tools` 被折叠进 `AgentContext.Tools` | **修复**（选审计建议的"补参数"项） | pi：`agent-loop.ts:801-810` `RunToolCallOptions.tools` 必填独立；`:823` `prepareToolCall(..., options.tools)`；`:721-724` `prepareToolCall` 默认参 `tools = currentContext.tools ?? []`（=批路径用 context.tools）。我们：`ToolExecution.cs:55-62` `RunToolCallAsync(ToolCall, IReadOnlyList<AgentTool> tools, AssistantMessage, AgentContext, AgentLoopConfig, CancellationToken, AgentToolUpdateCallback?)`——`tools` 独立必填（无默认值），`:64` 传 `PrepareToolCall`；`ToolExecution.cs:285` 工具查找改按首参 `tools`。批路径 `ToolExecution.cs:145-146`（顺序）/`:179-180`（并行）传 `context.Tools ?? Array.Empty<AgentTool>()` = pi 批路径 `currentContext.tools ?? []`；批 dispatch `hasSequentialToolCall` 读 `context.Tools`（`ToolExecution.cs:33-35`）= pi `agent-loop.ts:526-528` 读 `currentContext.tools`。测试：`ToolExecutionTests.cs:58-66` `Tools_Are_Caller_Provided_Independent_Of_Context_Tools`（context.Tools 为空、仅凭调用方 tools 解析成功——独立性直接证明）；`:49-52` 未知工具改传 `Array.Empty<AgentTool>()` | **核销** |
| **U-2** | Important：OCE 沿工具管线 rethrow → 取消时 stream.Fail 丢 turn_end/agent_end | **修复**（选审计建议的"改回 pi 语义"项） | pi：`agent-loop.ts:727-787`（prepare）与 `:855-866`（execute）均 catch-all、无 OCE 特例。我们：OCE `throw;` 特例两处已删——prepare `ToolExecution.cs:329-335` catch-all 落 `FromError(error.Message)`（注释 :331-333 记录裁定）；execute `ToolExecution.cs:404-413` catch-all，耗时先测→`AwaitUpdates`→错误结果（= pi `:855-866` 顺序）。显式 abort 探测保留：钩子返回后 `:309-310`、返回 ready 前 `:324-325`、并行 thunk 入口 `:209-213`，产出 "Operation aborted"（= pi `:748-753`/`:767-772`/`:630-637`）。残留检查：grep `src/PiUnityAgent.Core/` 全域无 `catch (OperationCanceledException)` 特例（仅注释 4 处）；loop 级 `AgentLoop.cs:103` `stream.Fail` 仅剩已登记的"Agent 流失败收场"行（spec §6 行441 区段，`docs/superpowers/specs/2026-11-09-S7-AgentLoop-design.md:440`）。测试三级齐：hook OCE `ToolExecutionTests.cs:149-158`、execute OCE（含耗时）`:178-187`、loop 级 `AgentLoopTests.cs:569-596` `Tool_Throwing_OCE_Yields_Error_Result_And_Loop_Completes`（断言 2×TurnEnd + 1×AgentEnd + 终值 "done" + CallCount=2）；既有 `Sequential_Mode_Stops_Abort`（`AgentLoopTests.cs:521-558`）钉 aborted 终态硬退出 | **核销** |
| **U-3** | Minor：PollAsync 吞异常未登记 | **登记**（spec §6 补行） | spec §6 行 `docs/superpowers/specs/2026-11-09-S7-AgentLoop-design.md:448`（pi reject/流悬挂 ↔ pi.net 同吞 ↔ 我们捕获返回空列表，类别 D，代价=钩子异常静默降级）。代码事实核对：`AgentLoop.Run.cs:161-175` `PollAsync` catch-all → `Array.Empty<Message>()`，注释"契约：钩子不得抛出"——行描述与代码一致 | **核销** |
| **U-4** | Minor：非有限数值 partial 降级未登记 | **登记**（spec §6 补行） | spec §6 行 `...design.md:449`（pi 快照直持 Infinity/NaN ↔ 我们 `SerializeToNode` 抛 `ArgumentException`→catch 降级空对象，D/低，代价=partial 期间字段缺省+终点 repair 同受限）。代码事实核对：`ToolCallArguments.cs:39-53` `ToObject` catch `ArgumentException` → 空 JsonObject（异常类型原审计已用 scratch 程序实测有效） | **核销** |
| **U-5** | Minor：`DefaultStreamFn.Get()` 文案方法名片段与"文案同 pi"声明不符 | **修复**（改文案为 pi 字面） | `DefaultStreamFn.cs:22` 现为 `"No default stream function configured. Pass streamFn explicitly or call setDefaultStreamFn()."` = pi `stream-fn.ts:17` 逐字（实核）。测试同步 `AgentLoopTests.cs:604-611`（全字断言）。spec §3.5 内嵌代码块注"文案同 pi"（`...design.md:296` 区段）修复后**为真**；spec §6 行441（DefaultStreamFn B）无需变更 | **核销** |
| **U-6** | Minor：批次嵌套类型 public 与"批次 internal"声明不符 | **修复**（ExecutedToolCallBatch 改 internal） | `ToolExecution.cs:20` `internal sealed record ExecutedToolCallBatch`。**订正**：原审计 U-6 所列三类型中，`ParallelSlot`（修复前 `:151`）与 `PreparedCall`（修复前 `:247`）在 `cf1ab0d` **本就是 private**（实核 `git show 4db0af0~1`），仅 `ExecutedToolCallBatch` 是 public——修复只需且只改了前者，现三者均非 public。pi 侧 `ExecutedToolCallBatch` 为模块内未导出类型（`agent-loop.ts:535`），internal 对应无误 | **核销**（附原审计描述过宽订正，见 §5 附注2） |
| **U-7** | Minor：before-hook `Reason=""` 空串透传（pi `\|\|` 空串也回退） | **修复**（IsNullOrEmpty） | `ToolExecution.cs:313-314` `string.IsNullOrEmpty(decision.Reason) ? "Tool execution was blocked" : decision.Reason`（注释 :313 记 =pi `\|\|`）；pi `agent-loop.ts:756` `beforeResult.reason \|\| "Tool execution was blocked"`。测试 `ToolExecutionTests.cs:97-105` `Before_Hook_Block_Empty_Reason_Falls_Back_To_Default`（空串→默认文案全字断言）。`Reason` 是 string，falsy 集合 = null/空串，`IsNullOrEmpty` 与 JS `\|\|` 等价 | **核销** |
| **U-8** | Minor：prepareArguments 返回非对象时强转报错（校验语义 vs InvalidCastException） | **登记**（spec §6 补行，不修——审计允许"备注或择一修复"） | spec §6 行 `...design.md:450`（pi 非对象交 schema 校验 ↔ pi.net 直传 ↔ 我们 `(JsonObject)` 强转→InvalidCastException 消息进错误结果，D/低，代价=极端入参错误文案不同、仍 isError 不执行）。代码事实核对：`ToolExecution.cs:297` `var preparedArguments = (JsonObject)preparedArgs;` 强转仍在，落 catch-all（:329-335）为 isError 结果——行描述与代码一致 | **核销** |
| **U-9** | Minor：QueueMode 形状无台账跟踪 | **登记**（台账补 D23 行 + spec 锚点订正） | 台账 `docs/待办-后置能力.md:33` D23 行：11 格齐全（形状锚 `AgentOptions`/`QueueMode` 待建、验收测试待补、目标 **S8**、未开工、备注"S7 低层循环只消费回调、不持有队列模式（U-9 补台账）"）。pi 锚点实核：`packages/agent/src/types.ts:55` `export type QueueMode = "all" \| "one-at-a-time";`（注释块 49 起）——原审计 `47-55` 过宽、修复订正为 `49-55` **正确**。spec §6 行451 scope 行同步锚点并注"→ 台账 D23"，spec scope 声明与台账互指自洽 | **核销** |
| **U-10** | Minor：D16 完成提交记账与 plan 字面不符 | **登记**（取审计给的第二选项：备注消解） | 台账 `docs/待办-后置能力.md:26` D16 行备注追加："完成提交以 f9470fd 为终态（90422e6 为接线提交，f9470fd 为终态修正提交）"。plan 口径实核：`docs/superpowers/plans/2026-11-09-S7-AgentLoop.md:1398` "完成提交填 Task 2 提交 SHA"（Task 2 = 90422e6 接线）——原审计必需动作即"改 90422e6 **或**备注 f9470fd 为终态修正提交"，二选一已满足，台账与 plan 字面差异以备注消解 | **核销** |

**10/10 核销**。台账 range diff 实核（`git diff e7ef6c0..e740b94 -- docs/待办-后置能力.md`）：仅 D16 行变更 + D22/D23 两行新增，其余行未误改。

---

## 2. 修复是否引入新偏离 / 破坏 pi 语义

逐面核（重点：tools 参数位置/顺序、OCE 消息文本、loop 事件序列）：

1. **tools 参数形状与顺序**：pi options 对象字段序 `{tools, assistantMessage, context, signal?, onUpdate?}` + hooks；C# 位置参数 `(toolCall, tools, assistantMessage, context, config, signal, onUpdate)`——`toolCall` 是 pi 第一位置参（`runToolCall(toolCall, options)`），其余按 options 字段同序展开，`tools` 必填无默认 = pi 必填。属已登记的"options 对象→位置参数"B 类（spec §6 行438，已补 U-1 修复注记"修复后与 pi 无此偏离"）。**无新偏离**。
2. **批路径工具集来源未被 U-1 改坏**：批路径仍传 `context.Tools ?? Array.Empty<AgentTool>()`（ToolExecution.cs:145-146/179-180）= pi `prepareToolCall` 默认参 `currentContext.tools ?? []`（agent-loop.ts:721-724）；批 dispatch 读 `context.Tools`（:33-35）= pi `agent-loop.ts:526-528`。pi `runToolCall` 与批路径本就是两种工具集来源，修复后我们同构。**一致**。
3. **OCE 消息文本**：pi catch-all 面向 `error instanceof Error ? error.message : String(error)`；我们面向 `Exception.Message`。取消场景下 JS AbortError 文本与 C# `OperationCanceledException.Message`（"The operation was canceled."）字面不同——异常消息平台差异，A 类；语义同（把抛错者的 message 给模型）。**非偏离**。
4. **loop 事件序列**：`AgentLoop.Run.cs` 修复未触碰；OCE 落结果后批正常完成 → 循环继续 → 下一请求 aborted 终态硬退出（finishTurn→turn_end→agent_end）路径不变。loop 级新测试断言 2×TurnEnd/1×AgentEnd/CallCount=2 通过（实跑绿）；abort 硬退出由既有 `Sequential_Mode_Stops_Abort`（:521-558）钉住。**一致**。
5. **spec 内部自洽**：spec 全文 grep 无 "取消"/"OperationCanceledException" 残留表述（0 命中）——修复选择"改回 pi 语义"后不存在可登记的偏离，未虚报已消失偏离，也无与代码矛盾的旧声明；spec §3.5"文案同 pi"修复后为真。**自洽**。
6. **编译面**：`RunToolCallAsync` 全部 11 个调用点（均在 `ToolExecutionTests.cs:51-206`）已随新签名更新；src 内无其他调用方（grep 实证）。Release 构建 0/0 佐证。

**结论：修复未引入新偏离。**

---

## 3. 新发现

- **N-1（Minor，文档卫生，不阻塞）**：台账头部"最后更新"戳（`docs/待办-后置能力.md:7`）在 `e740b94` 新增 D23 行后未同步——仍只写"…新增 D22 proxy 服务端代理流，归 后续扩展"，未提 D23。U-9 本体（D23 行 + spec 互指 + gate 计数）已成立，不受影响；下次触碰台账时顺手补一句即可。

无其他新发现。修复 diff 全量审阅（`git show 4db0af0` / `git show e740b94`）未发现越权改动：`4db0af0` 仅触 ToolExecution.cs / DefaultStreamFn.cs / spec / 两测试文件；`e740b94` 仅触 spec 一行 + 台账两行。

---

## 4. 必需动作

- [ ] 无阻塞项（U-1..U-10 已全部核销；门禁全绿）。
- [ ] （可选，不阻塞）N-1：台账头部"最后更新"补记 D23 新增。

---

## 5. 审计者附注

1. **门禁亲测**：本复核复跑 Release 构建 0 警告 0 错误、Ai 1096/1096、Core 62/62、`check-deferred --current-stage S7 --gate exit` OK（registry 23，D22=1，D16 无 stub）——与控制器转述一致，均非转抄。
2. **原审计一处描述过宽（订正）**：U-6 所列三类型中仅 `ExecutedToolCallBatch` 在 `cf1ab0d` 为 public；`ParallelSlot`/`PreparedCall` 修复前即 private（`git show 4db0af0~1:151/247` 实核）。修复范围正确；此订正不影响 U-6 核销，仅记录原审计精度。同理 U-9 的 pi 锚点 `47-55` 过宽，修复订正为 `49-55` 并经本复核对 pi 源码实核为正确。
3. **结论取 PASS_WITH_REGISTERED_DEVIATIONS 而非 PASS 的理由**：spec §6 仍存在登记中的 A/B/D 类偏离（PollAsync 吞异常 D、非有限数值 partial D/低、prepareArguments 强转 D/低、ThinkingLevel 三态 A/B 等）——这是 TS→C# 移植的常态登记态，全部有行有档有代价说明，无 Shape gap、无未登记偏离；按 SOP 判定口径不构成 FAIL。
4. **无法验证项**：S8"无需回改 S7 形状"的完成定义只能就 U-1 已修复部分给出正面判断（tools 独立性已由 `Tools_Are_Caller_Provided_Independent_Of_Context_Tools` 钉住）；S8 Agent 门面缺席下无法整体实证。真机 Unity 宿主行为归 S13。
5. **版本确认**：pi = `f9a263655 Release v1.5.0`、pi.net = `2d6757b`，均实核 `git log -1`，无未确认项。
