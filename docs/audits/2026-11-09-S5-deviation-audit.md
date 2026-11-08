# S5 偏离审查（2026-11-09）

> **审计者**：独立 subagent（GLM，非实现者、非任务 reviewer；只读代码与参考源）
> **参考版本**：pi `v1.5.0` / `f9a263655`（2026-11-02，本地 `D:/shuoshu/taiwuask/pi-study/pi`，已 `git log` 打钉）——行为/形状唯一真相源；pi.net `main` / `2d6757b`（仅 C# 写法参考）；UnityLlmTransport `4c0cc4b` / `0.6.0`（仅通讯事实，本轮未涉及新通讯事实）；Unity / netstandard2.1（仅 A 类硬约束）。
> **范围**：`4ba5648..c547649`（14 commits，S5 全量：Anthropic/Google 两协议 + 7 profile + D1/D6/D7/D10）。
> **结论**：**PASS_WITH_REGISTERED_DEVIATIONS**
> （spec §10 全部 34 行偏离声明逐项与代码核验一致，无虚报；发现 3 处未登记的低严重度偏离（§3），均非 Shape gap、不阻塞，但需按 §1.3 补入 spec §10；无 Critical。）

---

## 0. 审计输入与门禁实跑

- spec：`docs/superpowers/specs/2026-11-08-S5-Anthropic与Google协议适配-design.md`（§10 为待审计声明，本轮逐行核验，未照抄）。
- plan：`docs/superpowers/plans/2026-11-08-S5-Anthropic与Google协议适配.md`。
- 台账：`docs/待办-后置能力.md`（15 行）。
- 上位规则：`docs/03` §1.3 / §1.5 / §1.6；`docs/SOP/阶段偏离审查-SOP-2026-11-08.md`。
- 控制器已实跑并声明：Release 0/0、Ai 653/653、Hosts 3/3、`check-deferred --current-stage S5 --gate exit` OK、Ai 零 UnityEngine/UnityLlmTransport/GameData/Harmony/HttpClient。审计者只读复核：
  - `check-deferred.py --root . --current-stage S5 --gate exit` 实跑 → `registry entries: 15 … OK`，exit 0（本轮实跑，未改任何文件）。
  - `src/PiUnityAgent.Ai/PiUnityAgent.Ai.csproj`：仅 `System.Text.Json 8.0.5` + `System.Threading.Channels 8.0.0`，netstandard2.1，无传输/游戏引用。grep 命中的 "HttpClient" 全部位于注释/错误文案（`OAuthHttp.cs:12-13`、`AnthropicOAuth.cs:208`、`OpenRouterOAuth.cs:157`），非类型引用。
  - 7 份 `Providers/ModelData/*.json` 与 pi.net `src/Pi.Ai/ModelData/` 逐字节 `cmp` 全部 IDENTICAL。

---

## 1. 公共形状矩阵

| 区域 | pi 原版（v1.5.0） | pi.net | 我们 | 判定 | 类别/证据 |
|---|---|---|---|---|---|
| `AnthropicOptions` | `anthropic-messages.ts:241-301`：thinkingEnabled / thinkingBudgetTokens / effort（`AnthropicEffort` 字符串 union，无 minimal） / thinkingDisplay / interleavedThinking / toolChoice（union）/ **client?（预构建 SDK 客户端）** | 未移植 effort | `Api/AnthropicOptions.cs:21-40`：前六个字段齐全；effort=`ThinkingLevel?`；toolChoice=`JsonNode?`；**无 `client`** | 前五项 faithful；effort/toolChoice 形状宽化已登记（spec §10.2）；`client` 缺失未登记 | 见 §3.1（A 候选，需补登记） |
| `AnthropicThinkingDisplay` | `summarized \| omitted` | — | `AnthropicOptions.cs:9-16` enum Summarized/Omitted，wire 映射 `AnthropicMessages.cs:1499-1505` | 一致 | — |
| `GoogleOptions`/`GoogleThinkingOptions` | `google-generative-ai.ts:47-54`：toolChoice（`auto\|none\|any`）/ thinking{enabled,budgetTokens(-1 动态/0 禁),level} | 有 | `Api/GoogleOptions.cs:9-30`：Enabled/BudgetTokens/Level(ThinkingLevel?)；ToolChoice=JsonNode? | 一致（ToolChoice 宽化是 S3 起的 JsonNode 约定，spec §5 已写明形状） | B/D |
| `AnthropicMessages` 入口 | `Streams/Stream/StreamSimple` | 同 | `AnthropicMessages.cs:97-113`，签名一致 | 一致 | — |
| `GoogleGenerativeAi` 入口 | 同上 | 同 | `GoogleGenerativeAi.cs:57-73` | 一致 | — |
| Compat 面 | `getAnthropicCompat`（anthropic-messages.ts:225-240）10 键 + buildParams 直读 supportsMidConvoEffort/forceAdaptiveThinking/allowedFallbackModels | 有 | `AnthropicMessagesCompat`（AnthropicMessages.cs:21-39）+ `GetCompat`（1365-1415），13 键含默认值逐一对照 | 一致（默认值 true/true/false… 全同） | — |
| Provider 7 家 | `providers/{anthropic,minimax,minimax-cn,google,fireworks,vercel-ai-gateway,openrouter}.ts` | 有 | 6 个 Provider .cs（minimax 双变体合并一文件），id/baseUrl/env/api 逐家核验（§2.4 表） | 一致 | — |
| OAuth 面 | `auth/oauth/{anthropic,openrouter,pkce}.ts` + `auth/types.ts` OAuthAuth | 有 | `Providers/Auth/OAuth/{AnthropicOAuth,OpenRouterOAuth,Pkce,OAuthHttp,OAuthInput}.cs` | faithful（browser 分支后置已登记 C） | — |
| `CredentialStore` 持久化 | pi `auth/types.ts` store 契约（内存语义） | — | `Providers/Auth/FileCredentialStore.cs:22-113`：Read/List/Modify(null=不变)/Delete + per-provider `SemaphoreSlim` | 契约同 InMemory；持久化为 D7 本体 | — |
| `Models.LoginAsync/LogoutAsync` | `models.ts:775-830` + `auth/resolve.ts` | — | `Models.Auth.cs:436-522`（Login→store 写，Logout→store 删，失败包 `ModelsError(Auth)`） | 一致（abort 竞速→顺序流已登记 D） | — |

