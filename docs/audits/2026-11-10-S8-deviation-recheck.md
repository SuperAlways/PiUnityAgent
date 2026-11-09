# S8 偏离审查 · 复核（2026-11-10）

> **审计者**：独立 subagent（GLM，新会话，未参与首次审计与整改）
> **参考版本**：pi `v1.5.0` / `f9a263655`（本地检出 `D:/shuoshu/taiwuask/pi-study/pi`，本轮亲核 `git describe` = v1.5.0）；pi.net `main` / `2d6757b`；UnityLlmTransport `4c0cc4b` / `0.6.0`（S8 不引用）。与 spec §0 / 首审计声明一致，无版本漂移。
> **复核范围**：首次审计 HEAD `926b6b1` → 当前 HEAD `33d837f`（代码修复 `c8ce5aa` + 文档登记 `33d837f`）；同时对首次审计声明逐条回到 pi 原版与代码双源核验，不复用首审计结论。
> **方法**：`git diff 926b6b1..HEAD` 限定整改面；U-1/U-8 对照 pi 原版逐行（faux.ts:657-666 / agent.ts:604-607）；spec §6 新增行逐条与代码 file:line 比对；门禁四件亲跑。

---

## 结论：**PASS_WITH_REGISTERED_DEVIATIONS**

| 维度 | 结果 |
|---|---|
| Shape gap | **0** |
| Critical | **0** |
| Important | **0** |
| Minor | 0 条未登记偏离；3 条非阻塞附注（R-1 引用行号偏差 / R-2 台账时间戳滞后 / R-3 spec HEAD 自引用，见 §3） |
| spec §6 与代码一致性 | 新增 13 行（含 1 行订正为两层拆分、1 行改写）逐行核验**全部与代码一致** |

SOP §8 门禁逐项：三方偏离表每行与代码一致 ✓；审计报告产出、结论非 FAIL ✓；无 Shape gap/Critical 需用户批准项 ✓；未登记偏离已补进 spec §6 ✓；C 类 Dn 台账完整（D3/D17/D23 已完成+锚点+提交）✓；修复后复核由新的独立审计者完成（本文）✓。

---

## 1. 首审计必需动作逐条复核

