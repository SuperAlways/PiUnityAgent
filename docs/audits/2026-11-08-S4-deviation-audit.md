# S4 偏离审查（2026-11-08）

> **审计者**：独立 subagent（glm-5.3-flash / Claude Code agent 会话；非实现者、非上轮 reviewer）
> **参考版本**（本轮实核，非凭记忆）：
> - pi `v1.5.0` / `f9a26365568127461c47a07a37dcb42c140a3ac5`（2026-11-02）— `D:/shuoshu/taiwuask/pi-study/pi`，行为/形状唯一真相源
> - pi.net `main` / `2d6757b3235cf74c7feac2bd20fa8c77fc497b66`（2026-11-05）— `D:/shuoshu/taiwuask/pi-study/pi.net`，仅 C# 写法参考
> - UnityLlmTransport `4c0cc4b` / `VERSION 0.6.0` — `D:/shuoshu/taiwuask/UnityLlmTransport`，仅通讯事实
> - Unity 2022.3 / netstandard2.1 — 仅 A 类硬约束引用
> **范围**：`330c11c..4f659e5`（16 commits，`feature/s4-openai-family` worktree @ `4f659e5`）
> **门禁复核**：`check-deferred.py --current-stage S4 --gate exit` 审计者实跑一次 = `OK`（exit 0；code markers D1=2/D3=3/D4=2/D5=2/D8=1，registry 14 条一致）。构建/单测由 controller 实跑（Release 0/0；Ai 427/427；Hosts.Net8 3/3），审计者未重跑。
> **结论**：**PASS_WITH_REGISTERED_DEVIATIONS**
>
> 无 Critical、无 Shape gap。spec §10 各行回 pi/pi.net 源码核对全部与代码一致；另发现 **10 条 §10 未登记的行为级偏离**（全部为 C/D/B-low 级，公共形状均在，无一属于"删/简化公共形状"），其中 3 条建议必须补登记（见 §5）。合并前把 §5 必需动作处理掉即可维持本结论。

---

## 1. 公共形状矩阵

