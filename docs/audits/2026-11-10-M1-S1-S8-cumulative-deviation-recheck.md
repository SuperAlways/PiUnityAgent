# M1（S1–S8）累计偏离审计 复核（2026-11-10）

> **审计者**：独立复核审计 subagent（GLM / glm-5.3-flash，Claude Code agent 会话；非实现者、非首审计者、未参与修复批次；只读代码/文档/参考源，未改任何代码/tests/spec，唯一写入为本报告）
> **参考版本（本轮实核，非凭记忆）**：
> - pi 原版 `v1.5.0` / `f9a263655`（`D:\shuoshu\taiwuask\pi-study\pi`，`packages/ai/src`）——行为/形状/wire 唯一真相源（本轮亲读 `utils/overflow.ts` 195 行、`utils/string-enum.ts` 24 行、`utils/uuid.ts` 45 行、`session-resources.ts` 24 行、`index.ts` 导出面、`models.ts` applyAuth/cancelDeferred 段、`providers/faux.ts:327-336`）
> - pi.net `main` / `2d6757b`——本轮四 util 无 C# 移植，未采信其任何结论
> - UnityLlmTransport / Unity 2022.3 + netstandard2.1——本轮未触及（四 util 无外部依赖，Ai 层 netstandard2.1 下 `RandomNumberGenerator.GetBytes`/`Regex`/STJ Nodes 均为标准面）
> **范围**：修复 diff `6e9b46a..32f7430`（分支 `fix/m1-audit-fixes`，2 提交：代码 `955e7cc`（11 文件，+868/−1）+ docs `32f7430`（10 文件，+168/−23）；`git status` 实核工作区干净）。审计对象 = 首审计 `2026-11-10-M1-S1-S8-cumulative-deviation-audit.md` §5 全部必需动作（U-S1 / U-1 / U-2 / N-1..N-9）的整改落点，逐条回到 pi 原版源码行与代码行复核。
> **方法**：四 util 逐行对照 pi 同名 TS（正则逐条、分支逐段、字节布局逐位）；U-1 对照 pi `models.ts` applyAuth（:856-915）/`cancelDeferred`（:978-988）亲核三步管线顺序；U-2/N 系逐文件读 diff 并抽核被订正的行号引用是否与代码/pi 实码相符（AnthropicMessages.cs:732、OpenAiCompletions.cs:864/:869、OpenAiResponses.cs:1622、faux.ts:327-336、pi index.ts:33/:39/:42/:45 均亲核）；新 spec 的偏离表逐行对照代码签名；四套门禁由本审计者亲跑复现。**未复用实现者报告或首审计的任何未亲核结论。**
>
> **结论**：**PASS_WITH_REGISTERED_DEVIATIONS**

首审计 13 项必需动作（1 Shape gap + 2 Important + 9 Minor + 1 U-S1 关联登记）全部清偿且与 pi 源码逐点吻合；整改面新增 0 Shape gap / 0 Critical / 0 Important；新发现 2 条 Minor（R-1/R-2，均为新 util 的一行级登记缺口，见 §3）。门禁亲跑：Release 构建 0 警告 0 错误；Ai 单测 **1157/1157**（基线 1133 + 新增 24）；Core 单测 **96/96**；`check-deferred --current-stage S8 --gate exit` **OK**（registry 23、exit 0）。

---

## 1. 逐项核销矩阵（首审计 §5 必需动作）

判定口径：**核销** = 与 pi 真相源一致且有代码/测试证据；**核销（文档）** = 登记/现势动作已落且引用准确。

### 1.1 U-S1 · pi-ai 四公共导出（Shape gap，Important）→ **核销（补实现路线）**

