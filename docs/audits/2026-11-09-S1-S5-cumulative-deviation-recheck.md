# S1–S5 偏离修复 复核审计（2026-11-09）

> **审计者**：独立复核审计 subagent（GLM / glm-5.3-flash，Claude Code agent 会话；非实现者、非原审计者、未复用原审计者任何未亲核结论；只读代码/文档/参考源，未改任何代码/index/HEAD/分支，未跑实现者测试之外的命令）
> **参考版本（本轮实核，非凭记忆）**：
> - pi 原版 `v1.5.0` / `f9a263655`（`git log` 实核 HEAD = Release v1.5.0）——行为/形状/wire 唯一真相源
> - pi.net `2d6757b`（`git log` 实核）——仅 C# 表达参考
> - UnityLlmTransport：`third_party/UnityLlmTransport/VERSION`=`0.6.0`、`VENDORED.md` 登记 `4c0cc4b`（本轮未涉及其代码，仅版本核对）
> - Unity 2022.3 + netstandard2.1——仅 A 类硬约束引用
> **范围**：修复 diff `967d60e..1ae87eb`（分支 `feature/audit-fixes`，8 提交；`git status` 实核工作区干净）。审计对象 = 该 diff 中全部代码/台账/spec/审计文档改动，逐条对照原累计审计 §5 必需动作与其引用的 pi/pi.net 源码行。
> **方法**：8 个提交逐 commit 读 diff；每条修复亲自到 pi 原版源码行复核语义与行号（`auth/types.ts`、`models.ts`、`utils/identity.ts`、`anthropic-messages.ts`、`openai-completions.ts`、`openai-responses.ts`、`utils/retry.ts`、`types.ts`、`auth/oauth/*`）；台账 D16–D20 三件套与验收测试锚点逐一 grep 实存；`check-deferred`/Release 构建/两套单测由审计者亲跑复现。
> **结论**：**PASS_WITH_REGISTERED_DEVIATIONS**
>
> 原 §5 全部 6 组必需动作（S-1 / U-1 / G-1 / G-2 / G-3 / Minor 登记）已清偿且与 pi 源码行逐点吻合；U-2 驳回经亲核成立。新发现 0 Critical / 0 Important / 4 Minor——全部是文档级引注瑕疵（1 条事实性现势标注错误 + 3 条引用行号/路径偏差），不阻塞合并，一次 docs 提交可清偿。门禁亲核：Release 构建 0 警告 0 错误；Ai 单测 671 通过（基线 653 + 新增 18：S-1 三例 + U-1 四例 + U-9 一例 + G-3 十例）；Hosts.Net8 3 通过；`check-deferred --gate exit` OK（20 行台账、11 类代码锚零孤儿）。

---

## 1. 逐项核销矩阵（原报告 §5 必需动作）

判定口径：**核销** = 行为/形状与 pi 真相源一致且有代码/测试证据；**核销（文档）** = 纯登记/标注动作已落且引用准确。

### 1.1 S-1 · OAuthCredential 额外字段平铺（原 §3.1 Shape gap，Important）→ **核销**

| 核对点 | pi 真相源 | 我们 | 结论 |
|---|---|---|---|
| 写路径平铺（无嵌套 `extra`） | `auth/types.ts:24-29` `OAuthCredentials { refresh; access; expires; [key: string]: unknown }`——额外字段平铺凭据顶层（亲读实核） | `FileCredentialStore.cs:360-377` ToEntry OAuth 分支：先写 `type/refresh/access/expires` 四保留键，再逐对平铺 `Extra`（`:373` 跳过 `"extra"` 与保留键）；全仓 `grep '\["extra"\]' src/` 零命中 | 一致 |
| 读路径兼容旧嵌套 `extra` | 同上（pi 顶层索引签名天然容纳任意键） | `FileCredentialStore.cs:261-281` ReadExtra：先并旧版 `"extra"` 对象（`:266-272`，非对象抛 ModelsError(Auth)），再收顶层平铺键（`:274-278`，同样过滤） | 兼容成立，`OAuth_Legacy_Nested_Extra_Is_Read_And_Flattened_On_Next_Write`（FileCredentialStoreTests.cs:233-251）直接以 legacy JSON 断言读入 + 写出转平铺 |
| 保留键不可被 Extra 覆盖 | pi `refresh/access/expires` 是接口必需键、`type:"oauth"` 是 OAuthCredential 判别键（types.ts:24-33） | `FileCredentialStore.cs:283-284` `IsOAuthReservedKey = "type"/"refresh"/"access"/"expires"`，读（:276）与写（:373）双向过滤；保留键恒取自类型化属性（ParseEntry :215-219） | 不可覆盖成立，`OAuth_Extra_Cannot_Override_Reserved_Wire_Keys`（:253-280）写恶意 Extra 后断言 wire 四键为真值 |
| 测试是否真验证 wire JSON | — | 三例均 `JsonNode.Parse(File.ReadAllText(_path))` 后断言条目键（FileCredentialStoreTests.cs:210-280），非仅断内存对象 | 是 |

