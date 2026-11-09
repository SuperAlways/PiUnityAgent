# S6 偏离审查（2026-11-09）

> 审计者：独立 subagent（GLM，经 Claude Code Agent 会话；非实现者）
> 参考版本：
> - pi 原版 `v1.5.0` / `f9a263655`（`D:/shuoshu/taiwuask/pi-study/pi`，本审计实核 `git log -1` = `f9a263655 Release v1.5.0`）——行为/形状/wire 唯一真相源
> - pi.net `main` / `2d6757b`（`D:/shuoshu/taiwuask/pi-study/pi.net`，实核 `git log -1` = `2d6757b chore(memory): …`）——仅 C# 表达参考
> - UnityLlmTransport `4c0cc4b` / `0.6.0`——本阶段零引用，仅核边界（grep 实证见 §2.3）
> - Unity 2022.3 + netstandard2.1——仅 A 类硬约束依据
> 范围：`70c58c4..5e186c8`（分支 `feature/s6-tools`，5 个提交：4707cd9 / 54781a8 / bf8e296 / 36303f9 / 5e186c8）
> 结论：**PASS_WITH_REGISTERED_DEVIATIONS**

---

## 1. 公共形状矩阵

逐项对照 pi `packages/agent/src/types.ts:424-515`（工具段）、`packages/ai/src/types.ts:444-456`（ToolCall）、`packages/ai/src/types.ts:771-776`（Tool）、`packages/ai/src/utils/validation.ts`（校验）。所有"我们"行号以本 worktree HEAD `5e186c8` 为准。