---

## 2. 行为 / wire / 生态核验

### 2.1 Anthropic 协议（对照 pi `anthropic-messages.ts` 逐段）

| 行为 | pi 锚点 | 我们锚点 | 判定 |
|---|---|---|---|
| URL `POST {baseUrl}/v1/messages` | SDK baseURL+path | `AnthropicMessages.cs:1066-1067` | 一致 |
| 头合并：identity→accept/browser-access→session-affinity→model.headers→options.headers；OAuth 换 `claude-cli/2.1.280`+`x-app: cli`；session affinity 仅非 OAuth 分支 | `createClient` 1008-1107 | `BuildRequest` 975-1068（affinity 在 else 分支 1001-1009，与 pi 的 API-key 分支同构） | 一致 |
| API key→`x-api-key`；`sk-ant-oat`→`Authorization: Bearer` | 1004-1006, 1042-1061, 1097-1104 | `IsOAuthToken` 1081-1082 + 1029-1039 | 一致 |
| `anthropic-version: 2023-06-01` | SDK 恒发 | `ApiVersion` 50 + 1041-1044 | 一致（ ours 额外允许 null 头抑制，见 §3.2） |
| betas：SDK 把 `params.betas` 转 `anthropic-beta` 头；header 配置优先（null 清空）、OAuth 加 claude-code/oauth 两 beta、interleaved/fine-grained/server-side-fallback/mid-convo/inline 按条件 | `getBetaFeatures` 1109-1151 | `GetBetaFeatures` 1417-1497（model.headers 先、options.headers 后，null→空表；六类条件逐一对照）+ `BuildRequest` 1046-1058 出体转头（OnPayload 仍可见 betas） | 一致 |
| cache_control：`PI_CACHE_RETENTION` env 回落 short；none→全消；long+compat→ttl 1h；system 两段（OAuth 时 Claude Code 系统句）、tools 末位（compat 允许时）、最后 user/system 消息末块（五类块型） | `resolveCacheRetention/getCacheControl` 75-99 + `convertMessages` 1526-1552 + buildParams 1220-1268 | `ResolveCacheRetention` 1557-1563 + `GetCacheControl` 1576-1591 + 1199-1261 + 1837-1865（五类块型 text/image/tool_result/tool_addition/tool_removal 同） | 一致（D6 行为验收齐：`Cache_Control_Lands_On_System_Last_Tool_And_Last_Message` / `Cache_Retention_None_Drops_Cache_Control` / `Long_Cache_Retention_Adds_Ttl_1h`） |
| thinking 三形态：disabled / effort-messages（adaptive+block_binding+output_config effort low）/ adaptive+output_config(effort) / budget（enabled+budget_tokens，默认 1024，display 默认 summarized） | buildParams 1270-1304 | 1263-1307（budget `is > 0`→1024 与 pi `|| 1024` 同效） | 一致 |
| 温度：thinking 开/mid-convo/不支持时丢弃 | 1246-1254 | 1236-1243 | 一致 |
| strict 工具：`supportsStrictTools` 才 strict；prefer 降级重发（仅 400+invalid_request_error+三条容量消息）；require 不降级；流开始后绝不重发；诊断 `anthropic_strict_tools_fallback` | stream 循环 649-691 + constrained-sampling | 循环 206-290 + `IsStrictToolLimitRejection` 376-398（解错误体内层 type/message）+ `StrictToolLimitErrorMessages` 362-367（三条文案同） | 一致（测试 `Strict_Tool_400_Downgrade_Resends_Without_Strict` / `Strict_400_Without_Strict_Limit_Message_Is_Terminal_Error` / `Stream_Failure_After_Start_Is_Terminal_Without_Resend`） |
| strict schema 编译（17 个不支持键、anyOf/元组/additionalProperties/required 校验、可空属性 anyOf 补 null）+ Anthropic 关键字集 + minItems/format 特判 | 1586-1658 + makeStrictJsonSchema | `MakeJsonSchemaNodeStrict` 2200-2333 + `IsAnthropicStrictUnsupportedKeyword` 2102-2124 | 一致 |
| SSE：仅六命名事件进状态机；`event:"error"` 抛；ping/未知静默；sawMessageStart&&!sawMessageStop→错；content_block index→统一 contentIndex 槽位（openBlocks）；text/thinking/redacted_thinking/tool_use 四块型 | `iterateAnthropicEvents` 536-575 + stream 695-890 | `AnthropicMessageEvents` 351-359 + `ProcessAnthropicStream` 427-839（index 缺省 -1→delta 忽略；fallback 块前置检查 548-557 与 pi 729-734 同） | 一致 |
| thinking 签名：content_block_start 带 signature；`signature_delta` 只回填块不推流事件 | 803-808 | 677-682 | 一致 |
| tool_use：input_json_delta 累积（ToolCallArguments.Append 只存原文）、content_block_stop 一次 Finish；坏 JSON 落 `ArgumentsError` 不失败流 | 793-808, 828-837 + parseToolCallArguments | 668-675 + 706-723（catch JsonException→ArgumentsError，`ToolCallArguments.cs:27-49`） | 一致（"过程中参数预览停初始值"= pi 的渐进解析差异，已登记 spec §10.2 C） |
| usage：message_start 初值（input/output/cacheRead/cacheWrite/cacheWrite1h/total=四项和）；message_delta 仅更新非 null 字段（代理省字段时保留 message_start 值）；cache_creation.ephemeral_1h、output_tokens_details.thinking_tokens；计价模型 fallback 切换（allowedFallbackModels 命中→usageModel 换 cost） | 704-727, 838-880 | 474-537, 728-824（message_start usage 缺失→零值继续，已登记 C；fallback 三条件 provider+model+cost 同 pi 710-716） | 一致 |
| stop：rawStopReason 先记后映射（映射抛错时 raw 仍在）；end_turn/max_tokens/tool_use/refusal(stop_details.explanation)/pause_turn/stop_sequence/sensitive/未知→抛 | `mapStopReason` 1660-1686 | `MapStopReason` 2374-2388（逐值同文案） | 一致 |
| OpenRouter 实付覆盖：message_delta usage 的 cost/is_byok/cost_details（BYOK 加 upstream_inference_cost，缺则 prompt+completions 之和）；非 BYOK 直接覆盖；非法值不动 | openrouter-cost.ts + 881-888 | `ApplyOpenRouterReportedCost` 3109-3139 + `IsValidCost`/`IsOpenRouterModel` 3095-3100（provider==openrouter 或 baseUrl 含 openrouter.ai） | 一致（与 OpenAiCompletions.cs:2721 各一份私有拷贝=已登记 D/TODO） |
| providerThinkingLevel：effort-messages 模式记到回复快照，下一回合 `insertThinkingLevelMessages` 重放历史 effort（assistant 同 provider+api 才认），active effort 默认 high | 654, 1326, 1489-1496, 1557-1575 | `BuiltParams.ProviderThinkingLevel` 1111-1120 + 216-219 + 1791-1797 + `IsAnthropicEffort` 1897-1898 + `InsertThinkingLevelMessages` 1870-1895 | 一致（commit a5022bb） |
| tool 名 Canonical：OAuth 时声明用 toClaudeCodeName（17 表）、响应用 fromClaudeCodeName 回原 casing | 101-138, 760-762, 1477, 1650 | 55-63 + `ToClaudeCodeName` 1959-1960 + `FromClaudeCodeName` 846-859（OrdinalIgnoreCase 匹配） | 一致（测试 `OAuth_Tool_Name_Is_Mapped_Back_To_Declared_Casing`） |
| tool_result：连续 toolResult 合一个 user 消息；错误结果拆图（caption 文案同 pi #10794）；is_error 恒带 | 1497-1521 + transform-messages splitErrorToolResultImages | 1799-1832 + `SplitErrorToolResultImages` 1968-2002（caption/占位文案逐字同） | 一致 |
| 消息转换：system 折叠/持有（#11091）、user 文本串接或块数组（占位 "(see attached image)"）、assistant 空 signature→text（allowEmptySignature 除外）、redacted 回放、tool_use input | convertContentBlocks/convertMessages 140-194, 1348-1555 | 1599-1957（逐分支对照） | 一致 |
| transform-messages：跨模型 thinking 丢弃、text/ToolCall 复制、thoughtSignature 剥离、toolCallId 归一（`[^a-zA-Z0-9_-]→_`，64 截断）、孤立 tool call 补 "No result provided"、errored/aborted assistant 跳过、system 持有 | transform-messages.ts 全文 | `TransformMessages` 2394-2564 + `NormalizeToolCallId` 2009-2021（与 Google/OpenAI 各一份私有副本=已登记 D/TODO） | 一致 |
| 取消/终态：signal 中止→"Request was aborted"；无 stop→"Anthropic stream ended without a stop reason"；catch→Aborted/Error 终态推事件；retry 仅请求建立前（ProviderRetry 408/409/429/5xx）；超时→FetchOptions 三超时 | stream 892-923 | 307-343 + `BuildFetchOptions` 959-973（测试 `Retryable_Http_Error_Is_Retried_Only_Before_Request_Establishment` / `Cancel_Is_Aborted_Keeps_Partial` / `Timeout_Ms_Maps_To_All_FetchOptions`） | 一致 |
| assertRequestAuth：key 或 authorization/x-api-key/cf-aig-authorization 头任一 | 322-333 | 179-186（`HasHeader` 1075-1079 同三头、值非空白） | 一致 |

