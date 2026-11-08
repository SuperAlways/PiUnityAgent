# S5 Anthropic + Google 协议适配设计 spec（2026-11-08）

> **状态**：设计稿 v1，待用户 review 后进入 plan。
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

## 10. 三方偏离表（S5 初版）

| 项 | pi | pi.net | 我们 | 类别 | 代价 |
|---|---|---|---|---|---|
| OAuth HTTP | 全局 fetch/SDK | HttpClient | 注入 FetchFunction | B/D | profile 工厂多一个可选 fetch 参数 |
| OAuth 浏览器回调 | loopback + manual | callback server | S5 先 copy-code/manual | C | 浏览器自动回调后置 |
| Google 自定义 fetch | pi 拒绝 | — | Fetch 端口统一 | B/D | 无 |
| Anthropic tool 名 Canonical | OAuth 映射 | 同 | 照搬 | B | — |
| cache_control | pi 全量 | 同 | Anthropic 映射；Google 无 | B/C | — |
| 7 profiles | 45+ | 50 | 核心 5+OpenRouter | C/D9 | 其余增量 |
| 图片/分类 | 有 | 有 | 不做 | C/D4/D5 | 2B |

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