| # | 区域 | pi 原版 | pi.net | 我们 | 判定 | 类别/缺口 |
|---|---|---|---|---|---|---|
| 1 | `AgentTool` 泛型 `TParameters/TDetails` | `types.ts:478` 泛型接口 | `AgentTypes.cs` 独立 record + `ToolSchema`/`object?` | `src/PiUnityAgent.Core/Tools/AgentTool.cs:10-47`（无泛型，`JsonObject`/`JsonNode?`） | 形状完整、类型擦除 | **A**（spec §6 行1 已登记） |
| 2 | `AgentTool extends Tool`（结构继承四字段） | `types.ts:478` 继承 `Tool<TParameters>`（`ai/types.ts:771-776`：name/description/parameters/constrainedSampling） | 独立 record + `Definition` 投影 | `AgentTool.cs:12-22` 四字段自持；`AgentTool.cs:40-46` `Definition` 投影 | 等价表达；四字段重复由 S9 核对 | **B/D**（spec §6 行2 已登记） |
| 3 | `label`（必填） | `types.ts:479` `label: string` | `AgentTypes.cs` 构造参数 `string Label` | `AgentTool.cs:19` `required string Label` | 一致（required） | 无偏离 |
| 4 | `prepareArguments?` | `types.ts:481-484` `(args: unknown) => AgentToolParams`（须返回对象） | `Func<object?, object?>?` | `AgentTool.cs:25` `Func<JsonObject, JsonNode?>?`（入参收窄 JsonObject，返回放宽 JsonNode?） | 形状在；入参收窄有依据（pi `ai/types.ts:447` `arguments: JsonObject` 必为对象） | **A/B**（spec §6 行3 已登记；返回可空为擦除副作用，见附注 6.4） |
| 5 | `outputSchema?` | `types.ts:486-489` `object?` | `ToolSchema?` | `AgentTool.cs:28` `JsonObject?` | 一致 | **A**（spec §6 行1 覆盖） |
| 6 | `execute` | `types.ts:491-498` `(toolCallId, params, signal?: AbortSignal, onUpdate?) => Promise<AgentToolResult>` | `Func<string, object?, CancellationToken, AgentToolUpdateCallback?, Task<AgentToolResult>>` | `AgentTool.cs:31` 同 pi.net 签名（params 为 `JsonNode?`） | 一致；AbortSignal→CT | **A**（spec §6 行4 已登记） |
| 7 | `replay?` | `types.ts:500` `"never" \| "safe"` | pi.net 未移植 | `ToolExecutionTypes.cs:11-15` `enum AgentToolReplay { Never, Safe }` + `AgentTool.cs:34` `AgentToolReplay?` | 枚举替代字符串联合；S6 不序列化即无 wire 面 | **A**（spec §6 行6 已登记） |
| 8 | `executionMode?` | `types.ts:502-515` + `types.ts:47` `"sequential" \| "parallel"` | `ToolExecutionMode?` | `ToolExecutionTypes.cs:4-8` `enum ToolExecutionMode { Sequential, Parallel }` + `AgentTool.cs:37` | 枚举替代字符串联合 | **A**（§3.2 有述；§6 表无独立行，见 §3 未登记项 U-3） |
| 9 | `AgentToolResult` 六字段 | `types.ts:424-437`：content（必填 Text/Image 数组）/ details（`T`，默认 `JsonValue\|undefined` 即可空）/ structuredContent? / usage? / isError? / terminate? | `AgentTypes.cs` record 六参数 + `FromError` | `AgentToolResult.cs:12-27` 六字段一一对应（Content 缺省空数组、Details/StructuredContent/Usage 可空、IsError/Terminate 默认 false） | 形状完整；bool 非 nullable 表达 TS 可选 falsy 缺省 | **A/B**（spec §6 行8 已登记） |
| 10 | `AgentToolUpdateCallback` | `types.ts:463` 同步 `(partialResult) => void` | `delegate Task` | `AgentToolUpdateCallback.cs:4` `delegate Task AgentToolUpdateCallback(AgentToolResult)` | Task 化（pi.net 同）；"settle 后忽略"归 S7 执行器（pi `agent-loop.ts:832-836` acceptingUpdates 门） | **B**（spec §6 行5 已登记） |
| 11 | `AgentToolCall` 类型别名 | `types.ts:58` `Extract<…>` | 复用 ToolCallContent | 复用 `Ai.Types.ToolCall`（`ToolCall.cs:7`） | C# 无别名需求 | **A**（spec §6 行9 已登记） |
| 12 | `validateToolCall` 首匹配 + not found 文案 | `validation.ts:37-46`：`tools.find` 首匹配；`Tool "<name>" not found` | `Pi.Ai/Utils/Validation.cs` | `ToolValidation.cs:20-25`：`FirstOrDefault` 首匹配 + 同文案（`Tool \"nope\" not found`，测试 `ToolValidationTests.cs:23-28` 锁定） | 逐字一致 | 无偏离 |
| 13 | `argumentsError` 真值语义 | `validation.ts:55-57` `if (toolCall.argumentsError)`（JS 真值：空串不触发） | 同 JS 语义 | `ToolValidation.cs:30` `is { Length: > 0 }`（null/空串不触发，非空含纯空白触发）——与 JS 真值等价；提交 54781a8 专修此点；测试 `ToolValidationTests.cs:31-46` 锁定空串与 `" "` 两态 | 等价 | 无偏离 |
| 14 | 深拷贝防污染 | `validation.ts:58` `structuredClone(toolCall.arguments)` | — | `ToolValidation.cs:37` `toolCall.Arguments.DeepClone()`；测试 `ToolValidationTests.cs:72-79` 锁定不改输入 | 等价（`ToolCall.Arguments` 类型为非空 `JsonObject`，`ToolCall.cs:17`，与 pi `ai/types.ts:447` 一致，无 null 路径） | 无偏离 |
| 15 | 校验器缓存 | `validation.ts:6,8-18` `WeakMap<object, CoercingValidator>`（按 schema 对象身份） | ConditionalWeakTable | `ToolValidation.cs:17,49-50` `ConditionalWeakTable<JsonObject, ToolSchemaValidator>`（CWT 按引用身份）；编译参数 `coerce: true + formats + unresolvedRefs:"reject"` 对应 `ToolSchemaValidator.cs:21,30-37` | 实例生命周期语义等价 | **A/B**（spec §6 行13 已登记） |
| 16 | 错误文案骨架 | `validation.ts:63-68`：`  - <dotted path>: <message>` 逐条 join；整体 `Validation failed for tool "<name>":\n<errors>\n\nReceived arguments:\n<JSON.stringify(arguments, null, 2)>` | 同构 | `ToolValidation.cs:41-46` 同骨架；路径格式化 `ToolValidation.cs:52-57` 与 `validation.ts:21-27` 逐算法等价（空指针→`root`、`~1`/`~0` 逆序替换、点号 join）；缩进 JSON 走 `SchemaJson.StringifyIndented`（`SchemaJson.cs:293-298`，手写 `JSON.stringify(v,null,2)` 等价渲染，非 STJ 默认编码器，非 ASCII 不转义）；测试 `ToolValidationTests.cs:57-70` 锁定三段形态 | 逐字一致 | 无偏离 |
| 17 | 错误类型 | `Error` | `InvalidOperationException` | `InvalidOperationException`（`ToolValidation.cs:23,32,44`） | 异常类型化 | **B**（spec §6 行12 已登记） |
| 18 | pi-schema 引擎 + formats | `@earendil-works/pi-schema`（formats 21 种，`packages/schema/src/formats/index.ts:31-53`） | 自研 C# 子集 | `src/PiUnityAgent.Ai/Utils/Schema/*` 8 文件 internal；formats 19 种（`SchemaFormats.cs:19-41`，缺 `hostname`/`idn-hostname`，`SchemaFormats.cs:9-15` 注明"缺失时按注解忽略"）；`FormatOf` 未知 format→null→忽略（`SchemaChecker.cs:279-283`） | 子集移植，缺项登记 | **B + C/D21**（spec §6 行10/11 已登记；"19 种"数字勘误见 §3 U-4） |
| 19 | `unresolvedRefs: "reject"` | pi-schema 语义：悬空 $ref 只在该处拒绝 | 同 | `SchemaResolver.cs:63-101`：Resolve 失败→false schema + 记入 `_unresolved`；解释层产出 `unresolved reference …`（`SchemaExplainer.cs:119-121`）；语料含 3 条悬空 $ref 向量（`coerce/ref/dangling`、`ref/chain-broken`、`ref/anchor-missing`）全绿 | 一致 | 无偏离 |
| 20 | 校验器公共面 | pi-schema 独立包公开 | pi.net `public` | 三类型 internal（`ToolSchemaValidator.cs:9,12,24`），测试经 `InternalsVisibleTo`（`src/PiUnityAgent.Ai/Properties/AssemblyInfo.cs:3`）；唯一新增公开类型 = `ToolValidation`（`ToolValidation.cs:15`）+ Core 四类型 | 收敛公共面 | **D/A**（spec §6 行14 已登记） |
| 21 | 注册容器 / 重名 | pi 无 registry，`tools.find` 首匹配（`agent-loop.ts:707`） | 无 | 无 registry；`ValidateToolCall` 首匹配 + 测试 `ToolValidationTests.cs:82-98` | 一致，不造容器 | **D/YAGNI**（spec §6 行15 已登记） |
| 22 | `FromError` | `agent-loop.ts:922-927` `createErrorToolResult`：`content:[text]` + `details:{}`（**isError 不在结果对象上**，isError 由 outcome 承载） | `AgentTypes.cs` `FromError(text, details)` 透传 details | `AgentToolResult.cs:30-36`：text + `details ?? {}` + `IsError=true` | 细节见 §3 U-2：pi 的 isError 在 outcome 层，我们的 FromError 把它落在结果上（与 pi 工具作者契约 `types.ts:430-433`"返回 isError:true 的结果"一致，与 pi.net 一致） | **D**（spec §6 行16 已登记，措辞勘误见 U-2） |
| 23 | 明确不在 S6 范围 | `AgentToolCallOutcome`（`types.ts:440-447`）、`runToolCall`（`agent-loop.ts:813-822`）、hooks、`promptSnippet/promptGuidelines`（pi 接口未声明，由 coding-agent 工具动态挂载；pi.net 以 record 属性表达）、`ToolExecutionMode` 批次默认、`shouldTerminateToolBatch`（`agent-loop.ts:694-696`） | 有部分 | 均未定义（spec §1.2 明示排除；`Terminate` 字段形状已保留，注释钉住"全批 true 才生效"归 S7，`AgentToolResult.cs:26-27`） | 形状保留、行为后置，范围排除有明示 | 非 shape gap（S7/S9 范围） |