附带核验（回归面）：旧 `Extra` 空集合现在读为 `null`、写为不产生键（`:280` `Count == 0 ? null`）——仅内存表示归一，无 wire 影响；pi 写出的任意平铺键（含 `env`/`key` 等 oauth 非保留名）均可往返（读收进 Extra、写原样平铺）；OAuth 刷新轮换丢 Extra 与 pi 一致（pi `auth/oauth/anthropic.ts:240-273` 同样构造全新三键凭据，不 spread 旧值）。

### 1.2 U-1 · OpenCode 会话头接线（原 §3.2 Important）→ **核销**

| 核对点 | pi 真相源 | 我们 | 结论 |
|---|---|---|---|
| 注入点 | `models.ts:883` `mergeHeaders(mergeHeaders(getOpenCodeSessionHeaders(requestModel, sessionId), auth.headers), options?.headers)`（亲读实核） | `Models.Streaming.cs:91-96`（StreamOptions 重载）/ `:110-114`（SimpleStreamOptions 重载）：`MergeHeaders(sessionHeaders, MergeHeaders(auth.Auth.Headers, options.Headers))` | 嵌套序一致 |
| 优先级 session < auth.headers < request headers | pi `models.ts:384-397` `mergeHeaders(base, override)` override 胜（大小写不敏感去重） | 我们的 `MergeHeaders(provider, request)`（Models.Streaming.cs:162-183）同序（第二参胜）→ 内层 request 胜 auth、外层内层胜 session | 三层优先级成立 |
| requestModel（auth.baseUrl 覆盖后）作目标 | pi `models.ts:880` 先算 `requestModel`（baseUrl 重写）再传 `getOpenCodeSessionHeaders(requestModel, sessionId)` | `Models.Streaming.cs:25-26`（Stream）/ `:51-52`（StreamSimple）：先算 `requestModel` 再传入 `ApplyAuthOptions`，`ApplyAuthOptions` 用 `model.Provider/model.BaseUrl` 构 `ClientIdentityTarget`（:91-92/:110-111） | 一致 |
| 目标判定 | pi `utils/identity.ts:100-104` provider=`opencode`/`opencode-go` 或 host=`opencode.ai`（`matchesHost` :84-87） | `ClientIdentityHelpers.cs:128-131` 同三判 + `MatchesHost`（:117-119，Uri.Host 大小写不敏感 ≈ URL.hostname）；键名 `x-opencode-session` 逐字（:101 vs pi identity.ts:133） | 一致 |
| 空串/缺会话语义 | pi `identity.ts:130-135` `sessionId && isOpenCode(target)`——空串 falsy 不发 | `ClientIdentityHelpers.cs:100` `!string.IsNullOrEmpty(sessionId)` | 等价 |
| stream 与 streamSimple 双覆盖 | pi applyAuth 为 stream/streamSimple 共用 | 两个 `ApplyAuthOptions` 重载都被 Stream（:26）与 StreamSimple（:52）调用 | 双覆盖 |
| 接线证明 | — | `grep GetOpenCodeSessionHeaders src/`：定义 1 + 调用 2（均 Models.Streaming.cs）——原审计"全仓零调用"已消除 | 已接线 |

测试：ModelsOptionsTests.cs:63-141 四例（注入/显式头胜/非 OpenCode 不注入/StreamSimple 注入）；测试 provider 默认 `p1`（ProviderTestSupport.cs:10）保证反例非空转。