| 核对点 | pi 真相源（本轮亲读） | 我们 | 结论 |
|---|---|---|---|
| 导出面四行登记 | `index.ts:33` session-resources / `:39` overflow / `:42` string-enum / `:45` uuidv7（亲核） | `PORTING.md:38-41` 四行 ✅ 回指新 spec | 一致 |
| Overflow 模式表 | `overflow.ts:39-69` 主模式 27 条逐字（含 :68 413 前缀）+ `:71` Cerebras 无响应体 + `:82-86` 非溢出 3 条 | `Utils/Overflow.cs:16-47`（27 条逐字同序同文案，全 IgnoreCase）、`:50-51`、`:55-60` | 一致 |
| isContextOverflow 三段 | `overflow.ts:144-184`：① 文案（non-overflow 先排除 → 主表 → Cerebras 仅 `provider=="cerebras"`，:148-155）；② 静默 `stop+input+cacheRead > window`（:160-165，严格大于）；③ length 止步 `output===0` 且 `>= window*0.99`（:170-175）；`contextWindow` 真值判断（0/undefined 同"未传"） | `Overflow.cs:66-110`：① :69-84（排除序同）；② :88-95（`is int w && w != 0` = pi 真值；`> stopWindow` 严格）；③ :99-107（`Output == 0`、`>= lengthWindow * 0.99`）；`int?` null + `!= 0` 覆盖 pi 两态 | 一致（偏离登记见 spec §3 行4） |
| isRecoverableLength | `overflow.ts:186-188` `length && desiredMaxOutput>0 && output<desired` | `Overflow.cs:117-118` 同式 | 一致 |
| getOverflowPatterns | `overflow.ts:193-195` `[...OVERFLOW_PATTERNS]`（新数组同实例） | `Overflow.cs:121-122` `(Regex[])Clone()`（新数组同实例） | 一致（spec §3 行7 B 登记） |
| StringEnum 形状/真值 | `string-enum.ts:14-24` `{type:"string",enum:[...]}`；`...(options?.description && {...})`：`""` falsy 省略、`" "` truthy 保留（:21-22） | `Utils/StringEnum.cs:16-41`：产出同形 JsonObject；`!string.IsNullOrEmpty`（:32/:36）= `""` 省略、空白保留 | 一致（spec §3 行1 B + 行4 A/B 登记） |
| uuidv7 语义 | `uuid.ts:8-45`：越界 `RangeError`（文案 `between 0 and 281474976710655`，:11）；普通调用 `max(now,lastOrdinary)` 单调、显式时间戳原样保留且不推单调钟（:14-16）；序列首次由 bytes[1..5]（40 位）播种、之后每调用 +1、用尽 `RangeError("UUIDv7 generator sequence exhausted")`（:20-30）；字节布局 ver=7（:36）/variant=10（:38）/bytes[12..15] 保持随机/bytes[11] 保留随机低位（:41）；48 位大端（:33-35）；8-4-4-4-12 小写（:43-44） | `Utils/Uuid.cs`：越界 `ArgumentOutOfRangeException` 文案逐字（:34-38）；单调/显式保留同语义（:42-51）；播种 40 位、`sequence++`、耗尽 `InvalidOperationException` 文案逐字（:56-75）；时间戳大端（:78-81）；`0x70|`/:84、`0x80|`/:86、bytes[11] 低位保留（:89，`(long)bytes[11] & 0x01` 已避 CS0675）；Format 8-4-4-4-12 小写（:96-114）；`MaxTimestamp=0xFFFFFFFFFFFF` 公共常量（:15） | 行为逐位一致（异常类型偏离 = spec §3 行2 A/B 登记；lock 并发 = 行5 D 登记） |
| session-resources 语义 | `session-resources.ts:5-24`：Set 注册 + 返回注销闭包；清理逐个 try/catch、聚合 `AggregateError(errors,"Failed to cleanup session resources")`（:21-23）；Set 活迭代 | `SessionResources.cs`：注册 + `IDisposable` 注销（:20-31/:66-82）；快照遍历 + 逐个 catch + `AggregateException` 同文案（:37-63） | 一致（快照遍历 = spec §3 行3 D 登记；IDisposable = 行6 B 登记） |
| 测试非空洞 | — | `OverflowTests.cs`（8 Fact：22 条代表性文案/非溢出排除/仅 Cerebras/空串 falsy/静默 1099-1101 边界/length 990·1001 边界+output>0/Recoverable 五分支/副本）；`UuidTests.cs`（200 次格式+版本 nibble/显式时间戳编码 `00000000-03e8`·上限 `ffffffff-ffff`/follower 不钳位/200 次字典序严格递增/同毫秒序列区分/`-1`·`MaxTimestamp+1` 抛错文案）；`StringEnumTests.cs`（形状值序/齐备写入/空省略+空白保留）；`SessionResourcesTests.cs`（sessionId 传递/null 缺省/Dispose 注销+幂等/单失败不拦截+聚合文案+InnerExceptions 计数/双失败聚合 2/零注册 no-op）；`DeferredTests.cs:257-305` | 全部非空洞且断言对齐 pi 语义（边界值能区分 `>`/`>=` 与 `>=99%` 浮点差） |
| 登记 spec 注册真实 C# 偏离 | — | `docs/superpowers/specs/2026-11-10-M1-public-utilities-design.md` §3 七行：行1 StringEnum→JsonObject（B）、行2 Uuid 异常类型（A/B）、行3 SessionResources 快照（D）、行4 contextWindow 0 真值 + 空串省略/空白保留（A/B）、行5 lock 并发（D）、行6 IDisposable（B）、行7 IReadOnlyList（B）——任务点名的 5 项全部在案；签名与代码逐一对得上（`Overflow.cs:66/:117/:121`、`StringEnum.cs:16-19`、`Uuid.cs:15/:27`、`SessionResources.cs:7/:20/:37`） | 注册成立 |