**结论**：S6 涉及的 pi 公共形状 23 项全部有对应成员，无形状裁剪。

---

## 2. 行为 / wire / 生态

### 2.1 行为语义实核

- **argumentsError 真值**：pi JS `if (toolCall.argumentsError)` → 空串 falsy 不触发；我们 `ToolValidation.cs:30` `is { Length: > 0 }` 等价（null/空串跳过、非空含空白触发）。提交 54781a8 即为此修复（初版 `!string.IsNullOrEmpty`… 实为等价实现，但补了双态测试钉住）。✓
- **coercion 管线**：`ToolSchemaValidator.Parse`（`ToolSchemaValidator.cs:57-61`）= Coerce → Check(coerced) → 失败时 Errors(coerced)，即"先 coercion 再校验、错误按 coerced 值报告"。pi `compile(..., { coerce: true }).parse()` 同管线。差分语料 368 向量（222 ok / 146 fail，`Fixtures/schema-validation-corpus.json`）逐向量断言 `ok/coerced/errors(path|keyword|message)` 全等，覆盖 `coerce/num|int|bool|arr|str|obj|union|bytes|null/*` 与 26 种错误 keyword（含 `coerce/arr/wrap-string` 单值→数组、`coerce/null/drop-optional` 可选 null 丢弃、`coerce/ref/dangling` 悬空 $ref）。✓
- **首匹配**：`FirstOrDefault` + 测试锁定（`ToolValidationTests.cs:82-98`）。✓
- **缓存**：CWT 按 `JsonObject` 引用身份缓存编译产物（`ToolValidation.cs:17,49-50`）；schema 编译后固定（外部改 schema 不影响已编译 validator）与 pi WeakMap 同。注意：**spec §5.1 声明的缓存单测未落**（见 §3 U-1）。
- **schema 只读**：`ToolSchemaValidator` 构造即 `Warm()` 预索引（`ToolSchemaValidator.cs:35-36`），校验路径不改 schema。✓
- **未知/不支持关键字不炸实现**：`unevaluatedProperties/Items` 被索引但 Checker 不消费（`SchemaResolver.cs:17` SchemaKeywords 列入、`SchemaChecker.cs:12-16` 注明不支持）→ 注解忽略；`$dynamicRef` 不在 `$ref` 分支（`SchemaChecker.cs:96`）→ 同注解忽略；未知 format → `FormatOf` 返回 null → 忽略（`SchemaChecker.cs:279-283`）。符合 spec §4.4"注解忽略/按可解析部分校验，不得抛实现异常"。✓（D21 三项行为口径与台账一致。）