### 1.3 G-1 · 台账 D16–D20（原 §3.3，Important）→ **核销**

- 台账五行齐备：`docs/待办-后置能力.md:26-30`（D16 partial 快照+repairJson / D17 retry.ts 模块 / D18 ChatGPT sign-in 字段省略 / D19 目录 promptCache / D20 OAuth 浏览器 loopback），每行 11 列含 ID/参考/形状锚点/验收测试锚点/目标阶段/验收口径/状态/备注；`docs/05-教学阶段路线.md` §2.1 表新增 S7/S8/后续扩展三行（2026-11-09 diff 实核）。
- pi 参考锚逐一实存：D16 `utils/streaming-json.ts`+`utils/json-parse.ts`（ls 实存）；D17 `utils/retry.ts` `RetryPolicy`:134/`retryDelayMs`:146/`retryAssistantCall`:209/`isRetryableAssistantError`:270（grep 实核）；D18 `openai-responses.ts:49-56` isChatGPTSignIn + :362-379 omitUnsupportedFields（亲读实核）；D19 `types.ts:870` `promptCache?: ModelPromptCache`；D20 `auth/oauth/callback-server.ts` 实存。
- 代码锚实存：`ToolCallArguments.cs:13` `TODO(S7, D16)`、`ProviderRetry.cs:9` `TODO(S8, D17)`、`OpenAiResponses.cs:53`+`:455`（D18，修掉原"D1/S5"名实不符）、`ModelCatalog.cs:52` `TODO(后续扩展, D19)`、`TranscriptNormalization.cs:7` `TODO(S9, D11)`（补 G-3 指认的 D11 零锚）、`AnthropicOAuth.cs:82` `Deferred to 后续扩展: … (D20)`。
- 验收测试锚实存：`ToolCallArgumentsTests.ToolCallArguments_Does_Not_Expose_Partial_Json`（ToolCallArgumentsTests.cs:65）、`OpenAiCompletionsTests.Invalid_Tool_Arguments_Json_Falls_To_ArgumentsError_Not_Terminal`（:689）、`ProviderRetryTests.cs`、`ModelShapeTests.Model_Is_A_Chat_ProviderModel_With_New_Auxiliary_Fields`（ModelShapeTests.cs:10）、`OAuthTests` copy-code/manual——全部 grep 命中。
- `python scripts/check-deferred.py --root . --current-stage S5 --gate exit` 审计者亲跑：`registry entries: 20`，`code markers: D11=1, D15=2, D16=1, D17=1, D18=1, D19=1, D20=1, D3=3, D4=2, D5=2, D8=1`，**OK**；四条规则含"锚必须引用台账 ID/锚点必填"，孤儿标记为零（src 内全部 8 处 `Deferred to …`/3 处 `TODO(S…)` 逐一与台账比对无孤儿）。
- copilot×anthropic 头按原 §5 指示并入现有行：S5 spec §10.2 copilot 行尾注"并入 §10.4 范围行，无独立 Dn"，§10.4 实有"Vertex/Bedrock/Azure/Copilot auth 不做（非目标）"行（实核）。

### 1.4 G-2 · spec 偏离表现势修正（原 §3.3，Important）→ **核销（1 处引注事实错误 → 新发现 F-1）**