### 1.2 U-1 · `Models.CancelDeferredAsync` 缺 ApplyIdentity/ApplyTransformHeaders（Important）→ **核销**

| 核对点 | pi 真相源（本轮亲读 `models.ts`） | 我们 | 结论 |
|---|---|---|---|
| cancel 走 applyAuth 全管线 | `models.ts:978-988` `cancelDeferred` → `this.applyAuth(model, options)`（:986）；applyAuth 统一做 `transformHeaders` 执行+剥离（:874/:886）与 `mergeClientIdentity(this.identity, options?.identity)`（:884，identity===undefined 不写字段） | `Providers/Models.Capabilities.cs:76-79`：`ApplyAuthOptions` → `ApplyIdentity` → `await ApplyTransformHeaders(applied, options?.CancellationToken ?? default)` → `deferred.CancelDeferredAsync(requestModel, handle, applied)`；与同文件 `StreamDeferred` 管线（:37-40）三步同序 | 一致 |
| identity 合并条件 | pi `identity === undefined ? {} : { identity }`（:892） | `Models.Streaming.cs:257-261` `identity is null ? options : options with { Identity = identity }` | 等价 |
| transformHeaders 执行+剥离 | pi 剥离 `transformHeaders` 后才交 provider（:886 解构剔除） | `Models.Streaming.cs:263-276` 重载执行 transform、`options with { Headers, TransformHeaders = null }`；provider 收到已剥离选项 | 一致（首审计指出的"applied.TransformHeaders 未剥除即透传"已消） |
| Provider-visible 测试 | — | `DeferredTests.cs:257-305` `Cancel_Applies_Identity_And_TransformHeaders`：`StaticProvider.cancelDeferred` 委托捕获 `DeferredCancelOptions`，断言 `Identity.Attribution.Name=="test-app"`+`Version=="1.2.3"`（门面级合并）、`TransformHeaders==null`（已剥离）、`Headers["x-test"]=="1"`（已执行）。修复前 identity=null+TransformHeaders 未剥 → 测试必红，回归可探 | 非空洞、provider-visible |

### 1.3 U-2 · PORTING.md 基表失实（Important）→ **核销（文档）**

| 首审计指认 | 整改观察 | 证据（PORTING.md 行号为本轮亲核） | 残留 |
|---|---|---|---|
| §1 openai-completions/anthropic-messages ⬜ | ✅ 改 ✅ 并补 openai-responses/google-generative-ai 两行 | `PORTING.md:44-48`（四适配器 ✅，S4/S4-C/S5-B/S5-C；实文件 `src/PiUnityAgent.Ai/Api/*.cs` 本轮 `ls` 实存） | 无 |
| `google*` 整族 ⛔ 与已实现矛盾 | ⛔ 行改逐名枚举（google-vertex/azure-openai-responses/bedrock-converse-stream/mistral-conversations/pi-messages，与 pi `api/` 目录实名逐一亲核）并注明 google-generative-ai 已实现 | `PORTING.md:54` | 无 |
| auth 行 D1/D7 状态过期 | 刷新为 D1/D7 已完成 + 浏览器 loopback 归 D20 | `PORTING.md:52` | 无 |
| §2 Core 基表四路径不存在 | 基表按实路径改 ✅（`Core/Agent/*` 全实存）+ 表前订正引注 + proxy 行 🟡 D22 口径 | `PORTING.md:60-67`；与 §3B/3C（:109-110 等）不再矛盾 | 无 |
| estimate/abort ⬜ 行永不转绿 | estimate 🟡（三适配器私有副本承载，规划路径注销）；abort ⛔（CT 承载；pi index.ts 亦无 abort 导出——本轮 `grep abort index.ts` 零命中亲核） | `PORTING.md:42-43` | 无 |
| 状态行过期 | §1 状态行改 2026-11-10 现势（含 U-2 订正自述） | `PORTING.md:30` | 无 |