### 2.2 wire

- 本阶段无新 wire 面：`AgentToolReplay`/`ToolExecutionMode` 为枚举且无序列化路径（无 JsonConverter/写库代码）；`AgentTool.Definition` 产出的 `Ai.Tool` 即 S1 已锁的 provider `tools` wire 形状（`Tool.cs:6-17`）。pi 侧枚举来源是字符串联合，但 pi 自身也只在运行时比较、无 wire 字符串落点差异（工具执行模式不进 provider 请求）。**登记口径成立**（spec §3.2"未序列化即无 wire 面"）。
- 校验错误文案（回给模型的文本）逐字核：第 1 节 #12/#16。唯一字面差异是空 errors 集合时的兜底行（§3 U-1）。

### 2.3 生态边界（grep 实核，均无输出）

- `using UnityEngine|using UnityLlmTransport|using GameData|using HarmonyLib` in `src/PiUnityAgent.Ai` + `src/PiUnityAgent.Core`：**无**。
- 旧命名空间 `namespace Pi.Ai.` 残留：**无**。
- public schema 类型泄漏（`public sealed record SchemaValidationError|SchemaParseResult` / `public sealed class ToolSchemaValidator`）：**无**（三类型均 internal，`ToolSchemaValidator.cs:9,12,24`）。
- csproj 越界依赖（HttpClient/Unity*/GameData/Harmony）：**无**。
- TFM：Ai/Core 均 `netstandard2.1`；Ai 仅 `System.Text.Json 8.0.5` + `System.Threading.Channels 8.0.0`（无新增 NuGet，spec §2 承诺成立）；Core 仅 ProjectReference Ai。
- 测试工程 net10.0 经 `InternalsVisibleTo` 消费 internal schema 面（`AssemblyInfo.cs:3`）。