| 项 | 现状（实核） | 结论 |
|---|---|---|
| S3 v0.1 整份推翻标注 | `2026-11-05-S3-Provider抽象与Faux-design.md:10` ⚠️ 已过期横幅（点名四条被推翻形状并指向 v3 §6）；`:99` `### 3.3 与 pi 的差异（已被 v2/v3 推翻）`、`:109` `### 4.1 Provider（已被 v3 IProvider 接口推翻）`、`:123` `### 4.2 StreamFn（已被 v3 双 delegate 推翻）` | 核销 |
| S3 v2 §8 两行 | `2026-11-07-…-v2.md:723-724`：行723 改"登记 D1–D15（2026-11-09 现势：…）"并保留旧声明语义；行724 "后置 S4/S5（2026-11-09 现势：D6 已完成）" | 核销；但行723 现势列表有事实错误（见新发现 F-1） |
| S3 v3 §6 三行 | `2026-11-08-S3-v3-偏离补齐-design.md:655-657`：三行"我们"列改为已移植/已装配 + 尾注"本行 v3 登记时的'未移植/随 D2/S4'已过期"，旧声明保留为过期文本 | 核销（引用行号 `AuthHelpers.cs:15,56` 为注释行，实际成员在 :17/:60——见附注 5.2，沿自原审计非新引入） |
| S4 §10 行451 | `2026-11-08-S4-…-design.md:453` 行尾追加现势注（D1/S5 已完成、`TODO(S5,D1)` 已移除） | 核销 |
| S4 §10 行452 | `UnityLlmTransportFetch` → `TransportFetch`；类名实核 `TransportFetch.cs:17` | 核销 |
| S4 §10 行464 | `OpenAiResponses.cs:243` → `:216`；实核 `:216` 确有请求级 `$"{attempt.StatusCode}: {errorBody}"`，与该行 pi 锚 `openai-responses.ts:253-256`（顶层 catch 的 `formatProviderError`）语境吻合 | 核销（同文案族另有流中 `:3040` 一处，见附注 5.3） |
| S4 §10 行465 | `OpenAiCompletions.cs:2672` → `:2708`（实核 `:2708` = `Reasoning = reasoningTokens > 0 ? … : null`）；`OpenAiResponses.cs:1463-1464` → `:1499-1500`（实核 :1499-1500 两行连读正是该守卫，"1499 单行形似恒 0"提示句一并落表） | 核销 |

### 1.5 G-3 · 代码锚约定（原 §3.3，Minor）→ **核销**

- `AnthropicOAuth.cs:82` 文案改为 `"Deferred to 后续扩展: browser OAuth loopback callback (D20); use copy_code login."`——符合治理 SOP 的 `Deferred to <阶段>: … (Dn)` 格式；测试同步断言消息含 "D20"（OAuthTests.cs:281-291，断言增强非削弱）。
- `TranscriptNormalization.cs:7` `TODO(S9, D11)`——D11 成为"未开工但有代码锚"。
- S1 §4.4 三枚举 converter 实落：`ImagesTypes.cs:24` + converter `:33-52`；`ClassifierTypes.cs:91` + `:100-119`；`NestedToolCalls.cs:9` + `:18-37`。wire 值逐字对照 pi：`ImagesStopReason = "stop"|"error"|"aborted"`（pi types.ts:668）、`ClassifierStopReason` 同（pi types.ts:731）、`NestedToolCallRecord.status: "ok"|"error"|"unfinished"`（pi types.ts:627）——**全部一致**；未知值 `throw new JsonException(...)`（每 converter Read 尾分支），测试 `EnumWireTests.cs:149-153` 断言 `"nope"` 抛 JsonException。

### 1.6 Minor 登记（原 §3.2 U-3/U-5/U-6/U-7/U-8/U-9/U-10）→ **核销**

| 项 | 登记位置（实核） | 事实抽查 | 结论 |
|---|---|---|---|
| U-3 output_item 缺 item | S4 §10 新行"Responses output_item 缺 `item`"（D/低） | 我方 `OpenAiResponses.cs:1558-1559`（added）/`:1654`（done）确为判空跳过；pi `responses-shared.ts:675-676/746-749` 确为直接用 `event.item` | 核销 |
| U-5 supersede | S4 §10 D2 行尾追加"无 generation/AbortController supersede … U-5 补登记" | `Models.cs:40,46` SetProvider/DeleteProvider 确无作废调用 | 核销 |
| U-6 !signal.aborted | 同上行追加 | `Models.Auth.cs:212-215` 非 OCE 异常无条件收进 errors | 核销 |
| U-7 跨进程互斥 | S5 §10.1 新行（C/低），pi 锚写作 `auth/types.ts:82-84` | pi `auth/types.ts:82` 实为 "…cross-process too where the backing store supports it"——锚准确（原报告误写 credential-store.ts:82-84，已在 1ae87eb 订正） | 核销 |
| U-8 fallback tiers | S5 §10.2 新行（C/低） | pi `anthropic-messages.ts:710-716` fallbackCost 带整个 cost 对象；我们 `AnthropicMessages.cs:500-508` 只取四速率 | 核销 |
| U-9 空串 stop_reason | S5 §10.2 新行"已修：`Length > 0` 守卫（`AnthropicMessages.cs:736`）" | 守卫实码 `:737`（:736 为注释行，差一行）；pi 锚 `anthropic-messages.ts:841` `if (event.delta.stop_reason)` 逐字实核（grep 行号 841 精确）——空串 falsy 跳过、非空照映射，pi :896-897 pending→"Anthropic stream ended without a stop reason" 与我方 :314 逐字一致 | 核销；守卫与 pi truthy 语义一致（null/缺失/空串均跳过；非空白坏值照 pi 走 MapStopReason 抛） |
| U-10 Google 流中 error chunk | S5 §10.3 新行（C/观察，实机探测后定级） | 与原审计"无法本机核验 @google/genai SDK 行为"口径一致，登记为观察合理 | 核销 |