### 1.4 N-1..N-9（Minor）→ **全部核销（登记/订正）**

| 发现 | 整改观察 | 证据 file:line（本轮亲核） | 残留 |
|---|---|---|---|
| N-1 Core 缺窄 StreamFn 别名 | S7 §6 DefaultStreamFn 行代价列补注（pi agent types.ts:33-38 收 SimpleStreamOptions；我方基类委托；下转代价明示） | `2026-11-09-S7-AgentLoop-design.md:441` | 无 |
| N-2 Faux 残条族未登记 | S3 v3 §6 新增"Faux 残条族"行（①无 OnResponse(200) ②GetModel 抛异常 ③无 unregister ④partial 不增长；④ D16 后性质+补齐目标写入代价列）+ §11 版本记录 | `2026-11-08-S3-v3-偏离补齐-design.md:649`、`:770` | ④ 补齐本身是登记为后续工作项（首审计原文即"建议随 faux 补齐"，登记已满足必需动作） |
| N-3 arguments.done 容忍未登记 | S4 §10 行并入半边（pi responses-shared.ts:735 TypeError → 我方 `JsonStr(...) ?? ""` 容忍，行号亲核 :1622 实码相符） | `2026-11-08-S4-OpenAI家族协议适配-design.md:463` | 无 |
| N-4① | S5 §10 `:736→:732` | `2026-11-08-S5-Anthropic与Google协议适配-design.md:243`；实码 `AnthropicMessages.cs:732` `is { Length: > 0 }` 亲核相符 | 无 |
| N-4② | S4 §10 `:870→守卫:864/赋值:869` | `2026-11-08-S4-...-design.md:472`；实码 :864（`maxTokens > 0` 守卫）/:869（`parameters["max_tokens"]`）亲核相符 | 无 |
| N-4③ | S8 §6 U-2 行 `faux.ts:322-336→:327-336`，调用点维持复核 R-1 的 536-541/579/636/648 | `2026-11-09-S8-AgentFacade-design.md:690`；pi faux.ts:327（`function createErrorMessage`）+ :536/:579/:636/:648 亲核相符 | 无 |
| N-4④ | S3 v2 §8 现势句 D1–D15→D1–D23、13 项完成名单与台账逐行一致、D3 不再标 S8 | `2026-11-07-S3-对齐返工-design-v2.md:723`、`:803`（变更记录） | 无 |
| N-5 S8 header 口径 | header 行3 改"U-1/U-8 代码对齐；U-2~U-7、N-1~N-6 补 §6 登记" | `2026-11-09-S8-AgentFacade-design.md:3` | 无 |
| N-6 docs/05 头部/版本表 | 头部 v1.11（2026-11-10）+ §六补 S6/S7/S8/M1 审计四行（SHA `987b3c0`/`a6fc807`/`2caa252`/`955e7cc` 经 `git log` 亲核全实存） | `docs/05-教学阶段路线.md:3`、`:163-166` | 无 |
| N-7 docs/05 格式两小项 | S7 总表行还原 5 列（概念列独立）；§2.1 承接表补 D23 行 | `docs/05-教学阶段路线.md:34`、`:53` | 无 |
| N-8 台账 D2 备注 stale 锚 | `TODO(S5,D1)` 移除，备注改事实口径（两阶段凭据解析跳网络 + D1/S5 完成现势 + 订正标记） | `docs/待办-后置能力.md:12` | 无 |
| N-9 D16 观察项两条 | S7 §6 D16 行代价列补 ①.NET JsonException.Message 文案族注（模板 `JsonParse.cs:130` 逐字一致）②ToObject 每次新建 JsonObject vs pi 快照复用同引用（streaming-json.ts:489-495），归"事件快照"行延伸 | `2026-11-09-S7-AgentLoop-design.md:445` | 无 |

---