| 区域 | pi 原版（v1.5.0） | pi.net（2d6757b） | 我们（4f659e5） | 判定 | 类别/缺口 |
|---|---|---|---|---|---|
| `OpenAICompletionsOptions` | `openai-completions.ts:160-165`（toolChoice union / reasoningEffort 6 档 / thinkingBudgets） | JsonNode 表达 | `Api/OpenAiCompletionsOptions.cs:11-18`（ToolChoice=JsonNode?、ReasoningEffort=ThinkingLevel?、ThinkingBudgets?） | 等价 | B（§10 已登记）；ThinkingLevel 6 档 wire 值与 pi `reasoningEffort` union（`openai-completions.ts:162`）一致，无 "off" 越界 |
| `OpenAIResponsesOptions` | `openai-responses.ts:144-149`（reasoningSummary 含 null、serviceTier、toolChoice） | — | `Api/OpenAiResponsesOptions.cs:16-24`（ReasoningEffort?/ReasoningSummary?/ServiceTier string?/ToolChoice JsonNode?） | 等价 | B（§10 已登记） |
| 协议入口 Streams/Stream/StreamSimple | `openai-completions.ts:310-314,718-737`；`openai-responses.ts:154-158,274-292` | 同 | `OpenAiCompletions.cs:127-143`、`OpenAiResponses.cs:81-97`（Streams()/Stream/StreamSimple，ProviderStreams 出口） | 等价 | — |
| 事件形状与顺序 | start→text/thinking/toolcall(start/delta/end)→done/error（`openai-completions.ts:391,469,481,517,602,631,661,692,710`） | record 事件 | `OpenAiCompletions.cs:247,271,283,344,513,536,576,616,641`；Responses 槽位版 `OpenAiResponses.cs:206,1320,1337,1371,1529-1700,232,245` | 等价（reasoning 先于 text：`OpenAiCompletions.cs:494-515` = pi `:582-609`） | — |
| partial 快照 | 共享可变 `output`（事件间活引用） | 不可变 record 快照 | `MutableOutput.Snapshot(blocks)` 每事件新快照（`OpenAiCompletions.cs:3278-3292`） | 表达差异 | B/A；**S2 spec 已登记**（`docs/superpowers/specs/2026-11-05-S2-流式事件协议-design.md:108-115`）+ §10:447，非 S4 新偏离 |
| tool_calls 参数流 | `ToolCallArgumentsStream`（partial 容错解析，`utils/streaming-json.ts:452-500`） | — | 最小端口 `Api/ToolCallArguments.cs`（仅原始文本累积，Finish 一次解析） | 行为收窄 | C（§10:447 已登记）；`ToolCallDelta` 仍带原始文本增量（`OpenAiCompletions.cs:562-577`） |
| 非法工具参数 | `parseToolCallArguments` 永不 throw → argumentsError 落块（`utils/json-parse.ts:106-120`） | 同 | catch JsonException → ArgumentsError + 正常 Done（`OpenAiCompletions.cs:411-421`、`OpenAiResponses.cs:1661-1671`，消息含 `\n\nReceived arguments:\n<text>` 同 pi 格式） | 基本等价 | B（§10:448 已登记）；**但 pi 先跑 repairJson，我们缺**→见 §3 未登记 #2 |
| usage/cost | `parseChunkUsage`（`openai-completions.ts:1508-1555`）+ openrouter cost | — | `OpenAiCompletions.cs:2651-2715`（cacheRead 链 cached_tokens→prompt_cache_hit_tokens→cached_tokens、input=max(0,prompt-read-write)、reasoning_tokens、四项成本、OpenRouter reported cost） | 等价 | —（Reasoning 0→null 见 §3 #5） |
| finish_reason | `mapStopReason`（`openai-completions.ts:1556-1586`：stop/end/length/toolUse/content_filter/network_error/default→error） | 同 | `OpenAiCompletions.cs:2721-2733` 逐分支一致 | 等价 | — |
| Responses 状态机 | `processResponsesStream`（`openai-responses-shared.ts:503-828`：output_index 槽位、summary/text delta、arguments.delta/done replace、terminal 事件、unfinished tool call 拒交付） | — | `OpenAiResponses.cs:1275-1761`（槽位/回填 #6409/`MissingOutputIndex=-1` 哨兵/`response.failed` 分支/`!sawTerminalResponseEvent`/unfinished 拒交付 `:1752-1760`） | 等价 | — |
| Responses stop reason | `mapStopReason`（`openai-responses-shared.ts:861-891`，未知 status 抛错） | — | `OpenAiResponses.cs:1827-1854`（default 抛 InvalidOperationException → 外层 catch 转终态） | 等价 | — |
| service tier 定价 | `applyServiceTierPricing`（`openai-responses.ts:425-453`：flex×0.5、priority/fast×2（gpt-5.5 ×2.5）） | — | `OpenAiResponses.cs:589-614` 同式 | 等价 | — |
| 错误终态/取消 | catch → aborted?aborted:error + formatProviderError（`openai-completions.ts:694-712`；`openai-responses.ts:251-268`） | — | `OpenAiCompletions.cs:619-643`、`OpenAiResponses.cs:235-247`（Aborted 保留 partial，有测试） | 等价 | Responses 缺 provider 前缀 → §3 #4 |
| Fetch null 终态 | pi 无此形态（SDK fetch 恒在） | — | null → throw → 流内终态 Error（`OpenAiCompletions.cs:188-193`、`OpenAiResponses.cs:147-152`；测试 `OpenAiCompletionsTests.cs:386`、`OpenAiResponsesTests.cs:620`） | 新增守卫 | D13 登记（spec §8.4）；合理 |
| OnPayload/OnResponse/OnProviderStreamEvent | `openai-completions.ts:373-376,390,543` | — | `OpenAiCompletions.cs:179-186,240-245,441-444`；Responses `:138-145,199-204,1501-1504`；StreamSimple 透传（`OpenAiCompletions.cs:2922-2924`） | 等价 | — |
| retry 只在请求建立阶段 | `retryProviderRequest` 包住 `client.chat.completions.create`（`openai-completions.ts:382-389`） | — | `ProviderRetry.RunAsync` 只包 fetch+非 2xx 判定（`OpenAiCompletions.cs:200-237`、`OpenAiResponses.cs:159-196`），SSE 循环在其外 | 等价 | 非重试语义收窄见 §3 #1 |
| D14 auth helpers | `auth/helpers.ts`（envApiKeyAuth :9-31、lazyOAuth :46-57） | Auth/Helpers.cs | `Providers/Auth/AuthHelpers.cs:17-54,56-90`（stored 胜→按序 env；懒加载一次含并发；isSubscription/loginLabel 透传） | 等价 | — |
| D14 identity | `utils/identity.ts`（UA/attribution/OpenCode session） | — | `Types/ClientIdentityHelpers.cs:41-115`（OpenRouter 三头、NVIDIA 首字母大写、opencode-client、opencode-session 全齐） | 等价 | GetClientVersion 版本源差异 → §3 #3 |
| 8 family profile（12 变体） | `providers/{openai,xai,deepseek,moonshotai,moonshotai-cn,zai,zai-coding-cn,qwen-token-plan*,xiaomi,groq}.ts` | 同名 | `Providers/*Provider.cs`：12 变体 id/name/baseUrl/env/api **逐项与 pi 一致**（openai→responses、zai=`api.z.ai/api/coding/paas/v4`、qwen individual 与国际同 baseUrl 同 env 仅目录不同） | 等价 | 未含变体见 §3 后"已登记 scope 裁剪" |
| ModelCatalog | `model-catalog.ts`（flatten 按 type 过滤；provider 来自 JSON 内嵌） | ModelCatalog.cs | `Providers/ModelCatalog.cs:21-132`（`chat:` 前缀 + type!=chat 双保险、provider 字段优先于目录 key=pi 语义、缺资源空表） | 等价（比 pi 运行时更严） | 数据 12 份 JSON 与 pi.net ModelData **逐字节相同**（cmp 验证）；缺口见 §3 #6/#7 |
| 兼容矩阵 | `detectCompat/getCompat`（`openai-completions.ts:1587-1727`） | — | `OpenAiCompletions.cs:2739-2884`（逐字段一致：isNonStandard/useMaxTokens/thinkingFormat 7 形/supportsLongCacheRetention 等） | 等价 | — |
| Responses compat | `getCompat`（`openai-responses.ts:91-107`） | — | `OpenAiResponses.cs:383-412`（11 字段缺省+覆盖一致，含 supportsMidConvoEffort） | 等价 | — |
| Hosts.Net8 适配器 | SDK fetch | — | `TransportFetch.cs`（TransportException→FetchException 12 kind 映射 `:77-97`；IsRetryable 透传 `:96`）；Ai 层 grep `UnityEngine|UnityLlmTransport|GameData|Harmony|HttpClient` = 零命中；Ai csproj 仅 System.Text.Json/Channels | 等价 | link.xml 未 vendored = §10 已登记（S13 补） |
| vendored pin | — | — | `third_party/UnityLlmTransport` 93 个 tracked 文件与源仓 `4c0cc4b` archive 内容一致（diff 除 VENDORED.md/link.xml/Phase*Harness 外零差异；VERSION=0.6.0） | 一致 | link.xml 差异已登记 |

