# S6 偏离复核（2026-11-09，第二次独立审计）

> 审计者：独立复核 subagent（GLM，经 Claude Code Agent 会话；非原审计者、非实现者）
> 参考版本：
> - pi 原版 `v1.5.0` / `f9a263655`（`D:/shuoshu/taiwuask/pi-study/pi`，本复核实核 `git log -1` = `f9a263655 Release v1.5.0`）——行为/形状唯一真相源
> - pi.net `main` / `2d6757b`（`D:/shuoshu/taiwuask/pi-study/pi.net`，实核 `git log -1` = `2d6757b`）——仅 C# 表达参考
> - Unity/netstandard2.1——仅 A 类硬约束依据
> 范围：`70c58c4..953b142`（分支 `feature/s6-tools`，HEAD=`953b142`；处置只发生在末提交 `953b142`，4 文件 +41/−4，已整体复核）
> 结论：**PASS_WITH_REGISTERED_DEVIATIONS**

---

## 0. 逐项核销矩阵（U-1~U-6）

| ID | 原审计处置建议 | 实际处置 | 复核判定 | 证据（file:line） |
|---|---|---|---|---|
| U-1 | 删兜底改回 pi 逐字，**或** spec §6 补 D 类行 | 选登记路线：§6 新增「空错误集错误文案兜底」行（D/低，注明"语料中未可达"） | ✅ 核销。pi 侧描述与实况一致：pi `validation.ts` 将 `result.errors.map(...).join("\n")` 直接内插，**无兜底**（errors 为空时产出空行骨架）；我方代码引用与实际逐字一致 | pi `packages/ai/src/utils/validation.ts:63-66`；我方 `src/PiUnityAgent.Ai/Utils/ToolValidation.cs:43`；spec `docs/superpowers/specs/2026-11-09-S6-工具契约与注册-design.md:325` |
| U-2 | 修 spec §6 行16 与 `AgentToolResult.cs:29` 注释措辞（isError 来源写清），不必改代码 | 两处均已重写：不再声称 createErrorToolResult 设 isError；改为"对齐 pi 工具作者契约（IsError=true）+ createErrorToolResult 的 details:{} 缺省；pi 的 isError 由 outcome 层承载" | ✅ 核销（残留 1 处措辞歧义 → 新发现 N-1）。核心事实全部实核无误：pi `createErrorToolResult` 只设 `content`+`details:{}` **不设 isError**；pi 工具作者返回的 `result.isError === true` 会传播进 outcome（`isError: result.isError === true`）；我方 FromError 设 `IsError=true` 与 pi 工具作者契约（`AgentToolResult.isError?` 字段）及 pi.net FromError（`IsError: true`）同向 | pi `packages/agent/src/agent-loop.ts:921-925`、`:854`、`packages/agent/src/types.ts:429-433`；pi.net `src/Pi.Agent/Types/AgentTypes.cs:77-79`；我方 `src/PiUnityAgent.Core/Tools/AgentToolResult.cs:29-36`；spec `:322` |
| U-3 | spec §6 补 `executionMode` 枚举 A 类行 | 已补一行 A 类 | ✅ 核销。三方列全对：pi 字符串联合；pi.net 也是枚举（复核确认）；我们 `ToolExecutionMode?` 枚举（Sequential/Parallel）+ `AgentTool.ExecutionMode` | pi `packages/agent/src/types.ts:47`；pi.net `src/Pi.Agent/Types/AgentTypes.cs:259-262`；我方 `src/PiUnityAgent.Core/Tools/ToolExecutionTypes.cs:4-8`、`src/PiUnityAgent.Core/Tools/AgentTool.cs:37`；spec `:326` |
| U-4 | §4.4 format 计数改为"pi 21 种、我们 19 种（缺 hostname/idn-hostname）" | 已改 | ✅ 核销。独立重数：pi `formats` 对象实数 **21** 条目（date…uuid，含 hostname/idn-hostname）；我们字典实数 **19** 条目，缺 hostname/idn-hostname，且 `SchemaFormats.cs:10-12` 注明缺失时按注解忽略 | pi `packages/schema/src/formats/index.ts:31-53`；我方 `src/PiUnityAgent.Ai/Utils/Schema/SchemaFormats.cs:22-40`（缺失说明 `:10-12`）；spec `:258` |
| U-5 | 补缓存行为用例（+可选 wrap/null-drop ToolValidation 级用例）或改 §5.1 口径 | 三条测试全补：单值→数组、可选 null 丢弃、同实例缓存 identity | ✅ 核销。三条真实存在、**均为实质断言（非空断言）**：①断言 `"7"` 经 wrap+整数 coercion 输出 `[7]`；②断言 `{"a":null}` 校验后键被丢弃（与语料 `coerce/null/drop-optional`（`{"a":null}`→`{}`）完全同型）；③`Assert.Same` 同一 schema 实例两次 `ValidatorFor` 返回同一编译器实例（若未缓存则必然失败）。`ValidatorFor` 为既有 internal（非本提交新造），测试经 InternalsVisibleTo 可达。两 coercion 行为另有语料向量差分背书 | 我方 `tests/PiUnityAgent.Ai.Tests/Utils/ToolValidationTests.cs:100-113`（wrap）、`:115-125`（null-drop）、`:127-132`（缓存）；`src/PiUnityAgent.Ai/Utils/ToolValidation.cs:49-50`（internal ValidatorFor + CWT）；`src/PiUnityAgent.Ai/Properties/AssemblyInfo.cs:3`（InternalsVisibleTo）；语料 `tests/PiUnityAgent.Ai.Tests/Fixtures/schema-validation-corpus.json`（`coerce/arr/wrap-string`、`coerce/null/drop-optional` 实抽核对） |
| U-6 | spec §6 补 netstandard2.1 适配汇总行（A） | 已补一行（A/D），覆盖四类适配 | ✅ 核销。四类逐项对代码实核全覆盖：①`[GeneratedRegex]`→`static readonly Regex`（含 `RegexOptions.CultureInvariant`，注释注明）；②`System.Threading.Lock`→`object`（Monitor，`lock (_gate)` 用法不变）；③IsExternalInit polyfill ×2（Ai+Core，均 `#if NETSTANDARD2_1`，**确为 IsExternalInit**）；④ReferenceEqualityComparer polyfill（internal sealed，`ReferenceEquals`+`RuntimeHelpers.GetHashCode`=BCL 语义；5 个调用点与 pi.net 逐一对应） | ①我方 `src/PiUnityAgent.Ai/Utils/Schema/SchemaJson.cs:224-229` vs pi.net `src/Pi.Ai/Utils/Schema/SchemaJson.cs:224,227`；②我方 `src/PiUnityAgent.Ai/Utils/Schema/SchemaResolver.cs:106,118` vs pi.net `src/Pi.Ai/Utils/Schema/SchemaResolver.cs:106,118`；③`src/PiUnityAgent.Ai/Polyfills/IsExternalInit.cs`、`src/PiUnityAgent.Core/Polyfills/IsExternalInit.cs`；④`src/PiUnityAgent.Ai/Polyfills/ReferenceEqualityComparer.cs` + 调用点 `SchemaCoercer.cs:446,449`/`SchemaExplainer.cs:131,135`/`SchemaResolver.cs:29`；spec `:327` |