### 2.2 Google 协议（对照 pi `google-generative-ai.ts` + `google-shared.ts`）

| 行为 | pi 锚点 | 我们锚点 | 判定 |
|---|---|---|---|
| URL `POST {baseUrl}/models/{id}:streamGenerateContent?alt=sse`；仅 SSE 形态 | SDK streamGenerateContent | `GoogleGenerativeAi.cs:1648-1650`（DefaultBaseUrl v1beta 30；alt=sse 已登记 C） | 一致 |
| apiKey 经 `x-goog-api-key` 头（非 query） | SDK 头 | `BuildRequest` 1639-1640 | 一致（已登记 B） |
| wire 顶层 `contents`/`systemInstruction{parts:[{text}]}`/`tools[{functionDeclarations}]`/`toolConfig{functionCallingConfig{mode}}`；`generationConfig{temperature,maxOutputTokens,thinkingConfig}` | SDK 把 config 展开成 GenerateContentRequest 顶层 | `BuildParams` 505-581（systemInstruction 536-545、tools 547-550、toolConfig 552-558、thinkingConfig 入 generationConfig 561-578） | 一致（已登记 B；pi.net 把 thinkingConfig 放顶层被否——以 pi.ts+REST schema 为准） |
| thinking：enabled+reasoning→includeThoughts + level（大写枚举）或 budgetTokens；disabled→budget 0 或 fallback level | buildParams 398-408 + google-shared getDisabledGoogleThinkingConfig | 560-578 + `GetDisabledGoogleThinkingConfig` 1476-1494（level 型 fallback=Clamp(off) 后再解析，同 pi） | 一致 |
| 流消费：responseId 取首个非空（`||=` 语义）；candidate[0].content.parts；`thought===true` 才是 thinking；thoughtSignature 保留块内首个非空（RetainThoughtSignature）；text/thought 切换推 end/start；块尾收口 | stream 106-270 + google-shared isThinkingPart/retainThoughtSignature | 294-441（`IsThinkingPart` 1554-1555 只认 thought===true；`RetainThoughtSignature` 1558-1559） | 一致 |
| functionCall：先闭合当前块；缺 id/重复 id→`{name}_{ts}_{counter}` 生成；args 缺省 {}；thoughtSignature 挂 part；start+delta(全量 JSON)+end 三连 | 175-220 | 374-403（`Interlocked.Increment` 等价 ++counter，A：线程安全化） | 一致 |
| finishReason：STOP/MAX_TOKENS + 16 个 error 档；未知→抛 Unhandled；有 toolCall 且 STOP→升级 toolUse | google-shared mapStopReason 487-511 + stream 224-230 | `ErrorFinishReasons` 1574-1579（16 档逐一同）+ `MapStopReason` 1582-1589 + 407-415 | 一致 |
| usageMetadata：input=promptTokenCount-cachedContentTokenCount（不夹负）、output=candidates+thoughts、cacheRead=cached、cacheWrite=0、reasoning=thoughts、total=totalTokenCount 原值；算价 | 232-251 | 417-438（注释明确"不夹取下界"= pi 同） | 一致 |
| contents：user（text/inlineData）；assistant 同 provider+model 才留 thinking（thought:true+text），跨模型转纯文本；空 text/thinking 丢（带签名留）；functionCall 带 id 仅当 RequiresToolCallId（claude-/gpt-oss-/gemini≥3）；toolResult→functionResponse（成功 output/错误 error；图片 Gemini3+ 嵌 parts，<3 独立 "Tool result image:" turn）；连续 functionResponse 合并单 user turn | google-shared convertMessages 193-345 | 593-797（逐分支对照，含 `SupportsMultimodalFunctionResponse` 1414-1418、`RequiresToolCallId` 1405-1411） | 一致（spec §10.3 "functionResponse 边缘已按 pi 钉死"成立；commit bfde24b/ad2d0db 测试在） |
| tools：parametersJsonSchema + const→enum 改写（meta 声明剔除、名字映射/子 schema 下钻集合同）；useParameters（Vertex Cloud Code Assist）未移植 | google-shared convertTools 347-381 + rewriteConstAsEnum | `JsonSchemaMetaDeclarations/NameMaps/Subschemas` 1042-1057 + `RewriteConstAsEnum` 1083-1127 + `ConvertTools` 1064-1080 | 一致（useParameters 未移植=已登记 C/D，Vertex 非 S5） |
| toolConfig mode：none/any 直映射；strict（Gemini≥3 schema 编译通过）→VALIDATED；否则仅显式 toolChoice 才带 | resolveGoogleFunctionCallingMode 469-495 | 1356-1381（AUTO/NONE/ANY 映射同） | 一致 |
| thinking 档位：usesGoogleThinkingLevel（gemini-3 pro/flash、gemini-4+、flash-latest、gemma-4 正则）；resolveGoogleThinkingLevel（映射值 ∉ minimal/low/medium/high→抛 Unsupported）；getGoogleBudget（2.5-pro→2.5-flash-lite→2.5-flash 判序，预算表逐值同，其余 -1） | google-shared 36-113 + getGoogleBudget 426-466 | `UsesGoogleThinkingLevel` 1388-1395（三正则同式）+ `ResolveGoogleThinkingLevel` 1441-1452 + `GetGoogleBudget` 1497-1547（判序注释明确 flash-lite 先于 flash） | 一致 |
| XHigh/Max：无 Google wire 值→`ToGoogleSdkThinkingLevel` 抛 Unsupported（直传 GoogleOptions 路径在 RunCoreAsync try 内→终态 Error） | 类型上不可能直传 | 1464-1473（测试钉死） | 一致（已登记 B） |
| streamSimple：无 reasoning 或 clamp 后 off→disabled；level 型→Level；budget 型→GetGoogleBudget | 299-341 | `BuildSimpleOptions` 1781-1848 | 一致 |
| samplingParams：buildBaseOptions 解析进 options、buildParams 不消费（Google 采样参数不落线=pi 行为） | buildParams 364-425 无 sampling | 512-521（只 temperature/maxOutputTokens）+ 1795-1796（解析进 options） | 一致（已登记 B） |
| 取消/终态/重试/超时：abort→"Request was aborted"；无 finishReason→"Google stream ended without a finish reason"；error 档→`Provider stopped with: {raw}`；retry 仅请求建立前；OnPayload 一次、重试不复建 payload | stream 272-293 | 219-253（155-163：payload 构造一次；OnResponse 调用=已登记 B；pi 不调用） | 一致 |
| 自定义 fetch：pi 拒绝非全局 fetch；我们强制要求注入 Fetch | google-generative-ai.ts:87-89 | 142-147（缺→终态 Error） | 已登记 B/D（spec §10.1 "Google 自定义 fetch" 行） |