> 8 family 内**没有新增协议/scope**：只有 `openai-completions`/`openai-responses` 两套 wire（`CreateProviderOptions.Api/ApiByApi` 分派，`Providers/CreateProvider.cs:10-27` 与 pi `models.ts:1032-1067` 形状 1:1）。

---

## 2. 行为 / wire / 生态

- **事件顺序**：completions 严格 reasoning→text→toolcall（`OpenAiCompletions.cs:494-577` = pi `openai-completions.ts:582-667`）；Responses 按 output_item/output_index 槽位（`OpenAiResponses.cs:1507-1743` = pi `openai-responses-shared.ts:671-814`）。Start 在 OnResponse 之后（pi 同：`openai-completions.ts:390-391`）。
- **流开始后不重试**：`ProviderRetry.RunAsync` 只包请求建立（含非 2xx 读体后抛 `FetchException` 以复刻 SDK 的 408/409/429/5xx 重试，`OpenAiCompletions.cs:204-228`，测试 `Http_429_Is_Retried_Inside_ProviderRetry`/`Http_400_Is_Not_Retried` 双协议各一）；SSE 读取失败直接进 catch → 终态。
- **取消**：`signal.IsCancellationRequested` → Aborted 终态并保留 partial（`OpenAiCompletions.cs:587-595,633-642`；测试 `Cancel_Becomes_Aborted_And_Keeps_Partial` 断言 partial 内容与文案 "Request was aborted"）。取消检查在行间（netstandard2.1 无 `ReadLineAsync(ct)`，A 类，`SseEventReader.cs:35` 已注释；真实链路靠传输层 IdleTimeout 兜底）。
- **wire**：`[DONE]` 吞掉（`OpenAiCompletions.cs:434`、`OpenAiResponses.cs:1494`，= SDK 语义）；`prompt_cache_key/retention`、`stream_options.include_usage`、`store=false`、`max_tokens vs max_completion_tokens`、session affinity 三头、copilot 动态头、identity 头次序（identity→model.headers→copilot→SDK 默认→options.Headers 可覆盖/null 抑制，`BuildRequest:675-737`）均与 pi `createClient`/`buildParams` 逐项对应。
- **D6 cache**：`resolveCacheRetention`（env `PI_CACHE_RETENTION`，`OpenAiCompletions.cs:3161-3167`）+ `GetCompatCacheControl` anthropic cache_control（`:1176-1257`）+ Responses `prompt_cache_options`（`OpenAiResponses.cs:438-448`）全接；usage cacheRead/cacheWrite 落 `Usage`。
- **D2 两阶段**：恢复（AllowNetwork=false，先于凭据解析与网络）→ 解析有效凭据 → 联网拉取（`Models.Auth.cs:154-214` = pi `models.ts:565-675` 顺序化版）；credential 读失败不挡恢复（`:171-191`）；publication 先 store 删/写再点亮 Update（`:280-297`）；过期 OAuth 跳网络 + `TODO(S5,D1)`（`:260-262`）。顺序化/无 generation 去抖/原引用落库 = §10:450 已登记。
- **生态**：Ai 仍 `netstandard2.1`（csproj 实核）；Hosts.Net8 `net8.0`；vendored 六包自带 `netstandard2.1/LangVersion 9.0`；Unity 运行时 `unity/*/Runtime` 仅源码参考不进 dotnet 构建；`Directory.Build.props` 不改 vendored 包 TFM（VENDORED.md 落地说明）。

