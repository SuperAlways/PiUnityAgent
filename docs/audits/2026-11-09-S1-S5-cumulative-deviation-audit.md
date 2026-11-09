# S1–S5 累计偏离审查（2026-11-09）

> **审计者**：独立审计 subagent（GLM / glm-5.3-flash，Claude Code agent 会话；非实现者、非既有 5 份报告的审计者；只读代码与参考源，未改任何代码/index/HEAD/分支）
> **参考版本（本轮实核，非凭记忆）**：
> - pi 原版 `v1.5.0` / `f9a263655`（`git log` 实核 HEAD = Release v1.5.0，2026-11-02）——行为/形状/wire 唯一真相源
> - pi.net `main` / `2d6757b`（`git log` 实核）——仅 C# 表达参考
> - UnityLlmTransport：vendored 于 `third_party/UnityLlmTransport`，`VENDORED.md`/`VERSION` 登记 `4c0cc4b` / `0.6.0`（in-tree 拷贝，commit 本体无法在仓内独立复核，见 §6）
> - Unity 2022.3 + netstandard2.1——仅 A 类硬约束引用
> **范围**：全仓 root `57e10b7..0c6bbd3`（121 commits）；审计对象 = 当前 main（`0c6bbd3`）的代码现状，非增量 diff。代码面 = `src/PiUnityAgent.Ai/`（Types/Events/Providers/Api 全量）+ `src/PiUnityAgent.Hosts.Net8/`（Fetch 适配器）+ 测试锚点存在性。
> **方法**：S1/S2/S3-形状/Retry/ModelOperations/Faux/工厂/Auth 面由审计者亲读；S4/S5 两个 3000+ 行适配器与 7 份 spec 偏离表由 4 个只读采集 subagent 逐行对比后，**全部 Critical/Important 级声明由审计者亲自到源码行复核**（其中 1 条被驳回，见 §6）。
> **结论**：**PASS_WITH_REGISTERED_DEVIATIONS**
>
> 无 Critical。3 条 Important（1 条形状收窄、1 条行为未接线、1 组登记治理缺口）+ 约 20 条 Minor/观察。公共形状骨架（消息模型、事件协议、Provider/Auth/Models 门面、4 协议适配器 wire、Fetch 端口、TFM/依赖方向）逐项与 pi 对齐良好；发现的问题集中在**跨工具持久化互操作、一个未接线的 helper、以及"spec §10 已登记但台账无 Dn 行"的治理缝隙**。合并前/下一阶段开工前需完成 §5 必需动作。

---

## 1. 公共形状矩阵

判定口径按 SOP §4：A=语言/生态强制；B=借 pi.net 的 C# 表达且不改 pi 语义；C=行为后置但形状保留（须 Dn）；D=主动加固须明示；Shape gap=删/简化公共形状或改语义。

### 1.1 S1 消息与内容模型（pi `types.ts` / `providers/model-schema.ts` ↔ `src/PiUnityAgent.Ai/Types/`）