| 首审计发现 | 整改观察 | 代码/spec 证据 | 残留 |
|---|---|---|---|
| **U-1** Faux cancel 预取消令牌抛 OCE，pi best-effort 永不抛（faux.ts:657-666 只克隆/记录/标记+onResponse，不读 signal） | **已代码修复**。`CancelDeferredAsync` 末尾 `ThrowIfCancellationRequested()` 已删，改为注释说明"参数仅为 delegate 签名保留"；全方法无 token 读取/抛出。测试 `Cancel_With_PreCancelled_Token_Still_Completes`：预取消 CTS → 直调 `provider.CancelDeferredAsync` 不抛 + `CancelledDeferred` 单条、Id 相等。另在 spec §4.7.1/§4.7.4 补"best-effort 不读取消令牌、不抛出" | `src/PiUnityAgent.Ai/Providers/Faux/FauxProvider.cs:603-631`；`tests/PiUnityAgent.Ai.Tests/Providers/DeferredTests.cs:325-352`；`docs/superpowers/specs/2026-11-09-S8-AgentFacade-design.md` §4.7.1/§4.7.4 | 无 |
| **U-8** `turn_end` 空串 ErrorMessage 会落状态，pi 真值判断不落（agent.ts:605 `&& event.message.errorMessage`） | **已代码修复**。TurnEnd 分支改 `!string.IsNullOrEmpty(turn.Message.ErrorMessage)`。测试 `Empty_Error_Message_Is_Not_Recorded`：StopReason.Error + errorMessage="" → 消息仍入 transcript、`State.ErrorMessage` 保持 null | `src/PiUnityAgent.Core/Agent/Agent.cs:267-272`；`tests/PiUnityAgent.Core.Tests/Agent/AgentLifecycleTests.cs:165-186` | 无 |
| **U-2** Faux 错误终态标识/时间戳口径（pi：provider 级 api/provider + `requestModel.id` + `Date.now()`，faux.ts:327-336 定义、调用点 536-541/579/636/648） | **已登记** spec §6 新行"D3 Faux 错误终态标识/时间戳"，类别 D/B，代价=非默认 Api/Provider 的 Faux 实例可见差异 | 代码事实核验一致：`FauxProvider.cs:197-208`（请求侧 model.Api/Provider/Id + `Timestamp=0`）、:565（deferred fetch catch 走 EmitError）；pi 侧 faux.ts:327-336 + :636/:648 | R-1（引用行号 678-681 不精确，非实质） |
| **U-3** 工厂/静态 provider 层不支持文案与 pi 不一致；§6"逐字"声明在 provider 层不成立 | **已按建议拆两层登记订正**：原 1 行改为"D3 不支持文案（Models 层）"（逐字 ✓，pi models.ts:961/984 = 我们 Models.Capabilities.cs:23,29,62,68）+ "D3 不支持文案（provider 层）"（pi 工厂 fetch=`...for "<api>"` / cancel=`cannot cancel ... for "<api>"`，models.ts:1167-1185；我们两处统一 `Provider <id> does not support deferred responses`，ProviderFactory.cs:101,124 / StaticProvider.cs:74,90）。**本轮亲核 pi 两侧文案均成立** | `docs/superpowers/specs/2026-11-09-S8-AgentFacade-design.md` §6 两行；pi models.ts:961/984/1167-1185；`src/PiUnityAgent.Ai/Providers/Models.Capabilities.cs:23,29,62,68`、`ProviderFactory.cs:99-102,122-125`、`StaticProvider.cs:72-74,88-90` | 无 |
| **U-4** 工厂层 StreamDeferred 同步抛 ModelsError，pi 包 lazyStream 终态 Error | **已登记** spec §6 新行"D3 provider 直调错误面"（B）：直调同步抛、经 Models.StreamDeferred 消费等价终态 Error；代价=绕过 Models 的调用方需 catch | `ProviderFactory.cs:91-104`（:99-101 同步 throw）、`StaticProvider.cs:67-74`；外层等价=Models.Capabilities.cs:16 LazyStream.Run 包装 | 无 |
| **U-5** Continue 排空竞态恢复（Prepend+文案替换）为未登记 D | **已登记** spec §6 新行"Continue 排空竞态恢复"（D）：收益=并发 Continue 不丢消息；代价=internal API+文案替换路径 | `src/PiUnityAgent.Core/Agent/Agent.Queues.cs:75-88,95-105`；`Prepend`=`src/PiUnityAgent.Core/Agent/QueueMode.cs:71-82` | 无 |
| **U-6** Abort 吞 ObjectDisposedException | **已登记** spec §6 新行"Abort 吞 ObjectDisposedException"（D）：快照后取消、CTS 已 Dispose 吞 ODE；代价=吞掉"已无 run"无害信号 | `src/PiUnityAgent.Core/Agent/Agent.cs:106-125`（:117-124 try/catch ODE） | 无 |
| **U-7** listener 快照迭代 vs pi Set 活迭代 | **已登记** spec §6 新行"listener 快照迭代"（A/D）：C# 活迭代遇并发修改会抛；代价=新订阅者下一事件才收到 | `src/PiUnityAgent.Core/Agent/Agent.cs:281-295`（:289 `_listeners.ToArray()` 锁内快照） | 无 |
| **N-1** null 守卫 ArgumentNullException | **已登记** spec §6 新行"null 守卫"（B/D，一笔带过） | `AgentState.cs:60`（Model set）/:85（Tools set）/:102（Messages set）/:155（AddMessage）；`QueueMode.cs:45`（Enqueue） | 无 |
| **N-2** RetryDelayMs 负值钳 0 | **已登记** spec §6 新行"D17 负 delay 钳 0"（D）：净行为等价；代码注释+D17 台账备注在案 | `src/PiUnityAgent.Ai/Utils/Retry.cs:107-115`（:108 base 钳 0、:114 cap 钳 0，注释 :112-113 说明 .NET 与 setTimeout 差异） | 无 |
| **N-3** 正则多加 CultureInvariant | **已登记**：§6"D17 正则"行改写为"逐字移植 + IgnoreCase + CultureInvariant"并在代价列注明 N-3；§4.8 同步订正 | `Retry.cs:226`（`RegexOptions.IgnoreCase | RegexOptions.CultureInvariant`）；spec §4.8/§6 | 无 |
| **N-4** provider 层新增 FetchDeferredAsync | **已登记** spec §6 新行"provider 层 `FetchDeferredAsync`"（B）：纯增量便捷，`.result()` 便捷 pi 只在 Models 层 | `ProviderFactory.cs:107-112`、`StaticProvider.cs:76-80` | 无 |
| **N-5** Models.StreamDeferred 未传 observer | **已登记** spec §6 新行"LazyStream observer"（C/D8）：与 stream/streamSimple 同口径，D8 遥测后置既有边界 | `Models.Capabilities.cs:16`（`LazyStream.Run(model, async () => …)` 两参调用）；`Events/LazyStream.cs:17-20`（第三参 `ILazyStreamObserver? observer = null` 缺省 null） | 无 |
| **N-6** DefaultStreamFn.IsConfigured 增量成员 | **已登记** spec §6 新行"DefaultStreamFn.IsConfigured"（B） | `src/PiUnityAgent.Core/Agent/DefaultStreamFn.cs:25` | 无 |