### 1.7 U-2 驳回复核（原 §6-3）→ **驳回成立**（引注有两处小瑕疵，见新发现 F-2/F-3）

| 证据点 | 审计注记声明 | 亲核结果 | 结论 |
|---|---|---|---|
| pi completions | `if (options?.maxTokens)`（注记写作 `openai-completions.ts:826`） | 实际在 **`:827`**（`grep -n` 精确），语义确为 JS 真值判断，0 不发送 | 语义成立，行号差 1 |
| pi responses | `if (options?.maxTokens && …)`（`openai-responses.ts:373`） | 实核 **`:373` 精确命中**，0 falsy 不发送 | 成立 |
| pi.net 旁证 | `Pi.Ai/Api/OpenAiCompletions.cs:1083` 有 `> 0` 守卫 | 实核 `src/Pi.Ai/Api/OpenAiCompletions.cs:1083` = `if (options?.MaxTokens is { } maxTokens && maxTokens > 0)`——行号精确，路径缺 `src/` 前缀 | 成立，路径差前缀 |
| 我方 | `OpenAiCompletions.cs:870` / `OpenAiResponses.cs:543` `> 0` 守卫 | 两行实核均为 `is { } && > 0` | 与 pi 0 值行为同向 |
| 结论 | "U-2 不是偏离，不补登记、不修复" | 三方（pi/pi.net/我们）对显式 0 均不发 | **驳回成立**；负数 maxTokens 的微差见新发现 F-4 |

---

## 2. 新发现

均 Minor（文档级，不涉代码/行为）：

- **F-1（Minor，文档事实错误）**：S3 v2 §8 现势标注把 D4/D5 误列为已完成。
  - 位置：`docs/superpowers/specs/2026-11-07-S3-对齐返工-design-v2.md:723`——"2026-11-09 现势：D1/D2/**D4/D5**/D6/D7/D9/D10/D12/D13/D14 已完成"。
  - 三方证据均为未开工：台账 `docs/待办-后置能力.md` D4/D5 状态列=未开工（awk 实核，20 行状态全表见 §3）；`docs/05-教学阶段路线.md:54` D4/D5 仍在"2B / 后续"未完成行；代码 stub 仍在（`Models.Capabilities.cs:36,43` + `ProviderFactory.cs:97,103` 四处 `Deferred to 2B: … (D4)/(D5)`，check-deferred 计 D4=2/D5=2）。同行的"形状 + stub"自述亦与之矛盾。根因：修复计划 Task 6 Step 2 预置文本即含此错，实现照抄未对照台账；正确已完成集 = D1/D2/D6/D7/D9/D10/D12/D13/D14（9 项，与原累计审计 §4 一致）。
  - 建议：行 723 现势句改为"D1/D2/D6/D7/D9/D10/D12/D13/D14 已完成；D3=S8、D4/D5=2B、D8/D15/D20=后续、D11=S9、D16=S7、D17=S8、D18/D19=后续 未开工"（顺带把"登记 D1–D15"扩为"登记 D1–D20"——D20 属 OAuth 家族，现句未覆盖）。
- **F-2（Minor，引用行号差一）**：审计追加注 §6-3 引用 pi `openai-completions.ts:826`，实际为 **:827**（其余两处 `openai-responses.ts:373`、pi.net `:1083` 行号亲核精确）。驳回结论不受影响。
  - 建议：`docs/audits/2026-11-09-S1-S5-cumulative-deviation-audit.md` §6-3 中 `:826` 改 `:827`。
