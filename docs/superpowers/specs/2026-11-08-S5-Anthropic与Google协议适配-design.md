# S5 Anthropic + Google 协议适配设计 spec（2026-11-08）

> **状态**：已实现（`feature/s5-anthropic-google`，实现完成待独立审计；偏离表 §10 为 Task 1–6 报告汇总的待审计声明）。
> **范围**：S5 = `anthropic-messages` + `google-generative-ai`；首批厂商：Anthropic、MiniMax/MiniMax CN、Google、Fireworks、Vercel AI Gateway、OpenRouter（用户裁决：核心 5 + OpenRouter）。
> **参考版本**：pi `v1.5.0` / `f9a263655`；pi.net `main` / `2d6757b`；UnityLlmTransport `4c0cc4b` / `0.6.0`（仅通讯事实）。
> **上位规则**：[`docs/03 §1.3–§1.6`](../../03-开发方法-拆解学习与移植约定.md)、[`docs/05 §2.3`](../../05-教学阶段路线.md)。
> **目标**：实现两协议 + 7 个常用 profile；承接 D1 Anthropic/OpenRouter OAuth、D6 Anthropic cache、D7 文件凭据 store、D10 可用性；单测全走 MockFetch。

---

## 0. 背景

S4 已把 OpenAI 家族两协议 + 8 family profile 接到 Fetch 端口，并完成 SSE/tool args/retry/auth helpers/identity。S5 补 Anthropic Messages 与 Google Gemini 两套完全不同的 wire：

- Anthropic：命名 SSE 事件（message_start/content_block_*…）、content_block 索引、thinking signature、cache_control、tool blocks；
- Google：contents/systemInstruction/parts、thought parts、functionCall、candidate/finishReason；
- 两者都不复用 OpenAI 的 `messages/tool_calls` 解析状态机，但有共用的 Fetch/SSE cancellation/retry/usage/cost 基础设施。

S5 同时把 D1（Anthropic/OpenRouter OAuth）和 D7（文件凭据 store）从后置台账转成行为验收。

---

## 1. 目标 / 非目标

### 1.1 目标

- 新增 `Api/AnthropicMessages.cs`、`Api/GoogleGenerativeAi.cs`；
- 新增 `AnthropicOptions`、`GoogleOptions` 公共形状；
- profile：Anthropic、MiniMax、MiniMax CN、Google、Fireworks、Vercel AI Gateway、OpenRouter；
- Fireworks/OpenRouter 用 `ApiByApi` 同时接 `anthropic-messages` 与 `openai-completions`；
- D1：Anthropic + OpenRouter OAuth（登录/刷新/登出）与 D7 `FileCredentialStore`；
- D6：Anthropic cache_control / usage cache 字段映射；
- D10：OAuth/API key 可用性过滤；
- MockFetch 测试，无网、无 key；
- Ai netstandard2.1；零 UnityEngine / UnityLlmTransport / HttpClient。

### 1.2 非目标

- Google Vertex / Bedrock / Azure / Copilot/Codex 特殊 auth；
- 图片/分类 provider（D4/D5）；
- 真实 OAuth 浏览器回调服务器（S5 先做 copy-code/manual 流；浏览器 loopback 可后置）；
- S6+ Agent Loop/工具执行；
- 新的 transport 依赖；Unity 实机验收；
- MCP/Telemetry。

---

## 2. S3/S4 既有 V4 消费面

| 形状 | 用途 |
|---|---|
| `FetchFunction`/`FetchOptions`/`IFetchResponse`/`FetchException` | 通讯唯一缝 |
| `ProviderStreams`/`StreamFn`/`SimpleStreamFn` | 协议适配出口 |
| `SseEventReader`/`ToolCallArguments`/`ProviderRetry` | 已有基础设施 |
| `AuthHelpers`/`ClientIdentityHelpers` | D14 已有 helper |
| `Models`/`ProviderFactory`/`IModelsStore`/`CredentialStore` | 门面/装配/持久化 |

S5 不修改 S3/S4 公共形状；只新增协议、options、profile、OAuth/store。

---

## 3. 协议与 profile