| 区域 | pi 原版 | 我们 | 判定 |
|---|---|---|---|
| 消息四型 + `timestamp` | types.ts:561-659（SystemMessage/UserMessage/AssistantMessage/ToolResultMessage，timestamp 必填） | `Types/Message.cs:4-10`（抽象 record + `required long Timestamp`）+ 四派生（SystemMessage.cs:4、UserMessage.cs:4、AssistantMessage.cs:3、ToolResultMessage.cs:3），Role wire 值 `system/user/assistant/toolResult` 逐字一致 | 等价（B：abstract record 表达 union） |
| 内容块多态 | types.ts:422-457（text/thinking/image/toolCall 五字段族） | `Types/ContentBlock.cs:5-14` `[JsonPolymorphic("type")]` + 4 个 `[JsonDerivedType]`；`TextContent.cs`（Text/TextSignature）、`ThinkingContent.cs`（Thinking/Signature/Redacted）、`ImageContent.cs`（Data/MimeType）、`ToolCall.cs`（Id/Name/Arguments/ThoughtSignature/Namespace/ArgumentsError）字段一一对应 | 等价（A，S1 spec D2 登记） |
| `string \| blocks` 联合 | types.ts:564/581（content: string 或块数组，按角色收紧） | `Types/MessageContent.cs:8-24` Text/Blocks 包装 + converter；角色收紧由 `Types/MessageValidation.cs:9-31` 运行时校验（system 仅 TextContent、user 仅 text/image、assistant 仅 text/thinking/toolCall、toolResult 仅 text/image） | 等价（B/D：编译期联合→运行时校验，语义保全） |
| Usage / cost | types.ts:459-487（input/output/cacheRead/cacheWrite/cacheWrite1h?/reasoning?/totalTokens/cost{5+1 字段}） | `Types/Usage.cs:4-39` 逐字段对应；cost.total 必有、TotalFromProvider 可空一致 | 等价（token 计数 `int` vs `number`：A） |
| StopReason | types.ts:489（7 值） | `Types/StopReason.cs:9-48` 枚举 + wire 映射 pending/stop/length/toolUse/error/aborted/deferred 逐字一致 | 等价 |
| ThinkingLevel 族 | model-schema.ts:18-19（minimal..max、off 前置） | `Types/ThinkingLevel.cs:8,11` 两枚举 + converter，wire 值逐字一致 | 等价 |
| Tool/ToolReference/ConstrainedSampling | types.ts:746-780 | `Types/Tool.cs:6-23`、`Types/ConstrainedSampling.cs:3-17`（Disabled/Config 判别替代 `false \| config`） | 等价（A） |
| Context/TranscriptContext | types.ts:788-805（brand 隔离） | `Types/Context.cs:4-20`；brand 不可表达 → `TranscriptNormalization.NormalizeContext`（TranscriptNormalization.cs:11-37，折叠 systemPrompt+tools 为前导 system，原序不变） | 等价（A；折叠行为与 pi `normalizeContext` 一致） |
| DeferredHandle/NestedToolCalls/AssistantMessageDiagnostic | types.ts:539-638、utils/diagnostics.ts | `Types/DeferredHandle.cs`、`Types/NestedToolCalls.cs`、`Types/AssistantMessageDiagnostic.cs` 逐字段对应 | 等价 |
| Model / ProviderModel 三型 | types.ts:842-917（BaseModel/Model/ImageModel/ClassifierModel/ModelTypeMap） | `Types/Model.cs:30-79`：三 sealed record + 封闭 `ModelType` 枚举；chat 字段族（Reasoning/ContextWindow/MaxTokens/ThinkingLevelMap/SamplingParams/SamplingParamsByThinkingLevel/Compat/PromptCache）全在（Model.cs:49-61） | 等价（`Compat` 为 JsonObject 透传 vs pi 按 API 分型：A/B，pi.net 同思路；后果见 §3-3） |
| Options（ProviderRequestOptions+StreamOptions） | types.ts:160-270（23 字段） | `Types/StreamOptions.cs:10-43` 逐字段对应（含 onPayload/onResponse/onProviderStreamEvent/telemetry/env/transport/cacheRetention/websocketConnectTimeoutMs/metadata）；另加 `TransformHeaders`（D，S3v3 §6 登记） | 等价 + D 登记项 |
| SimpleStreamOptions / Deferred | types.ts:377-394（toolChoice/reasoning/deferred/thinkingBudgets） | `Types/SimpleStreamOptions.cs:3-9` 四字段 + `StreamOptionTypes.cs:32-41`（DeferredRequest Disabled/Enabled(Window)） | 等价（Window 为 string vs pi `"15m"\|"1h"\|"24h"` 字面量：轻微放松，Minor） |
| Images/Classifier 类型 | types.ts:661-743 | `Types/ImagesTypes.cs`、`Types/ClassifierTypes.cs` 逐字段对应（含三 stopReason 值） | 等价 |
| ClientIdentity 族 | utils/identity.ts:11-59 | `Types/ClientIdentity.cs:6-61`（UserAgentMode 三态/Attribution/Version + AttributionSet 哨兵 + ClientIdentityMerger 逐字段合并，null-关闭语义保留） | 等价（A：`attribution: null` 用哨兵表达） |
| **pi 公共 util 导出** | index.ts:12-47（lazy、auth/*、models、models-store、faux、types、diagnostics、event-stream、identity、json-parse、overflow、retry、streaming-json、string-enum、text、transcript、uuid、validation、version） | 见 §3-2/§3-3：`text.ts` 三函数、`json-parse.ts`、`streaming-json.ts`、`retry.ts`、`uuid.ts`、`overflow.ts`、`string-enum.ts` 无独立公共等价物（text/transcript 逻辑以私有副本存在于 4 个适配器；retry.ts 整块缺席且已登记） | **部分缺口（详见 §3）** |

### 1.2 S2 流式事件协议（pi `types.ts:823-839` + `utils/event-stream.ts` ↔ `Events/`）

| 区域 | pi 原版 | 我们 | 判定 |
|---|---|---|---|
| 事件 12 变体 | types.ts:823-839（start/text_*/thinking_*/toolcall_*/done/error，字段名 contentIndex/delta/content/toolCall/partial/reason/message/error） | `Events/AssistantMessageEvent.cs:14-72` 12 个嵌套 record，`Type` wire 名逐字一致；done 值域 {stop,length,toolUse,deferred}、error 值域 {error,aborted} 由 `Events/AssistantMessageEventValidation.cs:10-24` 运行时强制（pi 为 TS 类型） | 等价（A：判别 record + 校验器） |
| EventStream 容器 | utils/event-stream.ts:26-89（FIFO+等待队列、push 终态即封、end(result?)、单消费者） | `Events/EventStream.cs:13-134`：Channel unbounded（SingleReader）、Push 终态封流、End/WaitForResultAsync；`Fail(exception)` 为 pi 没有的基础设施收场口（D，S2 spec §5.1 登记） | 等价 + D 登记项（`End` 参数不可省 vs pi `end(result?)`：B，pi 自身调用点恒传值） |
| AssistantMessageEventStream | utils/event-stream.ts:97-130（终态写 durationMs：已有值不覆盖、timestamp 早于流起点不覆盖、单调钟、Math.max(0,round)） | `Events/AssistantMessageEventStream.cs:10-100`：三规则逐条对应（:87-99），`Stopwatch` 差值/Frequency 替代 `performance.now()`（A）；额外 `Partial` 属性（D，S2 spec §5.2 登记） | 等价 + D 登记项 |
| LazyStream / LazyStreamObserver | api/lazy.ts:60-86（setup 失败→push error→onEvent→end→observeResult，各一次；forwardStream push→onEvent、end→observeResult） | `Events/LazyStream.cs:17-75`：顺序逐条对应（catch 内 push→onEvent→end→onResult :68-73），错误消息形状对齐 pi createSetupErrorMessage（api/lazy.ts:4-23） | 等价（B：Task.Run 替代 `setup().then`，A） |

### 1.3 S3 Provider 抽象 / Auth / Models / Faux（pi `models.ts` / `auth/*` / `models-store.ts` / `providers/faux.ts` ↔ `Providers/`）