### 2.3 D1 OAuth / D7 store / D6 / D10

| 项 | pi 锚点 | 我们锚点 | 判定 |
|---|---|---|---|
| Anthropic OAuth 常量 | clientId(atob 解码同一值)/authorize/token/copy-code redirect/scopes/5min slack | `AnthropicOAuth.cs:21-31`（注释保持编码形态） | 逐字一致 |
| 登录方式 select（browser/copy_code）+ 未知方式文案 | anthropic.ts `anthropicOAuth.login` | 60-85（browser→NotSupportedException=已登记 C；未知→`Unknown Anthropic login method:` 同文案） | 一致 |
| copy-code 全链：PKCE→auth URL 八参数（code=true/client_id/response_type/redirect_uri/scope/code_challenge/challenge_method=S256/state=verifier）→auth_url 通知（instructions 文案同）→manual_code（placeholder "code#state"）→state 校验/缺 code→progress→交换六键→expires=now+expires_in*1000-5min | loginAnthropicCopyCode + exchangeAuthorizationCode | `LoginByCopyCodeAsync` 128-161 + `ExchangeAuthorizationCodeAsync` 163-202 + `BuildAuthorizeUrl` 229-246（URLSearchParams 空格→+ 语义） | 一致 |
| refresh：grant_type=refresh_token/client_id/refresh_token；错误文案 `Anthropic token refresh request failed. url=…; details=…`；invalid JSON 文案同 | refreshAnthropicToken | `RefreshAsync` 88-120 | 一致 |
| token 响应校验：TS 无校验（缺键静默 undefined）→我们三键缺一即 `invalid JSON` | TS JSON.parse 直取 | `TryReadTokenResponse` 212-227 | 已登记 C（采 pi.net，更严） |
| OpenRouter OAuth：manual code→永久 key（expires=9007199254740991=JS MAX_SAFE_INTEGER）；refresh no-op；toAuth=access；错误详情 error_description→message→error→error.message 同序；宽容解析（非 2xx 也先读体）；`key` 缺失文案 | openrouter.ts | `OpenRouterOAuth.cs:21-188`（PermanentExpiresAt 27、DefaultRedirectUri 30 固定 53693+可覆盖=已登记 C） | 一致 |
| OAuth HTTP：pi 全局 fetch+30s AbortSignal.timeout→注入 FetchFunction+FetchOptions 三超时；非 2xx 文案 `HTTP request failed. status=…; url=…; body=…` | postJson | `OAuthHttp.cs:26-66, 89-98` | 一致（已登记 B/D） |
| 粘贴解析：URL/`code#state`/查询串/裸 code（Anthropic 带 state）；OpenRouter 只取 code；netstandard2.1 手写解析+WebUtility.UrlDecode | parseAuthorizationInput ×2 | `OAuthInput.cs:17-76` | 一致（netstandard2.1 API=已登记 D） |
| PKCE：32 字节 base64url verifier + SHA-256 S256 challenge（SHA256.Create()+Fill 替代 net8 API） | pkce.ts generatePKCE | `Pkce.cs:17-31` | 一致（已登记 D） |
| 刷新轮换：锁内双检+轮换先持久化；调用方取消只取消锁等待（lockWait CTS+注册，回调第一句摘监听）；在途刷新只受 15s；显式 minValidity 不足→ModelsError(OAuth) | auth/resolve.ts 119-191 | `Models.Auth.cs:258-370`（DefaultOAuthMinimumValidityMs 5min :14、DefaultOAuthRefreshTimeoutMs 15s :17；commit fa5915d） | 一致（pi 同口径；登记 D"行为等价面无竞速结构"） |
| Login/Logout：type 分派（无 OAuth handler→ModelsError(Auth)）；写库失败包 ModelsError(Auth)；未注册 provider→ModelsError(Provider)；logout 静默删 | models.ts:775-830 | `Models.Auth.cs:436-522` | 一致（abort 竞速→顺序流=已登记 D；测试 `LoginAsync_*` 5 例+`LogoutAsync_Deletes_Stored_OAuth_Credential`） |
| D7 FileCredentialStore：version=1 校验、api_key/oauth 两型、坏 JSON/结构→ModelsError(Auth)、原子写（.tmp+File.Replace/Move+失败清理）、per-provider 串行、`~` 展开、不落明文日志 | pi store 契约（内存） | `FileCredentialStore.cs` 全文（Load 140-189 / Persist 276-312） | 一致（测试 12 例全在；"多进程共写不保证"已在台账 D7 备注登记） |
| D6 Anthropic cache：三落点+PI_CACHE_RETENTION+ttl 1h+usage 四字段+1h+2×input 计价 | 上表 | 测试锚 `AnthropicMessagesTests` 3 例+`Usage_Cache_Read_Write_And_One_Hour` | 一致（Google 无 cache_control 字段=pi google 同，无映射即无偏离；usage cacheRead=cached 已映射） |
| D10 可用性：未配置→不可用；stored OAuth→OAuth 型可用零网络；`GetAvailable*` 单次 Check | pi filterModels/getAvailable | `Models.Auth.cs:50-100`（CheckAuthAsync 对 stored OAuthCredential 判 auth.OAuth 存在）+ 测试 `Profiles_Are_Unavailable/Available_*`（7 家）+ `GetAvailable_Stored_OAuth_Credential_Makes_Profile_Available_Without_Network`（commit 0b21200） | 一致 |