| 协议 | profile |
|---|---|
| `anthropic-messages` | Anthropic、MiniMax、MiniMax CN、Fireworks、Vercel AI Gateway、OpenRouter |
| `google-generative-ai` | Google |
| 双协议 | Fireworks、OpenRouter（`ApiByApi`：anthropic + openai-completions） |

| profile | id | BaseUrl | auth | API |
|---|---|---|---|---|
| Anthropic | `anthropic` | `https://api.anthropic.com` | API key + Anthropic OAuth | anthropic |
| MiniMax | `minimax` | `https://api.minimax.io/anthropic` | `MINIMAX_API_KEY` | anthropic |
| MiniMax CN | `minimax-cn` | `https://api.minimaxi.com/anthropic` | `MINIMAX_CN_API_KEY` | anthropic |
| Google | `google` | `https://generativelanguage.googleapis.com/v1beta` | `GEMINI_API_KEY` | google |
| Fireworks | `fireworks` | `https://api.fireworks.ai/inference` | `FIREWORKS_API_KEY` | anthropic + openai-completions |
| Vercel AI Gateway | `vercel-ai-gateway` | `https://ai-gateway.vercel.sh` | `AI_GATEWAY_API_KEY` | anthropic |
| OpenRouter | `openrouter` | `https://openrouter.ai/api/v1` | API key + OpenRouter OAuth | anthropic + openai-completions |

---

## 4. 文件结构

```text
src/PiUnityAgent.Ai/
├─ Api/
│  ├─ AnthropicMessages.cs / AnthropicOptions.cs
│  └─ GoogleGenerativeAi.cs / GoogleOptions.cs
├─ Providers/
│  ├─ AnthropicProvider.cs
│  ├─ MinimaxProvider.cs
│  ├─ GoogleProvider.cs
│  ├─ FireworksProvider.cs
│  ├─ VercelAiGatewayProvider.cs
│  ├─ OpenRouterProvider.cs
│  ├─ ModelData/{anthropic,minimax,minimax-cn,google,fireworks,vercel-ai-gateway,openrouter}.json
│  └─ Auth/
│     ├─ OAuth/AnthropicOAuth.cs
│     ├─ OAuth/OpenRouterOAuth.cs
│     ├─ OAuth/OAuthHttp.cs / Pkce.cs / OAuthInput.cs
│     └─ FileCredentialStore.cs
```

---

## 5. 公共形状

```csharp
public enum AnthropicThinkingDisplay { Summarized, Omitted }

public sealed record AnthropicOptions : StreamOptions
{
    public bool? ThinkingEnabled { get; init; }
    public int? ThinkingBudgetTokens { get; init; }
    public ThinkingLevel? Effort { get; init; }
    public AnthropicThinkingDisplay? ThinkingDisplay { get; init; }
    public bool? InterleavedThinking { get; init; }
    public JsonNode? ToolChoice { get; init; }
}

public sealed record GoogleThinkingOptions
{
    public bool Enabled { get; init; }
    public int? BudgetTokens { get; init; }
    public ThinkingLevel? Level { get; init; }
}

public sealed record GoogleOptions : StreamOptions
{
    public JsonNode? ToolChoice { get; init; }
    public GoogleThinkingOptions? Thinking { get; init; }
}

public static class AnthropicMessages
{
    public static ProviderStreams Streams();
    public static IAssistantMessageEventStream Stream(Model, TranscriptContext, AnthropicOptions?);
    public static IAssistantMessageEventStream StreamSimple(Model, TranscriptContext, SimpleStreamOptions?);
}

public static class GoogleGenerativeAi
{
    public static ProviderStreams Streams();
    public static IAssistantMessageEventStream Stream(Model, TranscriptContext, GoogleOptions?);
    public static IAssistantMessageEventStream StreamSimple(Model, TranscriptContext, SimpleStreamOptions?);
}
```

---

## 6. 协议行为

### 6.1 Anthropic Messages