---

## 3. 未登记偏离 / Shape gap

**Shape gap：无。** 以下均为行为/健壮性级偏离，公共形状（类型/字段/成员）全部存在。按对齐规则 §1.3 "偏离必须登记"，标 ⭐ 的三条建议在合并前补进 spec §10（补行后本审计结论不变）。

1. ⭐ **[D] ProviderRetry 无状态码错误的重试面收窄** — pi `utils/provider-retry.ts:30`：`error.status === undefined → return true`（任何无状态码错误都重试，含 TLS/DNS——SDK 侧它们都是 APIConnectionError）。我们 `Api/ProviderRetry.cs:55-56`：无状态码仅在 `Kind == FetchErrorKind.Connect` 或传输层 `IsRetryable` 标记时重试；TLS/代理/协议类无状态错误不重试。commit `86fc621` 有意为之（尊重传输层事实，避免对永久性配置错误空转），`ProviderRetryTests.cs:59-92` 已把该语义钉成契约。代价：pi 会重试的 TLS 故障我们不重试（少一次自愈机会）。**属 D 类主动加固，但 §10 表无 ProviderRetry 行，需补登记。**
2. ⭐ **[C] `ToolCallArguments.Finish` 缺 repairJson 前置修复** — pi `utils/json-parse.ts:33-120`：`parseToolCallArguments` → `parseJsonWithRepair`，先原样 parse，失败后 `repairJson`（字符串内裸控制字符转义、非法 `\x` 双反斜杠）再试一次，**可修复的畸形参数解析成功**。我们 `Api/ToolCallArguments.cs:33-46` 直接 `JsonNode.Parse`，裸换行/非法转义的 arguments 落 ArgumentsError。§10:448 行只登记了"抛 JsonException → ArgumentsError"通道，未覆盖 repair 差异——实际行为比登记的更窄。代价：罕见畸形参数我们会把可救的调用判失败。建议：补 §10 登记为 C（端口 repairJson 归后续），或直接补 repair 端口。
3. ⭐ **[A/B] `GetClientVersion` 用程序集版本替代 `PI_AI_VERSION` 常量** — pi `utils/identity.ts:62-64` + `utils/version.ts:2`（`PI_AI_VERSION="1.5.0"`）；我们 `Types/ClientIdentityHelpers.cs:26-34`（identity.Version 优先 → 程序集 Major.Minor.Build）。C# 无该常量、程序集版本是 C# 惯用来源，A 类表达可接受，但 §10 无行；User-Agent 形状因此随构建版本漂移（pi 是包版本），需补登记。
4. **[B/低] Responses 错误终态缺 `${provider} API error` 前缀** — pi `openai-responses.ts:253-256` 给 `formatProviderError` 传 prefix（"OpenAI API error (400): …"）；我们 `OpenAiResponses.cs:243` 无前缀（"400: …"）。纯文案差异；completions 侧 pi 本就不带前缀（`openai-completions.ts:701`），我们两侧统一无前缀。建议登记或对齐。
5. **[D/低] `Usage.Reasoning` 0 → null** — pi `parseChunkUsage`/`finalizeResponse` 恒产数字（`reasoning: … || 0`，`openai-completions.ts:1545`、`openai-responses-shared.ts:644`）；我们 `OpenAiCompletions.cs:2672`、`OpenAiResponses.cs:1463-1464` 在 0 时产 null（"未上报"语义）。S3 `Usage.Reasoning` 本是可空字段，序列化输出因此少一个 `reasoning:0` 键。建议登记为 D。
6. **[B/低] ModelCatalog 缺资源静默返回空表** — pi.net `Models/ModelCatalog.cs:175-183` 缺资源抛 `FileNotFoundException`；我们 `ModelCatalog.cs:27` 返回空列表（且测试把"unknown provider → empty"钉成契约 `S4ProviderProfileTests`）。拼错 providerId 得到空目录，排障更隐蔽。pi TS 无运行时加载（编译期数据），严格说无 pi 行为可对齐；建议 §10 该行补注"缺资源=空表"。
7. **[C/低] ModelCatalog 不解析 `promptCache` 键** — `Model.PromptCache` 形状在（S3/D6），但 `ModelCatalog.ParseChatModel`（`ModelCatalog.cs:50-132`）不填充；当前 12 份 JSON 无此键，无实际丢失。登记为 C，接入含 promptCache 的目录数据时补。
8. **[B/低] `x-should-retry` 头值比较大小写不敏感** — pi `provider-retry.ts:26-28` 精确匹配 `"true"/"false"`；我们 `ProviderRetry.cs:51-52` `OrdinalIgnoreCase`。`"True"` 我们会识别、pi 落到 status 判定。影响极小；建议改 Ordinal 对齐或登记。
9. **[B/低] retry-after 解析宽容度** — pi 用 `Number.parseFloat`（`"250ms"`→250，`provider-retry.ts:56`）；我们 `double.TryParse(NumberStyles.Float)` 对尾随垃圾直接失败落指数退避（`ProviderRetry.cs:79-89`）；无时区 HTTP 日期 pi 按本地时区、我们 `AssumeUniversal`（`:95-99`，与 pi.net 相同）。带 GMT 的常见形态一致。建议登记一笔带过。
10. **[C/低] `ClientIdentity` 的 attribution 关闭哨兵不可 JSON 往返** — pi 声明 identity 是 "Plain JSON, 可持久化"（`identity.ts:27-28`，`attribution: null` 显式关闭归属并参与 merge）；我们 `Types/ClientIdentity.cs` 用私有 `AttributionSet` 哨兵表达"显式 null"，System.Text.Json 往返无法回填该哨兵。S4 不持久化 identity，仅记风险；后续做凭据/identity 持久化（D7）时需补。