### 2.4 门禁（审计者独立复跑，2026-11-09）

- `dotnet build PiUnityAgent.slnx -c Release`：**0 警告 0 错误**。
- `PiUnityAgent.Ai.Tests`：**1047/1047 通过**（= 671 既有 + 368 语料向量 + 1 语料守卫 + 7 ToolValidation）。
- `PiUnityAgent.Core.Tests`：**7/7 通过**。
- `python scripts/check-deferred.py --root . --current-stage S6 --gate exit`：**OK**（registry 21 条；code markers `D21=1` 及各既有 Dn 计数一致）。

---

## 3. 未登记偏离 / Shape gap

**Shape gap：0 条。** 未登记偏离均为 Minor 级（不涉形状裁剪、不涉行为主干），建议补登记/补测试，不阻塞合并。

| ID | 项 | 证据 | 为什么算未登记 | 建议 |
|---|---|---|---|---|
| U-1 | 空错误集兜底行 `  - root: invalid` | `ToolValidation.cs:43` `var errorText = errors.Length > 0 ? errors : "  - root: invalid";`；pi `validation.ts:63-66` 无此兜底（errors 为空时产出 `…":\n\n\n\nReceived arguments:` 空行骨架） | 行为语义差异（错误文案可观测），spec §6 表与 §4.1 均未列 | 二选一：删兜底改回 pi 逐字；或在 spec §6 补一行 D 类（"防御性兜底：explainer 对 invalid 值零产出时给占位行"）。实践可达性低（Check=false 而 explainer 零产出未在语料中出现），任一处理皆可 |
| U-2 | `FromError` 的 pi 描述措辞 | pi `agent-loop.ts:922-927` `createErrorToolResult` 只设 `content`+`details:{}`，**不设 `isError`**（isError 由 `AgentToolCallOutcome`/执行器承载）；spec §6 行16 与 `AgentToolResult.cs:29` 注释称"pi createErrorToolResult 等价：文本错误 + details={} + isError" | 登记 D 类行存在，但"与 pi 一致"的表述不实——我们多设了 `IsError=true`（该行为与 pi 工具作者契约 `types.ts:430-433` 及 pi.net `FromError` 一致，方向无害） | 修 spec §6 行16 / `AgentToolResult.cs:29-30` 注释措辞："对齐 pi 工具作者契约（返回 isError:true 的结果）+ createErrorToolResult 的 details:{} 缺省"，不必改代码 |
| U-3 | `ToolExecutionMode` 字符串联合→枚举未入 §6 表 | pi `types.ts:47`；我们 `ToolExecutionTypes.cs:4-8` | §3.2 正文有 A 类表述，但 §6 表只登记了 `replay` 行，`executionMode` 无行 | spec §6 补一行 A 类（与 replay 行合并亦可） |
| U-4 | formats 计数"19 种"失准 | pi `packages/schema/src/formats/index.ts:31-53` 实为 **21** 种；我们 19 种（`SchemaFormats.cs:19-41`）；spec §4.4 写"format（pi-schema 19 种）" | 数字与 pi 对不上（21−2=19，D21 行"缺 hostname/idn-hostname 两项"是对的） | spec §4.4 改为"21 种（缺 hostname/idn-hostname，见 D21）" |
| U-5 | spec §5.1 声明的三条测试未落 | spec §5.1 列"缓存：同一 schema 实例复用编译结果""单值→数组""可选 null 丢弃"为 `ToolValidationTests` 内容；实际 `ToolValidationTests.cs` 仅 7 条（无缓存用例；coercion 只钉数字串） | spec 验收清单 vs 实际测试面不符。行为本身由语料（`coerce/arr/wrap-*`、`coerce/null/drop-optional`）与 CWT 实现保证 | 补一条缓存行为用例（如同 schema 实例外部改属性后行为不变，pi 编译后固定语义）+（可选）一条 wrap/null-drop 的 ToolValidation 级用例；或 spec §5.1 改口径注明由语料覆盖 |