**14/14 全部闭环**：U-1/U-8 代码修复（对齐后不再是偏离），U-2..U-7 + N-1..N-6 共 12 项全部补进 spec §6（其中 U-3 按首审计建议拆为两层表述）。逐行核验无一与代码相悖。

---

## 2. 新增偏离扫描（整改面）

`git diff 926b6b1..HEAD --stat` 全集 = 2 代码 + 2 测试 + 3 文档（audit 报告 / audits README / spec），无其他文件被动。逐文件审阅：

- `FauxProvider.cs`（+3/-1）：仅删抛出行+补注释；cancel 路径 grep 全文确认无其他 token 读取（:603-631 唯一 cancel 实现，其余 `ThrowIfCancellationRequested` 均在 stream/fetch 路径，pi 同样读 signal——不构成偏离）。
- `Agent.cs`（+2/-1）：仅 TurnEnd 判据替换+注释；周边归约面未动。
- 两测试文件：仅新增用例，未改既有断言。
- spec `33d837f`：§4.7.1/§4.7.4/§4.8 订正 + §6 新增/改写行；§0-§5 行为描述未动（除 header 状态行）。
- `docs/audits/README.md`、首审计报告落盘：纯文档。

**新偏离 0 条。** 整改面外未发现首审计漏报（本轮对 U-1/U-8 双源亲核 pi 原版；§6 其余行抽验与首审计一致）。

---

## 3. 非阻塞附注（R-1..R-3，均不构成偏离/缺口）

- **R-1**（引用精度）：spec §6"U-2 行"pi 侧引用 `faux.ts:322-336,678-681`；实际 `createErrorMessage` 定义在 faux.ts:327-336，fetch/cancel 错误终态**调用点**在 faux.ts:536-541（无排队响应）、:579（stream catch）、:636（fetch resolve catch）、:648（fetch 外层 catch）；:678-681 是 provider 工厂对象 return，与错误终态无关。行文实质（provider 级 api/provider + `requestModel.id` + `Date.now()`）核验成立，仅行号引用失准。下次触碰该行时顺手订正即可，无需单独动作。
- **R-2**（台账时间戳滞后）：`docs/待办-后置能力.md:7`"最后更新"仍为 2026-11-09"独立偏离审计待做"；D3/D17/D23 三行实质内容（状态/锚点/完成提交 `0f9f291`/`3e198c0`/`ca663e7`/`6f2ec37`、`67f3ad0`、`5ef3e19`/`efe9fe1`）核验无误。建议下次文档提交把该行刷新为"复核通过"。
- **R-3**（自引用，记录性）：spec header 记录 HEAD `c8ce5aa`（代码终态），文档提交 `33d837f` 在其后落地，属文档无法自引用的固有滞后，非缺陷。