- **F-3（Minor，引用路径缺前缀）**：同注记中 pi.net 旁证写作 `Pi.Ai/Api/OpenAiCompletions.cs:1083`，实际为 `src/Pi.Ai/Api/OpenAiCompletions.cs:1083`（行号精确）。
  - 建议：补 `src/` 前缀。
- **F-4（Minor，驳回论证覆盖面）**：U-2 驳回只论证了显式 0，未覆盖**负数** maxTokens：pi JS 真值判断对负数仍发送（completions 原样发送负值；responses 侧 `Math.max(-n, OPENAI_RESPONSES_MIN_OUTPUT_TOKENS)` 抬到 16 后发送），我们与 pi.net 的 `is { } && > 0` 对负数一律不发。属病态输入且两家 C# 移植同向，非本次修复引入（守卫先于 967d60e 已存在），但"我们的 `> 0` 守卫与 pi 一致"这句在负值域不严格成立。
  - 建议：在 §6-3 注记补一句限定（"0 值一致；负值 pi 发送、两家 C# 移植按加固不发"）或登记一条 D/低观察，二选一即可。

---

## 3. C 类 Dn 检查

| 项 | 结论 | 证据 |
|---|---|---|
| 台账 D1–D20 行完整性 | **齐** | `docs/待办-后置能力.md:8-30` 20 行；D16–D20 五行（:26-30）逐列实核（ID/参考/形状锚点/验收测试锚点/目标阶段/验收口径/状态/备注） |
| 台账状态分布 | D1/D2/D6/D7/D9/D10/D12/D13/D14=已完成（9）；D3/D4/D5/D8/D11/D15/D16/D17/D18/D19/D20=未开工（11） | awk 提取状态列实核（见 §2 F-1——v2 spec 行与此分布冲突处即其错） |
| 代码锚 ↔ 台账一致性 | **一致，无孤儿** | `check-deferred --gate exit` 亲跑 OK；另手工 grep `TODO(S…)` 3 处 + `Deferred to …` 8 处，逐一有台账行（D11/D16/D17/D18/D19/D20/D3/D4/D5/D15） |
| 新增锚点验收测试实存 | **实存** | §1.3 列表全部 grep 命中（ToolCallArgumentsTests:65、OpenAiCompletionsTests:689、ModelShapeTests:10、ProviderRetryTests.cs、OAuthTests） |
| 新增测试的 wire 断言真实性 | **真实** | S-1 三例直接解析落盘 JSON 断言键；U-1 四例捕获 provider 收到的 `options.Headers`；G-3 十例走真实 STJ 序列化/反序列化（EnumWireTests.cs:122-153） |
| 门禁（审计者亲跑） | **全绿** | `dotnet build PiUnityAgent.slnx -c Release --nologo` 0 警告 0 错误；`dotnet test …Ai.Tests` 671 通过 0 失败；`dotnet test …Hosts.Net8.Tests` 3 通过；`check-deferred --current-stage S5 --gate exit` OK |

---

## 4. 必需动作

- [x] **S-1**：平铺落盘 + 旧嵌套兼容 + 保留键防覆盖 —— 已完成并经测试钉死（核销）。
- [x] **U-1**：`ApplyAuthOptions` 注入 `x-opencode-session`，优先级/目标/双入口对齐 pi models.ts:883 —— 已完成（核销）。
- [x] **G-1**：D16–D20 台账行 + 代码锚 + 验收测试锚 —— 已完成（核销）。
- [x] **G-2**：S3 v0.1/v2/v3、S4 §10 现势修正 —— 已完成；遗留 **F-1**（v2 行723 D4/D5 误标）待一次 docs 提交订正。
- [x] **G-3**：D20 文案格式 / D11 锚 / 三枚举 converter —— 已完成（核销）。
- [x] **Minor U-2~U-10**：U-3/U-5/U-6/U-7/U-8/U-9/U-10 全部登记；U-9 已修（核销）；U-2 驳回成立（核销）。
- [ ] **本轮新发现**：F-1/F-2/F-3/F-4（4 条 Minor，纯文档）——建议合并前以一次 docs 提交一并清偿；均不构成阻塞项，若用户接受也可并入后续 docs 批次。

---

## 5. 附注