netstandard2.1 适配专项核验（任务要求逐项判定）：

| 适配 | 位置 | pi.net 原写法 | 等价性判定 | 登记 |
|---|---|---|---|---|
| `[GeneratedRegex]` → `static readonly Regex`（含 `RegexOptions.CultureInvariant`） | `SchemaJson.cs:224-229` | `SchemaJson.cs`（pi.net）`[GeneratedRegex]` partial 属性 | 等价：GeneratedRegex 源生成默认 CultureInvariant，且仅匹配结果语义、无 wire；执行为解释执行（性能差异，非语义） | 建议 §6 补一行 A（见 U-6） |
| `System.Threading.Lock` → `object`（Monitor） | `SchemaResolver.cs:118`（`lock (_gate)` 用法 `:106`） | `SchemaResolver.cs:118`（pi.net）`private readonly Lock _gate = new();` | 等价：`lock(obj)`=Monitor，互斥语义相同；.NET 9 API 在 2.1 不可用，属硬约束 | 同上 |
| `ReferenceEqualityComparer` polyfill（BCL 为 .NET 5+） | `src/PiUnityAgent.Ai/Polyfills/ReferenceEqualityComparer.cs`（本分支新增，4707cd9） | pi.net 直接用 BCL `ReferenceEqualityComparer.Instance`（pi.net 目标更高 TFM） | 等价：`ReferenceEquals` + `RuntimeHelpers.GetHashCode` 即 BCL 语义；internal 不扩公共面 | 同上 |
| `IsExternalInit` polyfill ×2（Ai 既有、Core 本分支新增） | `src/PiUnityAgent.Ai/Polyfills/IsExternalInit.cs`、`src/PiUnityAgent.Core/Polyfills/IsExternalInit.cs` | pi.net 不需要（更高 TFM） | 等价：`#if NETSTANDARD2_1` 标准手法，使 init-only/record 在 2.1 可用 | 同上 |

U-6（合并到上表）：四项 2.1 适配均为 A 类硬约束且代码注释已写替代方案；plan 也预授权"最小替代适配并报告"。但 spec §6 表无对应行——建议补一行汇总（"netstandard2.1 适配：GeneratedRegex→static Regex / Lock→object / ReferenceEqualityComparer+IsExternalInit polyfill | A | 语义等价，代价=regex 解释执行+Monitor"）。

---

## 4. C 类 Dn 检查

- **D21（JSON Schema 高级关键字与格式）三件套**：
  - 台账行：`docs/待办-后置能力.md:31`（ID/能力/pi 参考/形状锚点/验收测试锚点/目标阶段=后续扩展/验收口径/状态=未开工/备注 全列齐，"最后更新"行同步）。
  - 形状锚点：`SchemaChecker.cs:16` `TODO(后续扩展, D21): unevaluatedProperties/Items、书签 $dynamicRef 动态作用域、hostname/idn-hostname format 未移植。`；`SchemaFormats.cs:9-15`（hostname/idn-hostname 缺失说明）。锚点有效、与台账指认一致。
  - 验收测试锚点：`待补：语料扩展差分（SchemaValidationCorpusTests 增加上述向量）`——写明了落点与向量口径；当前语料确无这三项向量（实测确认），与"未开工"状态自洽。**注意**：补向量时须同时登记 pi 侧实况（unevaluated* / $dynamicRef / 两个 format 在 pi-schema 全量支持）。
  - `check-deferred --current-stage S6 --gate exit` 复跑 OK（21 条，D21=1）。✓
- **不承接既有 Dn**：D16（partial 快照/repairJson，目标 S7）/ D3（Deferred，S8）/ D11（transcript 重放，S9）在 S6 均无实现压力，台账状态未动。✓
- **未新立 C 项**：spec §8 声明"校验行为本身完整落地，不把未实现执行器登记为 Dn"——与代码一致（执行器/hook/事件均未出现在 S6 diff）。✓