按 pi `anthropic-messages.ts` 端口：
- 请求：`POST /v1/messages`；headers `x-api-key`/`anthropic-version`/beta、OAuth `Authorization: Bearer`；body messages/system/tools/thinking/cache_control。
- SSE：命名事件 `message_start` / `content_block_start` / `content_block_delta` / `content_block_stop` / `message_delta` / `message_stop`。
- 文本/thinking/tool_use：`content_block` index → 统一 contentIndex；`input_json_delta` 累积；`signature_delta` 回填；redacted thinking。
- usage：input/output/cache read/cache write（含 1h cache write）；cache_control 由 D6 映射。
- stop_reason 映射；错误/取消终态；首字节前 retry；流开始后不重试。

### 6.2 Google Generative AI

按 pi `google-generative-ai.ts` 端口：
- 请求：`generateContent`/stream；contents + systemInstruction + generationConfig + tools + thinkingConfig。
- 响应：candidate/parts；text/thought/functionCall；thoughtSignature；finishReason/usage。
- 不支持自定义 Fetch 的 pi 限制不照抄：我们统一走 Fetch 端口。
- 事件顺序 start/text|thinking|toolcall/done/error。

---

## 7. D1 OAuth / D7 持久化 store

- `AnthropicOAuth`：copy-code/manual 登录；PKCE；token exchange 走注入的 `FetchFunction`；Refresh；ToAuth。
- `OpenRouterOAuth`：manual code/PKCE；换取永久 API key；Refresh no-op；ToAuth 返回 key。
- OAuth 的 HTTP 交换需要 host 注入：profile 工厂 `Create(FetchFunction? oauthFetch = null)`（不引用 HttpClient/Transport）；这是 pi 之外的 B/D 扩展，测试用 MockFetch。
- 浏览器 loopback 回调服务器后置（C）；S5 用 copy-code/manual 满足登录。
- `FileCredentialStore`：JSON 文件、原子写、per-provider `SemaphoreSlim`、`~` 展开；路径由宿主/测试注入；不落明文日志。
- `Models.LoginAsync/LogoutAsync` 接入 provider auth；OAuth 刷新写回 store；失败包 ModelsError(Auth/OAuth)。

---

## 8. D6 / D10

- D6：Anthropic `cache_control`（prompt/system/tools/last message）+ usage cacheRead/cacheWrite；OpenAI cache 已有；Google 无 cache 字段则保持。
- D10：OAuth 凭据可用性；未配置 provider 不返回可用模型；`GetAvailable*` 单次 Check。

---

## 9. 测试与验收

- Anthropic MockFetch：文本、thinking、tool_use/input_json_delta、signature、usage/cache、stop、错误、取消、retry。
- Google MockFetch：text/thought 切换、functionCall、usage、finishReason、错误、取消。
- Profiles 7 family：id/baseUrl/auth/models；双协议 profile 的 `ApiByApi` 分派。
- OAuth：copy-code/manual + token exchange MockFetch；refresh；logout；FileCredentialStore 原子写/并发/重启读。
- D6/D10 验收；Release 0/0；Ai tests；Hosts tests；`check-deferred --current-stage S5 --gate exit`。
- 独立偏离审计。

---

## 10. 三方偏离表（S5 收尾汇总，2026-11-09 Task 7 收编 Task 1–6 报告）

> ⚠️ **待审计声明**：下表由 Task 1–6 报告汇总而成，是**待审计声明**而非已核验结论。
> 独立审计（`docs/audits/2026-11-08-S5-deviation-audit.md`）必须逐项核验类别判定与"我们"列现状（代码/测试证据），不得照抄本表；核验出不符时以审计报告为准并回改本表。

### 10.1 公共/传输