**5.1 误报/澄清（对原报告及其追加注的复核性说明，非新偏离）**：
- 原报告 G-2 曾称 S4 §10 行464 应订正为 `OpenAiResponses.cs:3040`；修复改引 `:216`。实核两处均为真实 `{status}: {body}` 站点：`:216` 是请求建立失败路径（`FetchException(Protocol)`，对应 pi `openai-responses.ts:253-256` 顶层 catch 包 SDK 错误的语境），`:3040` 是流中错误体规范化路径（`message = $"{statusCode}: {bodyText}"`）。修复取 `:216` 与该行 pi 锚的请求级语境更吻合，不构成错误；如求完备可在代价列补注"流中同文案族另见 :3040"。
- 原报告 U-9 曾引 `AnthropicMessages.cs:736/:407` 两处与 pi `:841/:224` 两处——实核 pi 仅 `:841` 一处 stop_reason 消费点（:224 在 getAnthropicCompat 内，与 stop reason 无关）；我方修前修后均只有 `:736/:737` 一处（`:407` 为行号漂移误引）。修复覆盖了唯一真实站点，无遗漏。

**5.2 沿自原报告的引用行号近似（非本次修复引入，供下次订正）**：S3 v3 §6 行655-656 引 `AuthHelpers.cs:15,56` / `ClientIdentityHelpers.cs:26-115`——前者实际成员在 `:17/:60`（15/56 是紧邻 doc 注释行），后者覆盖成员群与注释块、可接受。S5 §10.2 空串行引 `AnthropicMessages.cs:736`（守卫实码 :737，:736 为其注释行）。

**5.3 回归检查结论（任务第 8 项）**：
- extra 空集合→null：仅内存表示归一，wire 无影响（§1.1）。
- `requestModel` 重排（先算后传 ApplyAuthOptions）：计算来源不变、provider 收到的 requestModel 值不变，唯一行为增量即 U-1 本身；pi 同序（models.ts:880→883）。
- 三枚举 converter：三枚举当前在 src 无任何序列化站点（grep 实核零引用），wire 变更无现存消费面；对非字符串 JSON token（如数字 `0`）会抛异常（STJ 层）而非旧行为的数字→枚举静默解析——与 pi 纯字符串联合一致，且无现存读入路径，属零风险收紧。
- 既有测试零删除/零削弱：diff 中 tests 只有新增（S-1 三例、U-1 四例、U-9 一例、G-3 十例）与 OAuthTests browser 用例断言增强；`ToolCallArguments_Does_Not_Expose_Partial_Json`（D16 现势钉）原样保留（ToolCallArgumentsTests.cs:65 实核）。
- 计划全局约束抽查：提交序列 8 个（a5148c1→1ae87eb），无 `git add -A` 痕迹（diff 路径逐一与计划 Files 清单吻合）；wire 值 stop/error/aborted、ok/error/unfinished、`x-opencode-session` 逐字实核。

**5.4 无法验证项（沿原审计，本轮不新增）**：
- Google 流中 `{"error":{}}` 在 pi 经 @google/genai SDK 的实际行为仍需实机（U-10 保持 C/观察）。
- pi 上游 `providers/data/*.json` 不在本地 checkout，模型目录代差仍需 D9 对数。
- vendored UnityLlmTransport `4c0cc4b` 仅能核对 VENDORED.md/VERSION 声明（拷贝排除 .git）。

**5.5 版本说明**：pi `v1.5.0/f9a263655`、pi.net `2d6757b` 本轮 `git log` 实核；本仓 `docs/audits/2026-11-09-S1-S5-cumulative-deviation-audit.md` 头部登记一致。

**5.6 范围说明**：本复核对象 = 修复 diff `967d60e..1ae87eb` 及其声明；不重审原累计审计已核销的其余面（SOP §3"必审"四面已由原报告覆盖且本轮无涉改）。测试未由实现者以外的环境重跑以外的验证（门禁由本审计者亲跑复现，数字见 §3）。

---

## 6. 复核附注 · F-1~F-4 清偿复验（2026-11-09，commit `c8d9d09`）

控制器按本报告 §2 新发现派发清偿，commit `c8d9d09`（HEAD，3 文件 +3/-2；`git status` 实核除本报告外工作区干净）。本审计者对四个 F 项逐一复验：