**已登记 scope 裁剪（复核确认已在 spec §3/§10 或 Dn 台账登记，不计未登记）**：

- xiaomi token-plan 三变体（`xiaomi-token-plan-ams/-cn/-sgp`，pi `providers/xiaomi-token-plan-*.ts:8-13`）未实现 — spec §3"小米（base + token-plan 变体后续）"登记，首批 8 family 收敛内。
- openai/xai 的 OAuth 订阅登录面（pi `openai.ts:16-21`、`xai.ts:14-19`）— D1/S5 台账 + §10。
- openai 的 classifiers/classify/filterAllModels 面（pi `openai.ts:23-33`，45 条含 `classifier:gpt-6-luna`）— D5 台账（2B）。
- provider 级 deferred 分派（pi `models.ts:1164-1188` 动态挂 fetchDeferred/cancelDeferred）— D3 台账（S8）。
- 其余 31 家 provider 与 OpenRouter/Azure/Copilot 等特殊 profile — §10 范围收敛行。
- `OpenAiShared.cs` 未提取（两适配器内联重复约 1300 行私有辅助，实测两文件各自带 `GetClientApiKey/ResolveCacheRetention/FormatProviderError/ShortHash` 等副本）— §10"D/TODO 收编债务"行登记，且"不得再造第三份拷贝"红线成立（现仅两份）。