**核销结论：6/6 全部按原报告 §5 处置，处置方向与 pi/代码事实一致。0 条未核销。**

顺带核销原审计附注 5 的 ConstrainedSampling 注释勘误：`AgentTool.cs:21` 已改为"以 Disabled 变体表达 false；null 表未指定"，与 `ConstrainedSampling.cs:13-16`（`Disabled`/`Config` 两变体）一致。✅

---

## 1. 门禁复跑（本复核者独立实跑，2026-11-09）

| 门禁 | 命令 | 结果 |
|---|---|---|
| Release 构建 | `dotnet build PiUnityAgent.slnx -c Release --nologo` | **0 警告 0 错误** |
| Ai 单测 | `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo -c Release` | **1050/1050 通过**（= 原审计 1047 + 3 条新测试） |
| Core 单测 | `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --nologo -c Release` | **7/7 通过** |
| 台账门 | `python scripts/check-deferred.py --root . --current-stage S6 --gate exit` | **OK**（registry 21 条；code markers `D21=1` 及既有 Dn 计数一致） |

与控制器申报的门禁数字（0/0、1050、7、21 条）全部吻合。

---

## 2. 修复完整性（diff `953b142` 是否引入新偏离/削弱测试）

`953b142` 实测 4 文件 +41/−4：