| 项 | 清偿声明 | 亲核证据 | 状态 |
|---|---|---|---|
| **F-1** | v2 行723 现势列表订正 | `docs/superpowers/specs/2026-11-07-S3-对齐返工-design-v2.md:723` 现文本"D1/D2/D6/D7/D9/D10/D12/D13/D14 已完成；D3=S8、D4/D5=2B、D8=后续、D11=S9、D15=后续 未开工"——与台账状态分布（本报告 §3：9 已完成 + D3/D4/D5/D8/D11/D15 未开工）逐项吻合 | **核销** |
| **F-2** | pi 行号 826→827 | `docs/audits/2026-11-09-S1-S5-cumulative-deviation-audit.md` §6-3 现引 `openai-completions.ts:827`；pi 实核 `:827` = `if (options?.maxTokens) {`（本报告 §1.7 grep 实核） | **核销** |
| **F-3** | pi.net 路径补 `src/` 前缀 | 同注记现引 `src/Pi.Ai/Api/OpenAiCompletions.cs:1083`；实核路径与行号均精确（`is { } maxTokens && maxTokens > 0`） | **核销** |
| **F-4** | 负数边界补注 + S4 §10 新行 | 双管齐下：① §6-3 注记补"唯一边界残留=pi 对负数（truthy）会发送、我们 `>0` 跳过…属 D 类加固"（事实正确）；② S4 §10 新增 `maxTokens 负数边界` 行（D/低，2026-11-08-S4-…-design.md:472）。行内 pi 列（`:827` 真值发送负数）、我方列（`OpenAiCompletions.cs:870` `>0` 跳过）、代价列（`ClampMaxTokensToContext` 存在，OpenAiCompletions.cs:3007-3011 `Math.Max(MinMaxTokens,…)` 会抬正）均亲核属实 | **核销主体**，但新行 **pi.net 列"同左"事实反转**（见下方 F-4r） |

**F-4r（残留 1 处，Minor，一行订正）**：S4 §10 新行的 **pi.net 列写作"同左"（= 与 pi 同、发送负数）**，实核不成立——pi.net 两个 OpenAI 适配器均有 `> 0` 守卫、**跳过负数**：`src/Pi.Ai/Api/OpenAiCompletions.cs:1083`（completions）、`src/Pi.Ai/Api/OpenAiResponses.cs:223`（responses，`maxTokens > 0 && supportsMaxOutputTokens && …`）。即负数域 pi.net 与**我们**同向、与 pi 反向——该单元格应为"有 `>0` 守卫，同我们（跳过负数）"。注：此错不改变行的主体判定（我们 vs pi 的 D/低 偏离登记正确），仅 pi.net 参照列失实；与 F-1 同类（登记行内的旁证事实错误）。

**装饰性残留（不需动作）**：F-1 建议中"登记 D1–D15 扩为 D1–D20（D20 属 OAuth 家族）"未被采纳；行 723 现文本字面仍真（该行能力族下登记的确实是 D1–D15，D20 由台账自身跟踪），可接受为现状。

**门禁影响**：c8d9d09 为纯 docs 提交（3 个 .md，无代码/测试改动），无需重跑构建/单测；`check-deferred` 不涉（无新增代码锚/台账行）。

**最终结论（复验后维持）**：**PASS_WITH_REGISTERED_DEVIATIONS**——原 §5 六组必需动作全部核销；F-1/F-2/F-3 核销，F-4 核销主体、残留 F-4r 一处 Minor 单元格订正（一行 docs 改动，不阻塞合并）。

---

## 7. 控制器终态附注（2026-11-09，commit `0cf8cec`）

- F-4r 已清偿：S4 §10 `maxTokens 负数边界` 行 pi.net 列改为“有 `>0` 守卫，同我们”（commit `0cf8cec`）。
- 本复核最终覆盖范围：`967d60e..0cf8cec`（8 个修复提交 + 2 个复核订正提交）；代码面无新增，仅文档订正。
- 全量门禁（控制器实跑，复核范围内代码未再变）：Release 0 警告/0 错误；Ai 671/671；Hosts 3/3；`check-deferred --current-stage S5 --gate exit` OK（20 条，零孤儿）。
- 终态结论维持：**PASS_WITH_REGISTERED_DEVIATIONS**；F-1~F-4/F-4r 全部核销。