### spec §6 偏离表逐行核对（18 行）

行1-18 逐行比对代码：**17 行与代码一致**（含 5e186c8 对 FromError 行的措辞补注）。唯一不实表述 = 行16"pi createErrorToolResult … + isError"（见 U-2，行为方向正确、描述失准）；另行10"pi-schema … 19 种"格式数在 §4.4 出现（见 U-4，§6 行10 本身未写数字）。无"表有行、代码无对应"的虚登记。

---

## 5. 必需动作

- [ ] U-1：空错误集兜底行——删除改回 pi 逐字，或在 spec §6 补 D 类行（实现者/控制器裁决）
- [ ] U-2：spec §6 行16 与 `AgentToolResult.cs:29` 注释措辞修正（isError 来源写清）
- [ ] U-3：spec §6 补 `executionMode` 枚举 A 类行
- [ ] U-4：spec §4.4 format 计数改为"pi 21 种、我们 19 种（缺 hostname/idn-hostname）"
- [ ] U-5：补缓存行为用例（或改 spec §5.1 口径）
- [ ] U-6：spec §6 补 netstandard2.1 适配汇总行（A）
- [ ] D21 补语料向量时：验收测试锚点从"待补"改实名，并回填台账状态

以上全部为 Minor；**无 Critical/Important，无 shape gap，无阻断项**。

## 6. 审计者附注

1. **误报澄清**：`PrepareArguments` 返回 `JsonNode?`（pi 返回"必须为对象"的类型）——spec §6 行3 的 A/B 判定已覆盖入参收窄；返回放宽是同一擦除逻辑的自然结果（S7 校验器兜底），不单独立项。`AgentToolResult.Content` 缺省空数组 vs pi 必填数组：缺省化是 record 缺省值惯例（pi.net 同为可选参数），不算裁剪。
2. **无法验证项——语料生成器缺席**：语料文件头与测试注释称"由 `tools/schema-oracle/gen.mjs` 对上游 pi-schema 差分生成"，但 pi.net@2d6757b 内**不存在**该工具（`git ls-files`/`find` 均无）。fixture 与 pi.net 测试 fixture **逐字节一致**（cmp 通过），其可信度等同 pi.net 自身测试语料；但"生成器是否排除过分歧向量"（语料偏置）无法在本环境复核。缓解：语料覆盖 368 向量、26 种错误 keyword、31 条错误路径（含 root 与多层嵌套），守卫测试断言覆盖下限。
3. **版本确认**：pi `f9a263655`（v1.5.0）与 pi.net `2d6757b` 均以本机 checkout `git log -1` 实证；UnityLlmTransport 本阶段零引用，仅边界 grep（§2.3），版本锁未重新核验（不适用）。
4. **pi.net 尚未移植而 pi 有的项**（S6 范围外，供后续阶段对齐时防漏）：pi.net `AgentTool` 已带 `PromptSnippet`/`PromptGuidelines` 属性（S9 素材，pi 侧由 coding-agent 工具动态挂载）；S9 动工时须按 pi `how-pi-agent-works` 的挂载语义落形状，勿以"pi 接口没写"为由漏掉。
5. **Doc 注释小勘误**（不影响行为，归入 U-2/U-4 一并处理）：`AgentTool.cs:21` 注释"false 折叠为 null"与实际类型不符——`Ai.Types.ConstrainedSampling`（`ConstrainedSampling.cs:9-13`）以 `Disabled` 变体表达 `false`，null 表"未指定"，比 pi 的 `false | Config` 多区分一档（S1 形状，方向无害）。
6. **审计方法自述**：schema 引擎 8 文件与 pi.net 源做逐文件 diff（namespace/C# 2.1 适配/D21 TODO 注释外零差异：SchemaCoercer/Explainer/Formats/Regexes 0 diff，Resolver 1 行，Checker 1 行，SchemaJson 3 处，ToolSchemaValidator 3 行可见性）；行为正确性以 368 向量差分语料（与 TS oracle 全等断言）+ 7 条 ToolValidation 聚焦测试为证据底座，未对 pi-schema TS 源做全量重读（超出单阶段审计深度，语料即此段的权威证据）。