| 文件 | 变更 | 判定 |
|---|---|---|
| spec design.md（§4.4+§6） | §4.4 一行改写（U-4）+ §6 FromError 行改写（U-2）+ §6 三行新增（U-1/U-3/U-6） | 登记性变更，§6 表现为 21 行且每行有代码对应（抽查 U-1/U-3/U-6/FromError 四行逐一与代码比对） |
| `AgentTool.cs` | 仅注释 1 行（ConstrainedSampling 措辞） | 无行为变更 |
| `AgentToolResult.cs` | 仅注释 1 行（FromError 措辞） | 无行为变更 |
| `ToolValidationTests.cs` | **纯追加** 34 行 / 3 条测试，未改动任何既有测试 | 无削弱；断言均为实质断言（Equal/False/Same，含失败路径即失败的判别力） |

- 代码行为零改动（`ToolValidation.cs`/schema 引擎 8 文件均不在 diff 中）→ 无新行为偏离。
- 测试面只增不减 → 无削弱。
- 唯一瑕疵是 spec §6 FromError 行尾括注的措辞歧义 → 见 §3 N-1（Minor，登记文本层面）。

---

## 3. 新发现

| ID | 级别 | 项 | 证据 | 说明与建议 |
|---|---|---|---|---|
| N-1 | Minor | spec §6 `FromError` 行尾括注「（与 pi.net FromError 同）」位置歧义 | spec `:322`；pi.net `src/Pi.Agent/Types/AgentTypes.cs:77-79` | 该括注紧跟「pi 的 isError 在 outcome 层承载，不在结果对象上」，就地读会被误读为"pi.net FromError 也不在结果对象上设 isError"——**不实**（pi.net FromError 明确设 `IsError: true`）。若读作"我们 FromError 在 IsError=true 上与 pi.net 同向"则正确，但 details:{} 缺省这一点上我们与 pi.net 不同（pi.net 透传调用方 details，缺省 null；我们取 pi createErrorToolResult 的 `{}`），"全同"也不成立。建议下次触碰 spec 时把括注改为「IsError=true 与 pi.net FromError 同向；details:{} 缺省取 pi createErrorToolResult」或直接删除括注。纯登记文本问题，不改代码，不阻塞 |

除 N-1 外无新发现：未登记偏离 0 条、shape gap 0 条、行为/wire/TFM 边界复核未见回退（Ai/Core 仍 netstandard2.1，schema 三类型仍 internal，`ToolValidation` 仍为唯一新增公开面）。

---

## 4. 必需动作

- [ ] N-1：spec `:322` FromError 行括注措辞收敛（Minor；可与 D21 补语料向量等后续触碰 spec 的机会顺带处理，无需单开提交）
- [ ] （承原审计遗留，非本次范围）D21 补语料向量时：验收测试锚点从"待补"改实名，并回填台账状态

---

## 5. 复核者附注

1. **独立来源声明**：本报告所有 pi/pi.net 引用均由本复核者直接读取两参考仓库实核（非转抄原审计报告）；U-4 的 21/19 计数为独立重数；U-5 三条测试逐条读过断言体；U-6 四类适配逐文件比对 pi.net 原写法。
2. **U-5 测试形态说明**：spec §5.1（`:277`）对缓存用例给的两个示例形态是"可观测计数 或 schema 外部修改后行为不变"；实落的是第三种（`Assert.Same` 直接 identity 断言）。它直接证明主 claim"同一 schema 实例复用编译结果"（若每次重编译必然 Assert.Same 失败），属有效替代；"编译后固定/外部修改不可见"的 pi 语义未单独成测，由 CWT 实现与语料覆盖兜底——可接受，不另立案。
3. **任务描述中"IsInternalInit?"存疑标记已实核**：两个 polyfill 文件均为 `System.Runtime.CompilerServices.IsExternalInit`（标准手法，使 init-only/record 在 netstandard2.1 可用），无 IsInternalInit 之类成员。
4. **门禁数字吻合**：Ai 1050 = 1047（原审计基线）+ 3（新测试），与控制器申报一致；check-deferred code markers 与原审计记录的 Dn 计数逐项一致，未出现回归。
5. **无法验证项**（承原审计附注 2）：语料生成器 `tools/schema-oracle/gen.mjs` 在 pi.net@2d6757b 中仍不存在（本复核未重查生成器问题，fixture 可信度沿用原审计结论：与 pi.net 测试语料逐字节一致）；不影响 U-1~U-6 核销。