| 项 | pi | pi.net | 我们 | 类别 | 代价 |
|---|---|---|---|---|---|
| OAuth HTTP | 全局 fetch/SDK | HttpClient | 注入 `FetchFunction`（profile 工厂 `Create(FetchFunction? oauthFetch)`，宿主注入） | B/D | profile 工厂多一个可选 fetch 参数 |
| OAuth fetch 注入缝 | lazyOAuth 内闭包 | HttpClient | `AuthHelpers.LazyOAuth` + 工厂参数 | B/D | 同上 |
| OAuth 超时 | `AbortSignal.timeout(30s)` / linked CTS | HttpClient timeout | `FetchOptions{Connect,FirstByte,Idle}=30s`（刷新锁内 15s 同 pi） | B/D | 超时执行归宿主传输 |
| OAuth 浏览器回调 | loopback 服务器 + manual 竞速 | callback server | 仅 copy-code/manual；Anthropic browser 选项选中 → `NotSupportedException`；无 `Listening for callback...` progress 事件（manual-only 下不真） | C | 浏览器自动回调后置 |
| OpenRouter redirect | 临时端口 loopback | callback server | 固定回环常量 `http://localhost:53693/oauth/callback`（构造参数可覆盖；pi 无固定端口参照，Task 6 选定） | C | 审计可改宿主注入；交换正确性不依赖它 |
| login abort 竞速 | mutation-started 侦听 race | — | 顺序流 + store 边界 ct 检查（锁等待可取消、取消不落盘；刷新在途取消对轮换免疫=pi 同口径） | D | 行为等价面，无竞速结构 |
| `expires_in` 校验 | TS 无校验（缺键→undefined 静默） | 显式校验 | 三键（refresh/access/expires）缺一 → `invalid JSON` | C（采 pi.net） | 更严；C# 可空安全所需 |
| netstandard2.1 API | `SHA256.HashData`/`RandomNumberGenerator.GetBytes(int)`/`System.Web.HttpUtility` | net8 API | `SHA256.Create()`+`Fill`/手写查询串解析+`WebUtility.UrlDecode` | D | 无行为差异（测试向量锁定） |
| 传输（两协议） | Anthropic/@google SDK 内部 fetch | HttpClient | 注入 `FetchFunction` 直发 REST | B/D | 与 S4 同一通讯缝 |
| Google 自定义 fetch | pi 拒绝 | — | Fetch 端口统一 | B/D | 无 |
| 共享 helper 私有副本 | 共享 util（transform-messages/strict-sampling/错误格式化等） | 同左左源 | 各适配器私有拷贝（4+ 份） | D/TODO | 去重需跨文件重构，留统一批次 |

### 10.2 Anthropic

| 项 | pi | pi.net | 我们 | 类别 | 代价 |
|---|---|---|---|---|---|
| copilot 头分支 | createClient 的 github-copilot Bearer + 动态头 | — | 未实现 | C | copilot 非 S5 profile；需要时另立项 |
| federation（工作负载身份联合） | anthropic.ts:53-69 env 兜底 | — | 未实现，env 链末尾返回 null，源码 `TODO(后续扩展,D15)` | C/D15 | 台账 D15 行（后续扩展） |
| betas | SDK 把 params.betas 转 `anthropic-beta` 头 | 同 | `BuildRequest` 出体转头，OnPayload 仍可见 betas | B | 行为等价 |
| `AnthropicOptions.Effort` 形状 | `AnthropicEffort` 字符串 union | 未移植 | `ThinkingLevel?` 枚举（含 Minimal） | B/D | StreamSimple 字符串→枚举回转；映射缺失按 pi default 回落 high |
| SSE 事件读取 | `response.body` ReadableStream + 自带 consume | HttpClient 流 | 既有 `SseEventReader`（S4 定型） | B | 语义等价；解析失败消息只含 data |
| JSON 解析 | `parseJsonWithRepair`（容错修复） | 严格 | `JsonNode.Parse`（严格） | C | 坏 data 终态 Error 而非尽力修复 |
| message_start 无 usage | 直取（缺失 TypeError→终态 Error） | 同款守卫 | usage 缺失保留零值继续 | C | 更稳；真实 API 必带 usage |
| toolcall delta partial 参数 | `append` 即时渐进解析并写回块 | — | `ToolCallArguments.Append` 只累积原文，`content_block_stop` 一次 Finish | C | 过程中参数预览停在初始值；终值与 pi 一致；补齐需 parse-so-far 解析器 |
| OpenRouter 实付覆盖 helper | 共享 util `openrouter-cost.ts` | 同源 | `OpenAiCompletions`/`AnthropicMessages` 各一份私有拷贝 | D/TODO | 去重候选（提 internal 会反向依赖，留统一批次） |
| StreamSimple 缺 key | SDK 抛异常 | — | 终态 `Error` 事件（无请求发出） | B | 与全适配器统一终态口径一致 |
| 基类 options 透传 | 对象展开（TS 结构兼容） | 逐字段平铺 | 每适配器 `ToXxxOptions(StreamOptions?)` 逐字段拷贝转换器（Task 5b 修复） | D | 公开签名零改动；新基类字段需同步转换器清单 |
| strict-fallback 重发 | stream 循环 | — | 已实现（Task 3，非偏离） | — | `HasPreferredStrictTools` + 诊断三件套 |