| 区域 | pi 原版 | 我们 | 判定 |
|---|---|---|---|
| Provider 接口 | models.ts:158-247（id/name/baseUrl?/headers?/auth/getModels/getAllModels?/refreshModels?/filterModels?/filterAllModels?/stream/streamSimple/fetchDeferred?/cancelDeferred?/generateImages?/classify?） | `Providers/IProvider.cs:9-29`（必选成员全齐）+ `Providers/ProviderCapabilities.cs:10-60` 五个能力接口表达 9 个 optional 成员；不用 DIM（登记） | 等价（A/B；语义等价于 pi 的 `!== undefined` 探测） |
| getModels 不抛契约 | models.ts:174-180 + ModelsImpl try/catch | `Providers/Models.cs:102-112` SafeChatModels/SafeAllModels try/catch→空表 | 等价 |
| Models 门面 24 成员 | models.ts:258-367 | `Models.cs:40-113` + `Models.Auth.cs` + `Models.Streaming.cs:14-64` + `Models.Capabilities.cs:10-43`：getProviders/getProvider/getModels/getModel/getModelsOfType/getModelOfType/getAllModels/refresh/checkAuth/getAvailable/getAvailableOfType/getAllAvailable/getAuth/login/logout/stream/complete/streamSimple/completeSimple/streamDeferred/fetchDeferred/cancelDeferred/generateImages/classify **零缺失零多余**（Async 后缀/参数序为 A/B 表达） | 等价 |
| MutableModels + CreateModelsOptions | models.ts:369-382 | `Models.cs:12-54`（SetProvider/DeleteProvider/ClearProviders + 四字段 options，缺省 InMemory） | 等价（DeleteProvider 返回 bool、Identity getter 为无害增益） |
| applyAuth 管线 | models.ts:856-901（getAuth→apiKey 覆盖（空串显式胜）→baseUrl 重写→openCodeSession 头→headers 三层合并→transformHeaders→env→identity） | `Models.Streaming.cs:14-64, 88-151`：apiKey 空串语义（:96）、baseUrl 重写（:28/:54）、headers 合并（大小写不敏感去重 :153-174）、transformHeaders 进 provider 前执行并摘除（:129-151）、identity 逐字段合并（:116-126）全部对应；**唯独 openCodeSession 头未接线**（见 §3-2） | 等价 − 1 缺口 |
| 未配置错误文案 | models.ts:873 `Provider is not configured: X` | `Models.Streaming.cs:83-84` 逐字一致 | 等价 |
| Auth 类型面 | auth/types.ts:1-259（ModelAuth/ApiKeyCredential/OAuthCredential/Credential/CredentialInfo/AuthOperationOptions/AuthContext/AuthResult/AuthCheck/AuthType/AuthPrompt×4/AuthEvent×4/AuthInteraction/ProviderAuthInteraction/ApiKeyAuth/OAuthAuth/LoginOptions/ProviderAuth） | `Auth/AuthTypes.cs`、`Auth/Credential.cs`、`Auth/ProviderAuth.cs`、`Auth/AuthInteraction.cs`、`Auth/AuthContext.cs` 全套逐字段对应；wire 值 `api_key`/`oauth` 一致（AuthTypes.cs:47-64） | 等价（A：signal→CT、判别 record） |
| CredentialStore 四法 | auth/types.ts:65-94（modify=唯一写路径、null=不变、返回写后值、list 不执行 key 命令） | `Auth/Credential.cs:27-36`（ReadAsync/ListAsync/ModifyAsync/DeleteAsync）+ InMemory（:57-125，per-provider SemaphoreSlim、modify null=不变返回 current）+ `Auth/FileCredentialStore.cs`（原子写/per-provider 锁/version 校验，D 加固） | 等价 + D（跨进程互斥未实现未登记，Minor） |
| AuthContext 缺省 | auth/context.ts:23-45（env 空白=absent、`~` 展开、浏览器恒 false） | `Auth/AuthContext.cs:14-34`（IsNullOrWhiteSpace→null、`~`→UserProfile、同语义） | 等价 |
| envApiKeyAuth / lazyOAuth | auth/helpers.ts:9-59（source="stored credential"/env 名；prompt `Enter {name}`；懒加载一次） | `Auth/AuthHelpers.cs:15-89` 逐字对应（含文案与 `??=` 单次加载） | 等价（D14，已完成） |
| Auth 解析链 | auth/resolve.ts:33-100（显式 apiKey→stored 全权拥有 provider、类型无匹配不回落 ambient→ambient；overlay env 空串回落 base） | `Models.Auth.cs:524-563` 同序同分支 + `:645-662` OverlayAuthContext | 等价 |
| OAuth PKCE/anthropic/openrouter | auth/oauth/pkce.ts、anthropic.ts、openrouter.ts（32B verifier/SHA256、client_id/authorize/token/callback URL、8+6 参数键、grant_type、30s 超时、MAX_SAFE_INTEGER expires、no-op refresh、copy-code/manual 流） | `Auth/OAuth/Pkce.cs`、`AnthropicOAuth.cs`、`OpenRouterOAuth.cs`、`OAuthHttp.cs`、`OAuthInput.cs`：wire 常量与参数键逐字符串核对全部一致（含 `claude-code-20250219`/`oauth-2025-04-20`、53692 anthropic copy-code callback）；browser 分支后置（NotSupportedException，登记）；OpenRouter redirect 自造固定端口 `localhost:53693`（S5 §10.1 登记） | 等价 + 登记 C/D |
| OAuth 登录/刷新/登出 + FileCredentialStore | — | `Models.Auth.cs:310-370,436-522`（锁内刷新、轮换写回、15s 超时、ModelsError(OAuth/Auth) 包装）+ `FileCredentialStore.cs`；D1/D7 已完成并有 44+11 用例（`tests/.../OAuthTests.cs` 33、`S5AuthIntegrationTests.cs` 16、`FileCredentialStoreTests.cs` 11） | 等价（C→已完成） |
| ModelsStore / ModelsPublication | models-store.ts + models.ts:106-130 | `Auth/ModelsStore.cs:9-67`（四字段 + Persist/Delete/Update 三态）+ `Models.Auth.cs:160-245` 两阶段刷新（恢复→凭据→网络，失败保留旧表） | 等价（刷新顺序化已登记 D2；supersede 残条见 §3-6） |
| createProvider / CreateProviderOptions | models.ts:1032-1162（至少一个实现校验、api map 分派、动态覆盖合并、refreshModels 包装） | `Providers/CreateProvider.cs:10-27` + `ProviderFactory.cs:40-222`（校验数非 null 实现 :52-58、MissingApi 终态 :114-117、Overlay 保序就地替换 :196-212、RefreshModelsAsync 恢复→双闸→滤型→Persist+Update :157-194） | 等价 |
| Faux | providers/faux.ts:732 行（默认值/工厂辅助/事件序/usage 估算/分块/deferred 计数/unregister） | `Faux/Faux.cs` + `Faux/FauxProvider.cs`：默认值/事件序/空文本空块/错误文案逐条对齐；确定性 id/固定分块为登记 D；残余偏差族见 §3-7 | 等价 + 登记项 + Minor 残条 |
| ModelCatalog | models.generated.ts + providers/data/*.json | `Providers/ModelCatalog.cs` + `Providers/ModelData/*.json` 19 份（与 pi.net ModelData 抽样逐字段一致）；字段面齐（H1），**promptCache 不解析**（S4 §10 行467 已登记，见 §3-4）、仅装 chat 型（登记，D4/D5 需补） | 等价 + 登记 C |
| 14 家 provider 工厂 | providers/{anthropic,openai,google,deepseek,openrouter,xai,moonshotai(+cn),minimax(+cn),fireworks,vercel-ai-gateway,groq,zai(+coding-cn),qwen-token-plan(+cn/individual),xiaomi}.ts | 同名 14 个 `XxxProvider.cs`：id/name/baseUrl/env 变量名**逐字符串全部一致**（ANTHROPIC_AUTH_TOKEN/ANTHROPIC_OAUTH_TOKEN/ANTHROPIC_API_KEY、OPENAI_API_KEY、GEMINI_API_KEY、DEEPSEEK_API_KEY、OPENROUTER_API_KEY、XAI_API_KEY、MOONSHOT_API_KEY、MINIMAX_API_KEY/MINIMAX_CN_API_KEY、FIREWORKS_API_KEY、AI_GATEWAY_API_KEY、GROQ_API_KEY、ZAI_API_KEY/ZAI_CODING_CN_API_KEY、QWEN_TOKEN_PLAN*_API_KEY、XIAOMI_API_KEY） | 等价（openai/xai OAuth 订阅面、openrouter 个性化、xiaomi 变体、anthropic federation 均登记后置 C/D） |

### 1.4 S4 OpenAI 家族（pi `api/openai-completions.ts` / `openai-responses.ts` / `openai-responses-shared.ts` ↔ `Api/OpenAi*.cs`）

| 区域 | 判定 | 说明（双侧证据摘录，全表见采集记录） |
|---|---|---|
| 请求体 wire | 等价 | `model/messages/stream/prompt_cache_key/prompt_cache_retention/stream_options.include_usage/store:false/max_tokens→max_completion_tokens（compat.maxTokensField）/temperature/tools/tool_choice/priority/samplingParams 末位合并`键名逐字一致（pi completions.ts:807-992 ↔ OpenAiCompletions.cs:836-1091）；responses `input/reasoning.effort+summary/include/service_tier/max_output_tokens(≥16)/prompt_cache_options(ttl 30m)` 一致（pi responses.ts:363-414 ↔ OpenAiResponses.cs:516-604） |
| thinkingFormat 十一分支 | 等价 | pi completions.ts:865-962 ↔ OpenAiCompletions.cs:922-1050（zai/qwen/chat_template/baseten/deepseek/openrouter/…逐分支） |
| compat 探测与缺省矩阵 | 等价 | pi completions.ts:1587-1727 ↔ OpenAiCompletions.cs:2775-2920（嗅探串/缺省值/`supportsStrictMode=false` 全同）；responses 11 字段缺省同（OpenAiResponses.cs:419-453） |
| SSE 解析 | 等价（B） | pi 委托 OpenAI SDK；我们 `Api/Sse/SseEventReader.cs:21-69` 自写行协议 + 适配器滤 `[DONE]`（语义等价，pi.net 同路线） |
| SSE→事件映射 | 等价 | chunk 级顺序（usage 先于 choices、reasoning 三字段序、responseId/responseModel 回退）；responses 全事件分支（`response.output_*` 16 类）与槽位四类 item、`${call_id}|${item.id}` 管道 id、textSignature V1 编码、output_item.done 四分支——逐一对齐（pi responses-shared.ts:520-814 ↔ OpenAiResponses.cs:1341-1777） |
| usage/cost | 等价 | cacheRead 三家回退链（cached_tokens/prompt_cache_hit_tokens/cached_tokens）、cache_write_tokens、reasoning_tokens、OpenRouter 实付（cost/is_byok/upstream_inference_cost）、service tier 定价乘数（flex 0.5/priority 2/gpt-5.5 2.5）逐式一致 |
| stop reason 映射 | 等价 | completions 五值+未知→error 文案；responses status 全表+未知 throw；无 finish_reason 的 supportsFinishReason 分支同 |
| 错误/取消/重试 | 等价 + D | 4xx/5xx 错误体格式化与 4000 字截断、CT→aborted 归一 "Request was aborted"、流中不重试、重试仅包请求建立——全对齐；无状态码仅 Connect 重试为 D（S4 §10 行461 登记）；无 fetch → 终态 Error（pi 隐式回落全局 fetch：B 方案等价强化） |
| headers | 等价 | identity→model.headers→copilot 动态头→session affinity→options.headers 叠放序、null 抑制、copilot X-Initiator/Openai-Intent 三键、session affinity 三格式（openrouter/openai/openai-nosession）、cacheRetention=none 全消——逐一对齐 |
| streamSimple | 等价（B/D） | buildBaseOptions 逐字段透传（含观察钩子）、clampMaxTokensToContext/estimate（CHARS_PER_TOKEN=3.5、IMAGE=4800、usage 锚定+尾随）、clampReasoning/预算表/MinAnswerTokens=1024 逐条一致；pi 的同步抛鉴权契约（types.ts:392-395 "may throw"）我们恒流内编码——在 "may" 许可内（S4 §10 行449 登记） |

### 1.5 S5 Anthropic / Google（pi `api/anthropic-messages.ts` / `google-generative-ai.ts` / `google-shared.ts` ↔ `Api/AnthropicMessages.cs` / `GoogleGenerativeAi.cs`）

| 区域 | 判定 | 说明 |
|---|---|---|
| Anthropic 请求 wire | 等价 | `anthropic-version: 2023-06-01`（双侧 :50 常量一致）、beta 六常量+OAuth 两 beta、`sk-ant-oat`→Bearer 否则 `x-api-key`、认证断言三头、system blocks（OAuth Claude Code 块前置）、tools（strict/input_schema/eager_input_streaming/deferred placeholder `__pi_deferred_placeholder__`）、tool_choice、thinking 四形态（disabled/adaptive+budget/adaptive+effort/enabled+`budget_tokens||1024`）、metadata.user_id、fallbacks、`claude-cli/2.1.280`+`x-app: cli`——逐字符串一致 |
| cache_control 三落点 + retention | 等价 | system 末块/tools 末个/最后一条 user|system 消息末块（白名单五类）；PI_CACHE_RETENTION 显式>env>short、none 全消（含 sessionId）、long→`ttl:"1h"`——与 pi anthropic-messages.ts:75-99/1527-1655 逐点一致 |
| Anthropic SSE | 等价 | 六事件白名单+ping 静默、message_start usage 初值（cacheWrite1h 双落点）、redacted_thinking、signature_delta 静默回填、message_delta 非空字段保留、流完整性守卫（started&&!ended/pending/aborted→throw）——逐一对齐 |
| Anthropic stop reason / usage | 等价 | 7 值映射+未知 throw；usage cache_creation 拆分、thinking_tokens→reasoning；**cost 核对：1h 部分 2×input（pi models.ts:1247 注释），我们 ModelOperations.cs:46-51 逐式一致** |
| Google 请求 wire | 等价 | `{baseUrl}/models/{id}:streamGenerateContent?alt=sse`、`x-goog-api-key` 头（pi TS 同、pi.net 用 query key 属其历史分歧，我们以 pi 为准）、顶层 systemInstruction、contents role user|model、五类 parts（含 thought/thoughtSignature/functionResponse 合并/图片独立 turn）、generationConfig 仅 temperature/maxOutputTokens、thinkingConfig（includeThoughts/thinkingLevel/thinkingBudget，-1/0 语义、getGoogleBudget 三族档位表）——逐键一致 |
| Google 流解析 | 等价（1 处 Minor） | 多 candidate 取首、responseId 首非空、functionCall 聚合+`name_ts_counter` 合成 id、finishReason+toolUse 升级、usageMetadata（input 不夹下界/output 含 thoughts/cacheWrite=0）——逐式一致；流中 `{"error":{}}` 我们吞掉（见 §3-9） |
| Google stop reason | 等价 | 16 值 error 表+STOP/MAX_TOKENS+未知 throw，逐值一致 |
| Options 四类 | 等价 | AnthropicOptions 六字段（缺 `client` 注入点：A 无 SDK，Minor）、OpenAiCompletionsOptions/OpenAiResponsesOptions/GoogleOptions 字段与 pi 接口一一对应（Google thinking.level 无法表达 `THINKING_LEVEL_UNSPECIFIED`：Minor） |
| 懒加载 | 等价（A） | pi lazyApi 动态 import 是 bundle 边界手段；C# 单程序集无此需要，LazyStream 语义由 `Events/LazyStream.cs` 承担 |

### 1.6 Fetch 端口 / Hosts / 生态

| 区域 | 判定 | 说明 |
|---|---|---|
| Fetch 端口 | 等价 + D | pi `fetch?: FetchFunction`（types.ts:152/170）；我们自有端口 `Types/Fetch.cs:12-124`（FetchRequest/FetchHeaders 大小写不敏感+null 抑制/FetchOptions 三段超时/IFetchResponse/FetchException(Kind)/TransportKind wire 值 auto/sse/websocket/websocket-cached 与 pi `Transport` 一致）；三段超时/Kind/MaxResponseBytes 为 B 方案 D 扩展（docs/07 登记） |
| Hosts.Net8 适配器 | 等价 | `TransportFetch.cs:39-143`（IStreamingHttpTransport→Fetch 端口、TransportException→FetchException 全 Kind 映射、headers 透传）+ `HttpClientFetch.cs:9-10`；依赖方向正确（Hosts→Ai+transport，Ai 零 Unity/transport 引用，grep 实核零命中） |
| TFM/AOT | 等价（A） | Ai/Core/Runtime = netstandard2.1（csproj 实核）；仅 System.Text.Json 8.0.5 + System.Threading.Channels 8.0.0；`required`/init 用 Polyfills（`Polyfills/RequiredMembers.cs`、`IsExternalInit.cs`）；Hosts.Net8 = net8.0（宿主层，允许） |

---

## 2. 行为 / wire / 生态核对结论

- **事件顺序与终态**：4 适配器与 Faux 的 start/text/thinking/toolcall/done/error 顺序、`onResponse` 时机（响应后、body 消费前）、CT→aborted 归一文案 "Request was aborted"、流中 error 不可重试、重试仅包请求建立——逐点与 pi 对齐（§1.4/1.5）。
- **wire 常量**：全部枚举/JSON 键/header 名经逐字符串核对一致（stopReason 七值、cacheRetention 三值、thinkingLevel 七值、transport 四值、AuthType 两值、`toolUse`/`cache_control`/`alt=sse`/beta flags/`anthropic-version 2023-06-01`/`sk-ant-oat`/`claude-cli/2.1.280`/OAuth 端点与参数键/14 家 env 变量名/baseUrl）。
- **默认值**：`StopReason`/枚举缺省、`Model.ContextWindow=128000`/`MaxTokens=16384`（与 pi faux 与 catalog 缺省一致）、重试 `maxRetries=0`/`maxRetryDelayMs=60000`、退避公式 `min(0.5*2^n,8)s*(1-0.25r)`、服务端延迟超上限立即失败（文案逐字）、PKCE 32B——一致。
- **取消**：CT 单入口（StreamOptions.CancellationToken，登记）、AuthOperationOptions/ResolutionOverrides 贯穿、InMemory store 的 modify 后置取消检查、OAuth 刷新在途免疫——对应 pi signal 语义。
- **重试**：`ProviderRetry.cs` 与 pi `utils/provider-retry.ts` 主循环/计数/noRetryStatuses 顺序/头解析逐条一致；唯一语义差（无状态码仅 Connect 重试）= D 加固，S4 §10 行461 已登记。
- **生态**：依赖方向 R1 达成（Ai 无 UnityEngine/GameData/DomainManager/传输引用，grep 实核）；vendored transport 只被 Hosts.Net8 引用；无 `git add -A` 类问题（本审计不涉及）。

---

## 3. 未登记偏离 / Shape gap

### 3.1 Shape gap（Important）

**S-1 · OAuthCredential 额外字段：pi 顶层平铺索引签名 → 我们嵌套 `extra` 子对象（跨实现 auth.json 互操作收窄）**
- pi：`auth/types.ts:24-29` `OAuthCredentials { refresh; access; expires; [key: string]: unknown }`——任意额外字段**平铺在凭据顶层**（pi.net `Auth/OAuth` 同；实例：github-copilot 把 `enterpriseUrl` 平铺写入）。
- 我们：`Providers/Auth/Credential.cs:17-23` `Extra` 为独立属性；`FileCredentialStore.cs:260-262` 读**只认顶层 `"extra"` 键**，`:357-361` 写把额外字段塞进 `"extra":{}`。
- 为什么不是 A/B/C/D：C# 完全可以平铺表达——`[JsonExtensionData]` 就是索引签名的官方等价物；这不是生态强制，也非 pi.net 表达（pi.net 未嵌套）。当前两家 OAuth（anthropic/openrouter）不产额外字段，故不炸自身；但 pi 侧写出的 auth.json 中平铺字段（如 Copilot `enterpriseUrl`）会被我们**静默丢弃**，方向性兼容被收窄。属"公共持久化形状收窄"= Shape gap（Important）。
- 建议：改用 `[JsonExtensionData]` 平铺（读旧 `extra` 做兼容回落），或由用户明确批准登记为 D 并补入 S3v3 §6 偏离表。

### 3.2 未登记偏离（Important ×1，Minor ×8）

**U-1 · pi 在 Models.applyAuth 注入的 OpenCode 会话头未接线（Important）**
- pi：`models.ts:883` `mergeHeaders(getOpenCodeSessionHeaders(requestModel, sessionId), auth.headers)`——门面层对所有请求注入 `x-opencode-session`。
- 我们：helper 已移植（`Types/ClientIdentityHelpers.cs:97-102`，键名/目标判定逐字对齐 pi identity.ts:130-135），但**全仓零调用**（grep 仅命中定义处）；`Models.Streaming.cs:88-99` 的 ApplyAuthOptions 只做 MergeHeaders(auth.Headers, options.Headers)。
- 为什么不是 A/B/C/D：形状在、行为断，且无任何 spec §10 行/Dn/TODO 锚点覆盖"接线缺失"——不属于任何已登记后置。S4 审计只核对了 helper 形状，未核对接线。
- 建议：在 ApplyAuthOptions 注入（约 3 行），或登记 Dn（目标=涉及 OpenCode 目标的阶段）。

**U-2 · completions/responses 的 `maxTokens > 0` 守卫（Minor）**
- pi：`openai-completions.ts:836-838`、`openai-responses.ts:373-375` 只判 `options?.maxTokens`（显式 0 也发送）；我们 `OpenAiCompletions.cs:870`、`OpenAiResponses.cs:543` 加 `> 0` 守卫（显式 0 静默不发）。主动加固（D）但 spec §10 无行。建议补登记。

**U-3 · Responses `output_item.added/done` 缺 `item` 时跳过而非抛错（Minor）**
- pi：`openai-responses-shared.ts:675-676/746-749` 直接用 `event.item`（缺失抛错→Error 终态）；我们 `OpenAiResponses.cs:1557-1560/1654` 先判 `item is JsonObject` 再处理，缺失跳过。对不合规服务端更宽容（D 倾向），未登记。

**U-4 · Faux 偏差残条（Minor，6 条）**
- 默认 `api` 固定 `"faux"`（Faux.cs:75）vs pi 每次注册 `randomId("faux")`（faux.ts:460）——多实例分派键相撞；
- 分块恒 `min(MinTokenSize,MaxTokenSize)` 且按裸字符（FauxProvider.cs:409-416）vs pi 随机 tokenSize×4 字符（faux.ts:289-299）——同 TokensPerSecond 实际流速约 1/4，且登记只覆盖"固定大小"；
- usage 输出只算 TextContent（FauxProvider.cs:163-187）vs pi 含 thinking/toolcall 文本（faux.ts:246-287）；且无 sessionId cacheRead/Write 前缀模拟；
- 不回调 `onResponse({status:200})`（pi faux.ts:527-586 三处都回调）；
- `GetModel(id)` 未命中抛 `KeyNotFoundException`（FauxProvider.cs:91-94）vs pi 返回 undefined（faux.ts:668-675）；
- 缺 `unregister()`（pi faux.ts:119-145；可用 SetProvider 覆盖等价达成）。
- 建议：并入 S3v3/v2 偏离表 Faux 行或在 _mapping 明示；测试支撑件，风险低。

**U-5 · 刷新 supersede 语义裁剪未显式登记（Minor）**
- pi：`models.ts:498-543` generation map + AbortController map——setProvider/deleteProvider/clearProviders 作废在途刷新；旧代次不得 publish。
- 我们：`Models.Auth.cs:160-220` 顺序 foreach（已登记"顺序化"，D2），但 `Models.cs:40-54` 不作废任何在途刷新、`Models.Auth.cs:411-428` publish 无代次检查。登记只覆盖并发顺序化，未覆盖 supersede。后果：SetProvider 后旧 provider 在途刷新仍可点亮旧表；并发 RefreshAsync 交错 publish（顺序化下窗口小）。

**U-6 · 刷新错误收集缺 `!signal.aborted` 守卫（Minor）**
- pi models.ts:599-614 仅在未中止时把错误收进 `errors`；我们 `Models.Auth.cs:209-216` 非 OCE 异常无条件收进（取消竞态窗口内多记）。语义影响极小。

**U-7 · FileCredentialStore 跨进程互斥未实现且未登记（Minor）**
- pi credential-store.ts:82-84 要求 modify "cross-process too where the backing store supports it"；我们只有进程内 `SemaphoreSlim`（FileCredentialStore.cs:30,125-131），D7 登记备注"多进程共写不在保证范围"在台账，但 spec §10 无行。建议补行即可。

**U-8 · Anthropic fallback 计价丢 `cost.tiers`（Minor）**
- pi anthropic-messages.ts:710-716 fallback 模型带整个 cost 对象（含 tiers）；我们 `AnthropicMessages.cs:500-508` 只取四速率。仅影响 fallback 模型阶梯计价。

**U-9 · 空串 stop_reason/finish_reason：我们抛 Unhandled，pi truthy 跳过（Minor）**
- pi anthropic-messages.ts:841/:224 空串 falsy 被跳过；我们 `AnthropicMessages.cs:736/:407` `is { }` 模式空串命中→`MapStopReason("")`→抛。现实不出现；若出现我们炸流、pi 静默。

**U-10 · Google 流中 `{"error":{...}}` chunk 被静默吞掉（Minor，定级待实机）**
- 我们 `GoogleGenerativeAi.cs:296-317` 无 candidates/usageMetadata 的 chunk 跳过→终态只能报 "Google stream ended without a finish reason"（:224-227）；pi TS 经 @google/genai SDK 的对应行为**本机无法核验**（node_modules 不在 checkout）。建议实机探测一次：若 SDK 会抛带 message 的错误，则升 Important 并补错误体解析。

### 3.3 登记治理缺口（Important ×2，Minor ×3）

**G-1 · "spec §10 已登记、台账无 Dn 行"的后置集合（Important）**
以下后置均在 spec 偏离表有行，但台账 `docs/待办-后置能力.md` 无对应 Dn（无形状锚点/验收测试/目标阶段三件套），违反 docs/03 §1.5"每一条后置能力必须有 ID、形状锚点、验收测试锚点、目标阶段、状态"：
1. **流式工具参数 partial 快照不生长 + `repairJson` 缺席**（S4 §10 行447/462；S5 §10.2 toolcall delta 行）：pi `streaming-json.ts:452-500`（每次 delta 把可容错的部分解析 JSON 写进 `block.arguments`，进入 partial 快照）+ `json-parse.ts:33-96`（finish 前修复控制字符/非法转义）；我们 `ToolCallArguments.cs:13-49` 仅原文累积、终值一次严格 Parse（畸形→ArgumentsError，不炸流——语义等价面已对齐），OpenAiCompletions.cs:600-610 / AnthropicMessages.cs:668-675 同构。代价：流式预览参数的消费者拿不到渐进数据；可修复畸形 JSON 被判 argumentsError（偶发假失败）。
2. **`retry.ts` 公共模块整块缺席**（S4 §10 行458"不实现"）：pi index.ts:40 公开导出 `RetryPolicy/retryDelayMs/retryAssistantCall/isRetryableAssistantError`（utils/retry.ts:134-275，配额/过载正则分类）；我们全仓零对应物（grep 实核）。登记了"不实现"但没有目标阶段——若归 agent 门面阶段需立项锚点。
3. **ChatGPT sign-in 字段省略**（S4 §10 行455；代码锚 `OpenAiResponses.cs:53` 注释 "（D1/S5）"）：pi responses.ts:49-56/362-379 对非 `sk-` 令牌省略 prompt_cache_retention/prompt_cache_options/max_output_tokens/temperature；pi.net 已实现。直连 ChatGPT 订阅令牌时这些字段会被 4xx 拒绝。锚点引用 "D1" 与台账 D1（OAuth 登录）名实不符。
4. **目录 promptCache 不解析**（S4 §10 行467）：`ModelCatalog.cs:50-132` 无 PromptCache 赋值，`Model.cs:60` 字段恒 null——D6 的数据输入面缺一角（D6 行为面已完成）。
5. **copilot×anthropic-messages 动态头**（S5 §10.2 登记；AnthropicMessages.cs:995 注释）。
- 建议：为仍留行为缺口的 1/2/3/4 各补一条 Dn 行（或并入现有 Dn）；1 的 partial 快照是可观测载荷收窄，建议优先。

**G-2 · spec 偏离表现势性失效（Important，文档完整性）**
- **S3 v0.1（2026-11-05）整份偏离表被 v2/v3 全面推翻但无任何"已推翻"标注**——最硬四条：`sealed record Provider(Id,Name,Models,Stream)`（现 `interface IProvider`，IProvider.cs:9）；StreamFn 异步+单 delegate（现同步+双 delegate，StreamFn.cs:8-14）；"S3 不含 auth、Faux keyless"（现 Auth 必需，IProvider.cs:17/StaticProvider.cs:19-26）；SimpleStreamOptions 13 字段清单（现 4 字段，SimpleStreamOptions.cs:3-9）。新会话照死表实现即返工。
- S3 v3 §6 三行被 S4 推翻未标注：`envApiKeyAuth/lazyOAuth 未移植（D14）`（现 AuthHelpers.cs:15,56 已落地、D14 已完成）、`identity 头生成族未移植`（现 ClientIdentityHelpers.cs:26-115）、`FetchModels→IRefreshableProvider 装配随 D2/S4`（现 ProviderFactory.cs:138-222）。
- S3 v2 §8 两行过期：行723"stub 登记 D1–D11"（9/11 已完成）、行724"prompt cache 后置"（D6 已完成）。
- S4 §10 四行失真：行451 `TODO(S5,D1)` 锚已随 D1 完成移除（行为正确、行未标注）；行452 类名 `UnityLlmTransportFetch` 实为 `TransportFetch`（TransportFetch.cs:17）；行464/465 行号漂移（`{status}: {body}` 实在 OpenAiResponses.cs:3040；Usage.Reasoning 判空实在 OpenAiCompletions.cs:2708 与 OpenAiResponses.cs:1499-1500——**1499 单行形似恒 0，必须连读 1500**，未来按行号引用易误判）。
- 建议：给被推翻的表行加"→ 被 vX 推翻"标注；修正 S4 四行。

**G-3 · 代码锚约定违例（Minor）**
- `Providers/Auth/OAuth/AnthropicOAuth.cs:81-83` browser 分支 `NotSupportedException` 文案**无 Dn 编号**（docs/03 §1.5 要求 `Deferred to Sx: ... (Dn)` 格式；台账 D1 备注有提及但代码锚不合格式）。
- D11（transcript 重放）是台账中唯一"未开工且代码零锚"的条目——形状锚 `TranscriptNormalization.cs` 在，但无 `TODO(S9,D11)` 标记（§1.5 第三种锚格式）。
- S1 §4.4 声称 `ImagesStopReason/ClassifierStopReason/NestedToolCallStatus` 有自定义 converter，实为裸 enum 数字序列化（ImagesTypes.cs:20-25、ClassifierTypes.cs:88-93、NestedToolCalls.cs:6-11）——当前无处序列化，低危；spec 声明与代码不符。

---

## 4. C 类 Dn 检查

| 项 | 结论 | 证据 |
|---|---|---|
| 台账 D1–D15 行完整性 | **齐** | 15 行均有 ID/能力/参考/形状锚点/验收测试锚点/目标阶段/状态（docs/待办-后置能力.md:11-25） |
| 验收测试锚点存在性 | **齐** | 抽样实核：`OAuthTests.cs`(33)、`S5AuthIntegrationTests.cs`(16)、`FileCredentialStoreTests.cs`(11)、`S4RefreshTests.cs`(11)、`S4ProviderProfileTests.cs`(9)、`S5ProviderProfileTests.cs`(17)、`ModelsCapabilityTests.cs`(2)、`S4DeferredCapabilityTests.cs`(6)、`TranscriptNormalizationTests.cs`(6)、`AuthHelpersTests.cs`(10)、`ClientIdentityHelpersTests.cs`(17)、`StreamOptionsTests.cs`(11)、`ModelShapeTests.cs`(4) 全部存在；全仓 510 Fact/Theory |
| 代码 TODO(Dn)/NS 与台账一致性 | **一致，无孤儿** | 全 src 10 处标记（LazyStreamObserver.cs:15=D8；AnthropicProvider.cs:13,87=D15；AnthropicOAuth.cs:81=browser 后置[无编号，见 G-3]；Models.Capabilities.cs:14,21,28=D3；:35,42=D4/D5；ProviderFactory.cs:97,103=D4/D5）全部有台账行；grep 证实的 "D800" 命中是 `\uD800` 正则字面量（误报） |
| 已完成 Dn 无残留 stub | **达标** | D1/D2/D6/D7/D9/D10/D12/D13/D14 已完成且无 NS/TODO 残留；剩余 stub 全属未开工 Dn（D3/D4/D5/D8/D15/D11） |
| 已登记于 spec §10 但台账无 Dn 的后置 | **缺口 5 项** | 见 §3.3 G-1（partial 快照/repairJson、retry.ts 模块、ChatGPT sign-in 字段、目录 promptCache、copilot×anthropic 头） |
| 门禁脚本 | 存在且规则齐 | `scripts/check-deferred.py` 四规则（锚必须引用台账 ID/已完成不得残留/阶段门禁/锚点必填） |

---

## 5. 必需动作

- [ ] **S-1（OAuthCredential 额外字段）**：改 `[JsonExtensionData]` 平铺（含旧 `extra` 读兼容），或用户明确批准登记为 D 并补 S3v3 §6 行。
- [ ] **U-1（OpenCode 会话头接线）**：在 `Models.Streaming.ApplyAuthOptions` 注入 `GetOpenCodeSessionHeaders`（≈3 行），或登记 Dn。
- [ ] **G-1**：为 partial 快照/repairJson、`retry.ts`、ChatGPT sign-in 字段省略、目录 promptCache 补 Dn 行（形状锚点+验收测试+目标阶段）；"copilot×anthropic 头"并入现有偏离行。
- [ ] **G-2**：S3 v0.1 偏离表整体标注"已被 v2/v3 推翻"；S3 v3 三行、S3 v2 两行加过期标注；S4 §10 行451/452/464/4修正。
- [ ] **G-3**：AnthropicOAuth browser NS 文案补 Dn 编号；D11 补 `TODO(S9,D11)` 锚；S1 三个可选枚举补 converter 或改 spec 声明。
- [ ] Minor/U-2~U-9：按"补 spec §10 行"批量登记（一批 docs 提交即可）；U-10 建议随下次实机验证探测 Google 流中错误体行为后定级。

## 6. 审计者附注

**误报驳回（采集 subagent 报告中被本审计者复核否决的结论）**：
1. S5 采集报告曾报"Anthropic federation 整条链缺失且无 Dn/TODO 锚点（Critical）"——**驳回**：federation 登记完备：`Providers/AnthropicProvider.cs:13,87` `TODO(后续扩展,D15)` + 台账 D15 行（目标=后续扩展、验收测试 `S5ProviderProfileTests.Anthropic_Without_Env_Or_Credential_Returns_Null`）+ S5 spec §10.2 federation 行。federation 位于 provider 认证链（AnthropicProvider）而非 API 适配器（AnthropicMessages），采集者在错误层级查找。登记链完整，判定 C 合规。
2. S3/S4 采集报告把"流式参数 partial 不生长"标为"无锚点的未登记偏离"——**部分修正**：S4 §10 行447/462 与 S5 §10.2 已明确登记该行为裁剪；缺的是台账 Dn 行（降为治理缺口 G-1）。
3. **U-2（`maxTokens > 0` 守卫）——控制器裁定驳回**（2026-11-09）：审计称“pi 只判 `options?.maxTokens`（显式 0 也发送）”不成立。pi 为 JS 真值判断：`if (options?.maxTokens)`（`openai-completions.ts:826`）、`if (options?.maxTokens && ...)`（`openai-responses.ts:373`）——`0` 是 falsy，**pi 不发 0**。我们 `OpenAiCompletions.cs:870` / `OpenAiResponses.cs:543` 的 `> 0` 守卫与 pi 一致；pi.net 的 OpenAI 适配器同样有 `> 0` 守卫（`Pi.Ai/Api/OpenAiCompletions.cs:1083`），即两家 C# 移植在 0 值上都与 pi 同向（个别非 OpenAI 适配器如 Google 用裸 `is { }`，与本条无关）。故 U-2 不是偏离，不补登记、不修复。

**无法验证项**：
- pi 上游 `providers/data/*.json` 不在本地 checkout（仅 data-json.d.ts），19 份模型目录 JSON 只能与 pi.net 快照对数（抽样 anthropic/fireworks/openai 逐字段一致）；与 pi 最新生成物的代差需在 D9 补一次上游对数。
- Google 流中错误体（`{"error":{}}`）在 pi TS 经 @google/genai SDK 的实际行为（node_modules 不在 checkout）→ U-10 定级待实机。
- OpenAI/Anthropic 官方 SDK 注入的 `x-stainless-*` 遥测头清单不可从 pi 源核验；我们无 SDK 故无这些头（对做指纹的网关可能有行为差）——观察项。
- Anthropic `baseUrl` 以 `/v1` 结尾时的拼接行为：pi TS 由 SDK 处理、不可本机核验；我们无 `NormalizeLegacyBaseUrl`（pi.net 有）——理论双拼 `/v1/v1/messages`，14 家 profile 的 baseUrl 实配均不带 `/v1`，现网不触发。
- Unity 2022.3 实机（Mono/IL2CPP AOT）下 `JsonPolymorphic` 特性路由与 STJ 8 表现：S1 spec D2 已声明"AOT 有风险、Mono 待实机"，本审计为静态审计不覆盖。

**版本说明**：pi/pi.net/UnityLlmTransport 三参考版本与本仓 PORTING.md/docs/03 登记一致；vendored transport 的 `4c0cc4b` 仅能核对到 `VENDORED.md`/`VERSION` 声明与目录内容，无法在仓内对其原始 commit 做哈希验证（拷贝范围排除 .git）。

**范围说明**：本审计对象为当前 main 代码现状（SOP §3"必审"四面）；普通代码质量/测试充分性/性能不在本报告范围（归常规 code review）。测试未重跑（只核锚点存在性）；构建/单测门禁由控制器负责。