---

## 4. C 类 Dn 检查

`docs/待办-后置能力.md`（2026-11-08 更新）S4 目标 7 条全部 `已完成`，逐条实核形状锚点与验收测试锚点**均存在**：

| Dn | 台账状态/完成提交 | 形状锚点实核 | 验收测试锚点实核 |
|---|---|---|---|
| D2 | 已完成 `24e4d80` | `Models.Auth.cs:154`（RefreshAsync 两阶段）、`ProviderFactory.cs:35,111`（RefreshableCreatedProvider）、`Auth/ModelsStore.cs:26,64-65`（Persist/Delete） | `S4RefreshTests` 11 例（`S4RefreshTests.cs:105-343`：恢复/拉取+持久化+覆盖/失败保留旧表/Providers 过滤/取消/Delete/无凭据跳网络/ApiKey 合成）全在 |
| D6 | 已完成 `4e3289d` | `Model.PromptCache`/`StreamOptions.CacheRetention`（S3）+ 双协议 cache 映射（§2） | `OpenAiCompletionsTests.cs:246`（Usage_And_Cost）、`OpenAiResponsesTests.cs:351`（Usage_And_ServiceTier）、`:398`/`OpenAiResponsesTests.cs:453`（prompt_cache_key/retention 落请求 + User-Agent） |
| D9 | 已完成 `6464ff9` | `ModelCatalog.cs:21` + 12 份嵌入 JSON（csproj EmbeddedResource） | `S4ProviderProfileTests`（Profile_Catalog_Models_Match_Provider_Identity / GetChatModels_Parses_DeepSeek_Model_Fields / _Cost_Tiers / _OpenAI_Returns_Only_Chat_Entries / _Unknown_Provider_Returns_Empty） |
| D10 | 已完成 `6464ff9` | `IFilterableProvider`/`Models.GetAvailable*`（S3） | `S4ProviderProfileTests`（Profiles_Are_Unavailable_Without_Env_Or_Credential / _Available_When_Env_Keys_Configured）+ `ModelsAuthTests.CheckAuth_And_GetAvailable_Filter_Configured_Providers` |
| D12 | 已完成 `7612e05`/`0b8f536` | `Models.Auth.ResolveAuthAsync` CT 贯穿 + `GetAvailable*` 每 provider 单次 Check | `S4DeferredCapabilityTests.cs:38,65`（Check 计数=1 + CT 传递 + Resolve 未被调用） |
| D13 | 已完成 `7612e05`/`0b8f536` | `FetchOptions.ConnectTimeout/FirstByteTimeout/IdleTimeout`（S3）+ `BuildFetchOptions`（`OpenAiCompletions.cs:650-664`、`OpenAiResponses.cs:254-268`） | `S4DeferredCapabilityTests.cs:126-174`（TimeoutMs 三值同值映射 / first-byte、idle 超时→终态 Error+CallCount=1）+ `Missing_Fetch_Becomes_Terminal_Error` 双协议 + `TransportFetchTests.Fetch_Maps_TransportException_To_FetchException` |
| D14 | 已完成 `92168ab` | `AuthHelpers.cs:17-90`、`ClientIdentityHelpers.cs`（含 OpenRouter/NVIDIA/OpenCode/OpenCode-session 全套） | `AuthHelpersTests`（stored 胜/env 顺序/懒加载一次含并发）、`ClientIdentityHelpersTests`、identity 头注入请求=双协议 `Request_Shape_Hooks_And_Headers`（`OpenAiCompletionsTests.cs:487` null 抑制 User-Agent 断言） |

- 代码标注约定合规：唯一 S4 后置锚 = `Models.Auth.cs:260` `TODO(S5,D1)`（OAuth 轮换），与台账 D1 行呼应；`check-deferred --current-stage S4 --gate exit` = OK（审计者实跑）。
- D2 台账"验收口径"含"Persist/Delete 二选一在发布路径验证（审计 N7）"——`Refresh_Publish_Delete_Removes_Store_Entry`（`S4RefreshTests.cs:271`）+ `Refresh_Fetches_Persists_And_Overlays_Baseline`（`:154`）两路都在，核销。
- D14 备注"withClientIdentity Models 包装器未移植（StreamOptions.Identity 已有等价合并）"——`StreamOptions.Identity` 存在（`Types/StreamOptions.cs:28`），identity 头在协议适配器请求构造点生效，与登记一致。