### 2.4 7 profile 逐家核对（id / baseUrl / env / api）

| profile | pi（providers/*.ts） | 我们 | 判定 |
|---|---|---|---|
| anthropic | id=anthropic / `https://api.anthropic.com` / stored→`ANTHROPIC_AUTH_TOKEN`(Bearer 头)→`ANTHROPIC_OAUTH_TOKEN`→`ANTHROPIC_API_KEY` / anthropic-messages / OAuth lazy / federation 兜底 | `AnthropicProvider.cs:24-46`（链序同，空串跳过同 pi 真值语义）+ 认证链 51-102；federation→`TODO(后续扩展,D15)`（≈:93） | 一致（federation 后置=D15，见 §4） |
| minimax / minimax-cn | `https://api.minimax.io/anthropic`+MINIMAX_API_KEY；`https://api.minimaxi.com/anthropic`+MINIMAX_CN_API_KEY | `MinimaxProvider.cs:9-35` | 一致 |
| google | `https://generativelanguage.googleapis.com/v1beta`+GEMINI_API_KEY+google api | `GoogleProvider.cs:10-21` | 一致 |
| fireworks | `https://api.fireworks.ai/inference`+FIREWORKS_API_KEY+双协议 | `FireworksProvider.cs:12-29`（ApiByApi anthropic-messages+openai-completions） | 一致 |
| vercel-ai-gateway | `https://ai-gateway.vercel.sh`+AI_GATEWAY_API_KEY+anthropic（typesafe 分类后置 D5） | `VercelAiGatewayProvider.cs:11-24` | 一致 |
| openrouter | `https://openrouter.ai/api/v1`+OPENROUTER_API_KEY+双协议+OAuth（loginLabel 同） | `OpenRouterProvider.cs:22-44` | 一致（目录个性化未实现=已登记 C；images/classifiers=D4/D5） |

`Profiles_Has_Expected_Identity` / `Profile_Catalog_Models_Match_Provider` / `*_ApiByApi_Dispatches_Requests_By_Model_Api` / `Profiles_Without_OAuth_Fetch_Keep_ApiKey_Only` / `Profile_With_Fetch_Attaches_Lazy_OAuth` 测试锚点全部存在且与上述一致。

### 2.5 基类 options 透传（Task 5b 修复，commit 3ecfbde）

`AnthropicMessages.ToAnthropicOptions`（120-149）、`GoogleGenerativeAi.ToGoogleOptions`（80-109）逐字段拷贝全部 `StreamOptions` 基类字段（含 TransformHeaders/Telemetry/Transport/WebsocketConnectTimeoutMs 等 21 项），同型直返。同批把 OpenAiCompletions/OpenAiResponses 的 Run 接线补上（`OpenAiCompletions.cs:145-180`、`OpenAiResponses.cs:99-134`），`Streams_Stream_Accepts_Base_StreamOptions_And_Keeps_Auth` 双侧测试在。与 spec §10.2 "基类 options 透传" 行一致（D：新基类字段需同步转换器清单——代价已写明）。

---

## 3. 未登记偏离 / Shape gap

**无 Shape gap**（未发现"非 A/B/C/D 的形状裁剪或语义改动"）。以下 3 处为**未登记偏离**，均有明确 A/D 归类依据，不构成阻塞，但按 `docs/03` §1.3"偏离必须登记"应补入 spec §10（控制器处置：补行，不需返工）：

1. **`AnthropicOptions.client` 字段未移植** — pi `anthropic-messages.ts:296-300` 公共接口含 `client?: Anthropic`（注入预构建 SDK 客户端，如 AnthropicVertex）。我们 `AnthropicOptions.cs:21-40` 无此成员。
   为什么不是已登记项：spec §10.1 "传输"行只登记了 fetch 缝，未点名这个 options 字段。
   归类：**A 候选**（`Anthropic` 是 @anthropic-ai/sdk 类型，我们的移植没有该 SDK，类型本身无法存在；等价注入缝= `options.Fetch`）。
   建议：在 spec §10.2 补一行「options.client 预构建 SDK 客户端 | Anthropic 注入 | — | 未移植（无 SDK 类型；注入缝=Fetch）| A | Vertex 等 SDK 客户端注入面后置，接 Vertex 时以 Fetch/构造参数重达」。
   file:line：`src/PiUnityAgent.Ai/Api/AnthropicOptions.cs:21-40` vs `D:/shuoshu/taiwuask/pi-study/pi/packages/ai/src/api/anthropic-messages.ts:296-300`。

2. **默认头 null 抑制扩展（IsHeaderSuppressed）** — 我们允许 options.Headers 里显式 null 抑制适配器自设的 `anthropic-version` 与（betas 出头时的）`anthropic-beta`（`AnthropicMessages.cs:1041-1044, 1046-1058, 1070-1073`）。pi 的 `anthropic-beta` 侧本就有 null 清空语义（getBetaFeatures 1122），但 `anthropic-version` 由 SDK 恒发、无抑制面。S4 两 OpenAI 适配器无此机制（base commit `4ba5648` grep=0），是 S5 新增。已由测试 `Headers_Respect_Model_And_Request_Overrides_With_Null_Suppression`（AnthropicMessagesTests.cs:247-271）钉死。
   归类：**D**（主动加固：宿主可关掉版本头以兼容伪装网关；代价=与 pi 头集不完全重合）。
   建议：spec §10.2 补一行登记。

3. **StreamSimple `reasoning=off` 在非自适应 Anthropic 模型上的预算兜底** — pi `streamSimple`（anthropic-messages.ts:987-1001）对 reasoning="off"（真值字符串）不设防：非自适应分支 `thinkingBudgetForLevel("off")` 返回 undefined → maxTokens 变 NaN → wire 上 `max_tokens: null`（退化行为）；仅 Google 侧 pi 有 clamp-off 检查（google-generative-ai.ts:317-320，我们已照抄）。我们 `ThinkingBudgetForLevel`（`AnthropicMessages.cs:2822-2833`）对 Off 落 `_ => 16384` 兜底，且 Anthropic 侧无 off 短路 → 产出 budget 16384 的合法请求。
   归类：**D**（我们主动加固，修复 pi 的退化角落；行为差异仅在 StreamSimple+reasoning=off+非自适应模型的组合）。
   建议：spec §10.2 补一行注记（或按"pi 行为退化"在决策日志记录）。

另有两点**核对后判定为已覆盖、不算未登记**：
- OpenRouter/Vercel 目录只进 chat 模型（image/classifier 条目不进 `ModelCatalog`）：由 §10.4 D4/D5 行覆盖，且 `Providers/_mapping.md` 已显式写明数量（"63 image+33 classifier 不进"）。
- `GoogleOptions.ToolChoice` 用 `JsonNode?` 承接 pi 的字符串字面量 union：spec §5 形状表已写明该形状；运行期仅接受 "auto"/"none"/"any"（`GoogleGenerativeAi.cs:584-587`），行为等价。

---

## 4. C 类 Dn 检查

| Dn | 状态 | 形状锚点 | 验收测试锚点 | 完成提交 | 审计核验 |
|---|---|---|---|---|---|
| D1 | 已完成 | `OAuthAuth`(Login/Refresh/ToAuth)、`Providers/Auth/OAuth/*` 5 文件、`Models.Auth.cs` Login/Logout/ResolveStoredOAuth/RefreshStoredOAuth、工厂 `Create(FetchFunction?)` | `OAuthTests`（33 测试方法 + 15 InlineData ≥44 用例，与台账"44 用例"相符）+ `S5AuthIntegrationTests` 19 例 | `f592503`/`fa5915d` | 全部核实存在且绿（控制器实跑） |
| D6 | 已完成 | `Model.PromptCache`/`StreamOptions.CacheRetention` + Anthropic 映射 | `Cache_Control_*` 3 例 + `Usage_Cache_Read_Write_And_One_Hour` + OpenAI 家族 S4 锚 | `4e3289d`/`95fa6f5` | 核实 |
| D7 | 已完成 | `FileCredentialStore.cs` | `FileCredentialStoreTests` 12 例 | `0f81980` | 核实（原子写/并发/坏结构/version/重启读全有） |
| D10 | 已完成 | `Models.Auth.CheckAuthAsync` OAuth 判定 | `Profiles_*`（7 家 env）+ `GetAvailable_Stored_OAuth_*` | `6464ff9`/`4ff32a0`/`fa5915d` | 核实 |
| D15 | 未开工（目标：后续扩展） | `AnthropicProvider.cs:93` `TODO(后续扩展,D15)`（env 链末尾返回 null） | `S5ProviderProfileTests.Anthropic_Without_Env_Or_Credential_Returns_Null`（钉住现行为） | — | 形状/验收/目标三锚齐，`check-deferred` 计入（D15=2 markers） |

**D15 裁决：有效登记。** 理由：
1. federation（工作负载身份联合）不在 D1 的验收口径内——D1 口径是"能完成登录、刷新、登出；凭据持久化；失败编码为终态 Error"（OAuth 登录面），而 federation 是 pi `anthropic.ts:53-69` 的 api-key resolve **env 兜底分支**（三必需 env + SDK 换 token），既非 login 也非 refresh/logout；
2. S5 spec（§1.1 目标、§1.2 非目标）与 plan 自始未承诺 federation——不存在"把目标阶段为 S5 的 Dn 改走"的重排期事实，治理 SOP §4.1 的"重排期必须用户批准"不触发；
3. 登记 triple（spec §10.2 行、台账 D15 行、代码 TODO 锚 + 行为钉死测试）齐备，`check-deferred --current-stage S5 --gate exit` 绿。
**附注**：D15 目标阶段是"后续扩展"（未排期）。按治理 SOP 新增后置行不需用户批准，但它在功能上等于"pi 的 anthropic 认证链少一档兜底"——合并时用户应知情；若用户认为 federation 属于本次 S5 承诺范围，则升格为"需用户批准的重排期"。

**台账其余 15 行**：状态/目标阶段/锚点与代码标注一致（`grep` 计数 D15=2、D3=3、D4=2、D5=2、D8=1 与行内容吻合）；S5 gate 实跑 exit 0。

---

## 5. spec §10 逐行核验结论（"待审计声明"→核验结果）

- **§10.1（公共/传输 12 行）**：全部与代码一致。逐行证据见 §2.3 与 §3.1；"OAuth fetch 注入缝=AuthHelpers.LazyOAuth+工厂参数"（`AuthHelpers.cs:60-64`、`AnthropicProvider.cs:29-36`、`OpenRouterProvider.cs:27-40`）、"OAuth 超时 30s/刷新锁内 15s"（`OAuthHttp.cs:20`、`Models.Auth.cs:14,17,332-334`）、"expires_in 三键校验"（`TryReadTokenResponse`）、"netstandard2.1 API"（`Pkce.cs:20-26`、`OAuthInput.cs:61-76`）均属实。
- **§10.2（Anthropic 13 行）**：全部一致。copilot 头分支未实现（我们无 github-copilot 分支）、federation→D15、betas 出体转头（OnPayload 仍可见）、Effort 形状 ThinkingLevel?、SseEventReader 复用、严格 JSON 解析、message_start usage 缺失零值继续（`AnthropicMessages.cs:514`）、toolcall delta 只累积（`ToolCallArguments.cs:18`）、OpenRouter 成本 helper 双私有拷贝（`AnthropicMessages.cs:3109`、`OpenAiCompletions.cs:2721`）、StreamSimple 缺 key 终态 Error、ToXxxOptions 逐字段转换器、strict-fallback 重发已实现——逐条核实。
- **§10.3（Google 10 行）**：全部一致。wire 顶层字段/thinkingConfig 入 generationConfig、x-goog-api-key 头、OnResponse 调用、samplingParams 不落线、useParameters 未移植、XHigh/Max 抛 Unsupported、functionResponse 边缘按 pi、仅 alt=sse、EstimateContextTokens 私有副本（`GoogleGenerativeAi.cs:1895-2005` 与 `AnthropicMessages.cs:2851-2961` 双份）、StreamSimple 缺 key 终态 Error——逐条核实。
- **§10.4（范围 7 行）**：全部一致。7 profile、图片/分类不做、OpenRouter 目录个性化未实现（我们无 `personalizeOpenRouterCatalog` 等价物）、tool 名 Canonical 双向照搬、cache_control、Vertex/Bedrock/Azure/Copilot auth 不做。
- **未发现虚报行**：没有发现"声明已实现但代码缺失"或"声明偏离但实际照抄"的情况。

---

## 6. 必需动作

- [ ] spec §10.2 补 3 行登记（§3.1 `options.client` A、§3.2 默认头 null 抑制 D、§3.3 StreamSimple reasoning=off 预算兜底 D）；补后本报告 §3 即清偿。
- [ ] spec 头部/§10 引用的审计报告路径写为 `docs/audits/2026-11-09-S5-deviation-audit.md`（spec 现写 `2026-11-08-…`，与本报告实际落盘名不一致），并把头部"待独立审计"状态更新为本报告结论。
- [ ] D15 在合并请求中向用户明示（"后续扩展、未排期、现行为=null 不伪装可用"），取得知情；如用户要排期则按重排期流程批准。
- [ ] 台账 D1 行"44 用例"建议改为"33 测试方法（含 InlineData ≥44 用例）"以免误读（非阻塞）。

## 7. 审计者附注（误报/无法验证项/版本说明）

- **版本**：pi 本地参考经 `git log -1` 确认 `f9a263655 Release v1.5.0`；pi.net `2d6757b`。与 spec 声明一致，无漂移。
- **测试/构建**：控制器实跑的 Release 0/0、Ai 653/653、Hosts 3/3 未重跑（按任务说明采信）；`check-deferred` gate 由审计者只读实跑复确认 exit 0。审计过程中未修改任何文件、未动 index/HEAD/分支、未 push/merge。
- **采信边界**：审计为静态源码对照 + 测试名/断言抽查（读了大样本测试名与关键断言，如 `Headers_Respect_…`、`Strict_Tool_400_Downgrade_…`、`GetAvailable_Stored_OAuth_…`），未逐条执行 MockFetch 断言；行为结论以源码与 pi 逐段对照为准。
- **spec 报告路径漂移**：spec §10 写 `2026-11-08-S5-deviation-audit.md`，控制器指定落盘 `2026-11-09-S5-deviation-audit.md`，以控制器指定为准（已列入必需动作 2）。
- **误报更正**：初步怀疑"Google streamSimple 对 reasoning=off 与 pi 不一致"——核对后确认我们与 pi 完全一致（两侧都有 clamp-off→disabled）；真正的不一致只在 Anthropic 非自适应分支（§3.3），且方向是我们更稳健。