## 2. 整改面无新增偏离（代码批 `955e7cc` + docs 批 `32f7430` 复核）

- **代码批逐文件**：11 文件 = 4 新 util + Models 两文件（cancel 两步接线 + DeferredFetchOptions 重载花括号风格化，无行为变化）+ 5 新测试文件。无 wire/形状改动，无既有行为重排；首审计 §1/§2 已核对的其余面（Events/、Core、wire 常量、生态边界）在 `6e9b46a..32f7430` 内零触碰（diff --stat 亲核）。
- **docs 批逐文件**：10 文件全 .md，零生产码/测试改动；登记行所引代码/行号/SHA 经抽核全部相符（见 §1.3/§1.4）。台账 registry 维持 23（U-S1 走补实现、N-2 走 D/known gap 行，均不产生新 Dn——`check-deferred` 复跑一致）。

---

## 3. 新发现（R-1/R-2，均 Minor，不阻塞合并）

- **R-1 · SessionResources 回调执行顺序偏离 pi 且未登记**：pi `session-resources.ts:14` 对 `Set` 活迭代，ECMAScript 规定 Set 迭代为**插入序**；我们 `SessionResources.cs:17` 用 `HashSet<SessionResourceCleanup>`，.NET 官方语义枚举序未定义（删除/重插可变序）。多回调注册且清理顺序有依赖时可观察。新 spec §3 行3 只登记了"活迭代 vs 快照"，顺序差异未登记；测试侧亦明示不契约化顺序（`SessionResourcesTests.cs:18`）。**建议**：spec §3 补一行（pi Set 插入序 / HashSet 未定义序，A/B，代价=多回调清理顺序依赖方需自行排序），或将承载改 `List`（保持插入序、仍可去重判定）。
- **R-2 · `RegisterSessionResourceCleanup(null)` 即抛，pi 聚合到清理期**：pi 接受 undefined 并入 Set，到 `cleanupSessionResources` 才对每个回调调用抛 TypeError→聚合 `AggregateError`；我们 `SessionResources.cs:22-25` 注册时即抛 `ArgumentNullException`（D 类加固）。失败时点/形态不同，未在新 spec §3/fix 报告偏离表登记。**建议**：spec §3 补一行（D，收益=编程错误早暴露）或删守卫回归 pi 口径——两行级动作，随下次文档批顺带。

两条均为"新增公共面的一行级登记缺口"，不构成 Shape gap（形状/主干行为与 pi 一致），不触发 SOP §8 门禁失败；与 S8 复核 R-1..R-3 同级。

---

## 4. 门禁（本审计者 2026-11-10 亲跑于 `32f7430`，工作区干净）

| 门 | 结果 |
|---|---|
| `dotnet build PiUnityAgent.slnx -c Release` | **成功，0 警告 / 0 错误** |
| `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj -c Release` | **失败 0，通过 1157，总计 1157**（基线 1133 + 新增 24，与修复报告/spec 声明一致） |
| `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj -c Release` | **失败 0，通过 96，总计 96** |
| `python scripts/check-deferred.py --root . --current-stage S8 --gate exit` | **OK，exit 0**（code markers D4=2/D5=2/D8=1/D11=1/D15=2/D18–D22 与台账 23 行一致，registry entries: 23） |

---

## 5. 结论

**PASS_WITH_REGISTERED_DEVIATIONS**

| 维度 | 数字 |
|---|---|
| Shape gap | **0**（U-S1 四公共导出已补实现并登记） |
| Critical | **0** |
| Important | **0**（U-1 管线补齐、U-2 基表现势订正均核销） |
| Minor | 首审计 9 条（N-1..N-9）**全部核销**；新发现 **2** 条（R-1/R-2，一行级登记缺口） |

按 SOP §8 门禁逐项：spec 偏离表与代码一致 ✓；审计报告结论非 FAIL ✓；Shape gap 已修（补实现路线，无需用户批准项）✓；未登记偏离已补进 spec ✓（R-1/R-2 为本轮新增的一行级残留，随下次文档批清偿）；C 类台账完整（23 行、exit 0）✓；复核由新的独立审计者完成（本文）✓。**建议**：R-1/R-2 在下次文档提交各补一行登记即可，无需单独批次；本分支可进入合并批准环节（合并门禁=实机/用户同意仍按既有纪律执行）。