---

## 5. 必需动作

- [ ] **补 spec §10 三行**（⭐ 项，合并前）：
  1. ProviderRetry 无状态码错误仅 Connect/传输层 IsRetryable 可重试（pi 一律重试）— D，证据 `ProviderRetry.cs:55-56` vs `provider-retry.ts:30`，测试 `ProviderRetryTests.cs:59-92`；
  2. `ToolCallArguments.Finish` 无 repairJson 前置修复（pi 可修复畸形参数解析成功）— C，证据 `ToolCallArguments.cs:33-46` vs `json-parse.ts:33-120`；同时把 §10:448 行的"Task 4 旧注释语义以本表为准"扩到 repair 差异；
  3. `GetClientVersion` 用程序集版本替代 `PI_AI_VERSION` — A/B，证据 `ClientIdentityHelpers.cs:26-34` vs `identity.ts:62-64`。
- [ ] 建议一并补登（可合并为一条"B/C 低风险杂项"行）：Responses 错误文案缺 provider 前缀；`Usage.Reasoning` 0→null；ModelCatalog 缺资源=空表与 promptCache 不解析；`x-should-retry`/retry-after 解析宽容度；`ClientIdentity` attribution 哨兵不可 JSON 往返（D7 前置）。
- [ ] 可选加固（不阻塞）：`ToolCallArguments` 补 repairJson 端口；`ProviderRetry` 头值比较改 Ordinal；ModelCatalog 缺资源抛错或告警。
- [ ] 上述登记完成后，本审计结论即可作为合并材料（Shape gap=0、Critical=0、Dn 台账完整、门禁 OK）；按 SOP §5.1，登记由 controller 完成，不需重派审计（本次未发现需修复代码才能关闭的项）。

---

## 6. 审计者附注

1. **误报驳回（1 条）**：子审查曾报"缺 `ToolCallArgumentsStream.replace()`"——不成立。`OpenAiResponses.cs:1788-1807 ReplaceArguments` 实现了与 pi `streaming-json.ts:472-486` 相同的语义（`text` 接续已累积文本→补差量；否则重置重累积、delta=null），function_call 的 `output_item.done` 全量覆盖路径（`OpenAiResponses.cs:1651-1660`）与 pi `:774-777` 对齐。仅"partial 参数对象"差异已由 §10:447 覆盖。
2. **FormatUserAgent Detailed 恒 `pi/<v> (browser)`**（`ClientIdentityHelpers.cs:41-47`）：与 pi 在非 Node/Bun 环境的行为**一致**（`identity.ts:75-82` 的 fallback 分支即输出 `(browser)`），不算偏离；真正差异只在版本源（§3 #3）。
3. **SseEventReader event-only 事件丢弃**（`SseEventReader.cs:42`）：pi TS 内联解码器（`anthropic-messages.ts:406-475`）会产 event-only 事件，我们按 pi.net `AiSse` 语义丢弃；OpenAI 家族流无 event-only 事件，无实际影响。建议在 §10:441 SSE 行补一句"按 pi.net 语义"。
4. **pi 模型数据不可直读**：pi 仓 `providers/data/*.json` 被 gitignore（`openai.models.ts:4` import 不可解），本轮以 pi.net ModelData 快照逐字节比对 + pi `catalog/providers/deepseek.ts` 逐字段抽查为旁证；12 份 JSON 与 pi.net 完全一致，deepseek 条目与 pi 生成定义逐字段吻合。
5. **审计 diff 包缺失**：任务输入所写 `.superpowers/sdd/s4-audit-330c11c..4f659e5.diff` 不存在（该目录只有 7 份 task report）；审计改从 worktree @ `4f659e5` 直读源文件，覆盖率不受影响（两协议适配器 3441/3230 行重点段全读，SSE/tool args/retry/auth/identity/profile/catalog/D2/D12/D13 逐项核对）。
6. **vendored 目录里有 bin/obj 构建产物**（`third_party/UnityLlmTransport/**/bin|obj`）：仅为本地构建残留、未被 git 跟踪（`git ls-files` = 93 个文件均为源码/文档）；建议 controller 确认 `.gitignore` 覆盖，避免未来误提交。
7. 本轮未重跑构建/单测（controller 已实跑 Release 0/0、Ai 427/427、Hosts 3/3）；除 `check-deferred` 外未执行任何写操作，未改 index/HEAD/分支。