### 10.3 Google

| 项 | pi | pi.net | 我们 | 类别 | 代价 |
|---|---|---|---|---|---|
| wire 顶层字段 | SDK 展开=GenerateContentRequest 顶层 | **放进 generationConfig**（与 Google REST schema 相悖，实发会被拒） | 顶层 `systemInstruction`/`tools`/`toolConfig`；`thinkingConfig` 在 generationConfig 内 | B（以 pi.ts+官方 REST schema 为准） | 无 |
| apiKey 位置 | SDK→`x-goog-api-key` 头 | `?key=` query | `x-goog-api-key` 头 | B | 防 key 泄进 URL 日志 |
| OnResponse | 不调用（SDK 藏 raw response） | 调用 | 调用（brief 要求，与 OpenAI/Anthropic 移植同） | B | 无 |
| samplingParams | buildBaseOptions 解析但 Google buildParams 不消费 | 并进 generationConfig | 解析进 options、payload 不消费（照 pi.ts） | B | Google 模型采样参数覆盖不落线（pi 行为） |
| convertTools `useParameters` | 有（Vertex Cloud Code Assist） | 有 | 未移植 | C/D | Vertex 非 S5 范围；接 Vertex 时补 |
| `XHigh`/`Max` thinking level | 类型上不可能直传 | — | 直传抛 Unsupported → 终态 Error | B | 无 |
| functionResponse 边缘 | 2.x 图文混排 `response.output` 仍文本、纯图拆独立 turn | 同 | 同 pi（Task 4 补测钉死：初版测试写错、生产码对） | C | 已按 pi 钉死，无残留 |
| 流形态 | SDK 走 `alt=sse` | 同 | 仅 `alt=sse`；JSON（非 SSE）形态未实现 | C | pi SDK 亦只走 alt=sse |
| StreamSimple context 估算 | 共享 estimate util | — | 每适配器私有 `EstimateContextTokens`（4096 安全余量） | D/TODO | 与 §10.1 helper 副本同类，去重候选 |
| StreamSimple 缺 key | SDK 抛异常 | — | 终态 `Error` 事件（无请求发出） | B | 同 Anthropic 口径 |

### 10.4 范围

| 项 | pi | pi.net | 我们 | 类别 | 代价 |
|---|---|---|---|---|---|
| profiles | 45+ | 50 | 7（Anthropic/MiniMax/MiniMax CN/Google/Fireworks/Vercel AI Gateway/OpenRouter，双协议 Fireworks/OpenRouter） | C/D9 | 其余增量接（台账 D9） |
| 图片 provider | openrouter-images 等 | 有 | 不做 | C/D4 | 2B |
| 分类 provider | typesafe-system-one 等 | 有 | 不做 | C/D5 | 2B |
| OpenRouter 目录个性化 | `GET /models/user` 按用户过滤目录 | — | 未实现（静态目录照 pi.net 数据） | C | 按需另立项 |
| Anthropic tool 名 Canonical | OAuth 映射 toClaudeCodeName/fromClaudeCodeName | 同 | 照搬（双向） | B | — |
| cache_control | pi 全量 | 同 | Anthropic 映射（D6）；Google 无 cache 字段（pi google 同） | B/C | — |
| Vertex/Bedrock/Azure/Copilot auth | 有 | 有 | 不做（非目标） | C | 按 §1.2 范围 |

---

## 11. 实施顺序

1. options + model data + auth/OAuth seam/store shapes
2. Anthropic adapter
3. Google adapter
4. profiles + catalog
5. D1 OAuth + D7 store + Models login/logout
6. D6/D10 tests + docs + S5 audit

<!-- END SPEC -->
</content>