另记一处**首审计报告**的行号小漂移（非本轮新发现）：N-1 引 `AgentState.cs:60,78,95`，实际守卫在 :60/:85/:102/:155（另有 :60 为 Model setter，首审计未列全）；守卫存在性与登记内容均成立，不影响结论。

---

## 4. C 类 Dn 台账复核

| 项 | 状态 | 本轮核验 |
|---|---|---|
| D3 | 已完成 | 形状/验收锚点在台账（`docs/待办-后置能力.md:13`）；`DeferredProviderCapabilityTests/DeferredModelsTests/FauxDeferredTests + DeferredWindowTests` 在；U-1 修复后 FauxDeferredTests 11 用例全绿（含新增取消用例） |
| D17 | 已完成 | 台账 :27；RetryTests 在；N-2/N-3 登记后台账 D17 备注（负 MaxAgentDelayMs 钳 0）与代码一致 |
| D23 | 已完成 | 台账 :33；AgentQueueTests/AgentQueueIntegrationTests 在；U-5 的 Prepend 属 D23 队列的 internal 增量，登记于 spec §6 |
| 其余 Dn | 未到期 | check-deferred 亲跑：`code markers: D11=1, D15=2, D18=1, D19=1, D20=1, D21=1, D22=1, D4=2, D5=2, D8=1`，全部为台账在案的未开工/后续扩展注记，无越期 stub |

---

## 5. S7 零回改 + 门禁（本审计者亲跑，2026-11-10）

- **S7 零回改**：`git diff 59475c8..HEAD --name-only -- src/PiUnityAgent.Core/Agent/AgentLoop*.cs AgentEvent.cs AgentContext.cs AgentLoopConfig.cs AgentLoopTypes.cs DefaultStreamFn.cs ToolCallHooks.cs ToolExecution.cs ToolLoadout.cs AgentToolCallOutcome.cs` → **空**。（本轮把 `AgentTranscript.cs` 一并放入 glob，出现的 diff 经查为 S8 新建文件 `e301c96`，59475c8 处不存在，非 S7 文件。）
- Ai 侧 drift 仅 `Api/ProviderRetry.cs` 1 行注释（a333e2c，D17 去钉，计划内）+ D3/D17 计划内文件。
- `dotnet build PiUnityAgent.slnx -c Release` → **0 警告 0 错误**。
- `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj` → **96/96 通过**（首审计 95 + U-8 新用例 1）。
- `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj` → **1133/1133 通过**（首审计 1132 + U-1 新用例 1）。
- `python scripts/check-deferred.py --root . --current-stage S8 --gate exit` → **OK，exit 0**。
- 新用例非空洞抽检：`Cancel_With_PreCancelled_Token_Still_Completes`（DeferredTests.cs:325-352，直调 provider 层 cancel + 计数/Id 双断言）、`Empty_Error_Message_Is_Not_Recorded`（AgentLifecycleTests.cs:165-186，StopReason/消息内容/State.ErrorMessage 三点断言）均为行为断言。

---

## 6. 最终建议

**PASS_WITH_REGISTERED_DEVIATIONS**。首审计 8 条未登记偏离 + 6 条登记瑕疵全部闭环（2 条代码修复对齐 pi、12 条 spec §6 登记，U-3 按建议拆两层表述）；整改面无新增偏离；零 Shape gap、零 Critical/Important。R-1..R-3 为非阻塞附注，不构成合并障碍。

按 SOP §8，S8 可进入实机验证/合并批准环节（合并门禁见记忆 taiwu-merge-gate-require-user-ok，实机+用户同意仍为必要条件）。R-1/R-2 两条 cosmetic 项建议在下次文档提交顺带订正。
