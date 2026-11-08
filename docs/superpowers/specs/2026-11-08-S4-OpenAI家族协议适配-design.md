# S4 OpenAI 家族协议适配设计 spec（2026-11-08）

> **状态**：设计稿 v1，待用户 review 后进入 plan。
> **范围**：S4 只做 OpenAI 家族：`openai-completions` + `openai-responses`；S5 的 `anthropic-messages` / `google-generative-ai` 另立 spec。
> **参考版本**：pi `v1.5.0` / `f9a263655`；pi.net `main` / `2d6757b`；UnityLlmTransport `4c0cc4b` / `VERSION 0.6.0`（仅通讯事实，S4 不引用）。
> **上位规则**：[`docs/03 §1.3/§1.4/§1.5/§1.6`](../../03-开发方法-拆解学习与移植约定.md)、[`docs/05 §2.3`](../../05-教学阶段路线.md)。
> **审计 SOP**：[`docs/SOP/阶段偏离审查-SOP-2026-11-08.md`](../../SOP/阶段偏离审查-SOP-2026-11-08.md)。
> **目标**：把 S3 的公共形状接到真实 OpenAI-compatible wire；首批 profile 可跑；D2/D6/D9/D10/D12/D13/D14 承接；不依赖真实网络。

---

## 0. 背景

S3 已把 Ai 层的 Provider/Auth/Models/Options/Fetch 形状补齐，并用 Faux 验证了同步流。S4 要做的不是“再写一个 Faux”，而是第一次把 `ProviderStreams` 接到真实 HTTP + SSE：

- `openai-completions`：覆盖 DeepSeek 与大量 OpenAI-compatible 服务；
- `openai-responses`：覆盖 OpenAI 官方、xAI/Grok；
- provider profile 只负责 baseUrl/auth/models/compat；
- 所有单测走 `MockFetch`，无网、无 key。

S4 的关键难点不是 HTTP，而是：**SSE 事件状态机、tool_calls 参数流、reasoning 字段差异、usage/cache、finish_reason、首字节前重试**。

---

## 1. 目标 / 非目标

### 1.1 目标

- 新增 `Api/` 协议适配层，实现 `openai-completions` 与 `openai-responses`；
- 新增首批 8 家 family profile：OpenAI 官方、xAI/Grok、DeepSeek、Moonshot/Kimi、Z.ai/智谱、Qwen Token Plan、小米、Groq；通用 OpenAI-compatible 走 ProviderFactory recipe；
- 支持文本、thinking、tool_calls 的流式翻译；
- 支持 usage/cost、finish_reason、错误/取消/超时/首字节前重试；
- 新增 D14 auth helpers/identity 头生成，接入 provider profile；
- 承接 D2/D6/D9/D10/D12/D13；
- 以 `MockFetch` 完成协议级测试，不依赖真实网络；
- Ai 仍为 `netstandard2.1`，零 UnityEngine / UnityLlmTransport / HttpClient。

### 1.2 非目标

- S5 的 Anthropic / Google 协议实现；
- Google Vertex / Azure / Bedrock / Mistral / `pi-messages`；
- OpenRouter、Together、Cerebras、NVIDIA、HuggingFace、Baseten、AntLing、Fireworks 等后续 profile；
- GitHub Copilot / Codex 的 OAuth/WebSocket 链路；
- OpenAI ChatGPT OAuth 登录行为（D1/S5；S4 用 API Key）；
- 图片/分类 provider（D4/D5）；
- 真实网络、Unity 实机联调、游戏内验收；
- 完整 transcript 重放（D11/S9）；S4 只做 provider 发送前的归一化；
- 新增全局 `ApiRegistry`；S4 用 `ProviderFactory` 的 `Api`/`ApiByApi` 分派，动态注册后续按需登记。

---

## 2. 现有 S3 形状（消费面）

S4 只消费，不重定义：

| 形状 | 用途 |
|---|---|
| `ProviderStreams` / `StreamFn` / `SimpleStreamFn` | 协议适配器出口 |
| `CreateProviderOptions.Api/ApiByApi` | profile 到协议的分派 |
| `StreamOptions` / `SimpleStreamOptions` | 请求公共选项 |
| `FetchFunction` / `FetchRequest` / `IFetchResponse` / `FetchException` / `FetchOptions` | 唯一通讯接缝 |
| `AssistantMessageEvent` / `IAssistantMessageEventStream` / `LazyStream` | 事件流 |
| `TranscriptNormalization` | `Context` → `TranscriptContext` |
| `Models` / `ProviderFactory` | 门面/装配 |
| `Model.Compat`（`JsonObject?`） | provider 兼容参数（S3 形状） |
| `Model.PromptCache` / `StreamOptions.CacheRetention` | D6 |
| `IModelsStore` / `ModelsPublication` / `IRefreshableProvider` | D2 |

S4 不修改这些公共形状；只新增 `Api/` 与 provider profile/模型数据。

---

## 3. 协议与 provider 范围

| 协议 | 首批 profile（8 家 family） | 说明 |
|---|---|---|
| `openai-completions` | 通用 OpenAI-compatible、DeepSeek、Moonshot/Kimi、Z.ai/智谱、Qwen Token Plan、小米、Groq | chat/completions + SSE |
| `openai-responses` | OpenAI 官方、xAI/Grok | Responses API（input items / output items） |

**首批 8 家 family**（含国内厂商的 CN/区域变体）：

1. OpenAI 官方（Responses）
2. xAI / Grok（Responses）
3. DeepSeek（Completions）
4. Moonshot / Kimi（Completions；intl + CN）
5. Z.ai / 智谱（Completions；z.ai + coding-cn）
6. Qwen Token Plan（Completions；intl + CN + individual）
7. 小米（Completions；base + token-plan 变体后续）
8. Groq（Completions）

暂缓：Together/Cerebras/NVIDIA/HuggingFace/Baseten/AntLing/Fireworks 等简单 profile 后续增量接；OpenRouter、Azure、Cloudflare、Copilot、OpenCode/Codex 等特殊 profile 后置；MiniMax/Anthropic/Google 属 S5。

### 3.1 Provider vs API（边界，强制）

- **API/协议层**：只负责 wire（请求体、SSE、事件翻译、usage/stop）；S4 只有 `openai-completions` 与 `openai-responses` 两套。
- **Provider/profile 层**：负责 baseUrl、auth、headers、模型目录、compat、可用性策略；它**不实现 SSE/JSON 解析**。
- **Model/目录层**：负责具体模型 id、provider、api、context window、cost、reasoning/cache 能力。

`DeepseekProvider` / `OpenAiProvider` / `XaiProvider` 的形态就是：

```text
Provider = ProviderFactory.Create(
    id / baseUrl / auth / models / api = OpenAiCompletions.Streams() 或 OpenAiResponses.Streams()
)
```

它们不是新协议；只是把 pi 的 `providers/*.ts` 对应成 C# profile。通用 OpenAI-compatible 端点可以直接走 `ProviderFactory.Create` recipe，不需要新增公共 provider 类。Moonshot/Z.ai/Qwen/小米/Groq 同属首批 thin profile；后续接 Together/Cerebras/NVIDIA/HuggingFace 等也优先走“profile + 模型数据 + compat”，不新增协议适配器。

---

## 4. 文件结构

```text
src/PiUnityAgent.Ai/
├─ Api/
│  ├─ OpenAiCompletions.cs          # 协议适配器 + Streams()
│  ├─ OpenAiCompletionsOptions.cs   # provider 专属选项
│  ├─ OpenAiResponses.cs            # 协议适配器 + Streams()
│  ├─ OpenAiResponsesOptions.cs     # provider 专属选项
│  ├─ OpenAiShared.cs               # 公共 JSON/headers/stop/usage 小工具（internal）
│  ├─ ProviderRetry.cs              # provider 层首字节前重试（internal）
│  ├─ Sse/SseEventReader.cs         # SSE 解析（internal）
│  └─ ToolCallArguments.cs          # tool_calls 参数流状态机（internal）
├─ Providers/
│  ├─ OpenAiProvider.cs             # OpenAI 官方 profile
│  ├─ DeepseekProvider.cs           # DeepSeek profile
│  ├─ XaiProvider.cs                # xAI/Grok profile
│  ├─ MoonshotProvider.cs           # Moonshot/Kimi（intl/CN）
│  ├─ ZaiProvider.cs                # Z.ai/智谱（z.ai/coding-cn）
│  ├─ QwenTokenPlanProvider.cs      # Qwen Token Plan（intl/CN/individual）
│  ├─ XiaomiProvider.cs             # 小米（base/variants）
│  ├─ GroqProvider.cs               # Groq
│  ├─ ModelCatalog.cs               # 内嵌模型目录加载
│  └─ ModelData/
│     ├─ openai.json
│     ├─ xai.json
│     ├─ deepseek.json
│     ├─ moonshotai.json / moonshotai-cn.json
│     ├─ zai.json / zai-coding-cn.json
│     ├─ qwen-token-plan*.json
│     ├─ xiaomi.json
│     └─ groq.json
├─ Providers/Auth/AuthHelpers.cs    # D14：EnvApiKeyAuth / LazyOAuthAuth
└─ Types/ClientIdentityHelpers.cs   # D14：identity 头生成
```

> `Api/` 与 `Providers/` 的边界：`Api` 只认识 wire 协议和通用类型；`Providers` 只负责 profile、auth、models、baseUrl。provider profile 不写 SSE/JSON 解析。

---

## 5. 公共形状

### 5.1 OpenAI Completions options

```csharp
namespace PiUnityAgent.Ai.Api;

public sealed record OpenAiCompletionsOptions : StreamOptions
{
    /// pi OpenAICompletionsOptions.toolChoice 的 native 形状；C# 不做 SDK union。
    public JsonNode? ToolChoice { get; init; }

    /// pi reasoningEffort；值域与 ThinkingLevel 一致。
    public ThinkingLevel? ReasoningEffort { get; init; }

    /// pi ThinkingBudgets：minimal/low/medium/high。
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}
```

### 5.2 OpenAI Responses options

```csharp
public enum OpenAiReasoningSummary
{
    Auto,
    Detailed,
    Concise
}

public sealed record OpenAiResponsesOptions : StreamOptions
{
    public ThinkingLevel? ReasoningEffort { get; init; }
    public OpenAiReasoningSummary? ReasoningSummary { get; init; }

    /// pi serviceTier 的 native 字符串（flex/priority/fast/default/auto）。
    public string? ServiceTier { get; init; }

    /// pi toolChoice 的 native 形状。
    public JsonNode? ToolChoice { get; init; }
}
```

### 5.3 协议入口

```csharp
public static class OpenAiCompletions
{
    public static ProviderStreams Streams();
    public static IAssistantMessageEventStream Stream(
        Model model, TranscriptContext context, OpenAiCompletionsOptions? options = null);
    public static IAssistantMessageEventStream StreamSimple(
        Model model, TranscriptContext context, SimpleStreamOptions? options = null);
}

public static class OpenAiResponses
{
    public static ProviderStreams Streams();
    public static IAssistantMessageEventStream Stream(
        Model model, TranscriptContext context, OpenAiResponsesOptions? options = null);
    public static IAssistantMessageEventStream StreamSimple(
        Model model, TranscriptContext context, SimpleStreamOptions? options = null);
}
```

`ProviderStreams` 的 `StreamFn` 仍接收 `StreamOptions?`；`Streams()` 内部把 `OpenAiCompletionsOptions`/`OpenAiResponsesOptions` 的附加字段按类型读取，不改变 S3 的委托形状。

### 5.4 Provider profiles

```csharp
public static class OpenAiProvider
{
    public static IProvider Create();
}

public static class XaiProvider
{
    public static IProvider Create();
}

public static class DeepseekProvider
{
    public static IProvider Create();
}

public static class MoonshotProvider
{
    public static IProvider Create();      // api.moonshot.ai
    public static IProvider CreateCn();    // api.moonshot.cn
}

public static class ZaiProvider
{
    public static IProvider Create();       // api.z.ai
    public static IProvider CreateCodingCn(); // open.bigmodel.cn
}

public static class QwenTokenPlanProvider
{
    public static IProvider Create();             // ap-southeast-1
    public static IProvider CreateCn();           // cn-beijing
    public static IProvider CreateIndividual();   // individual
}

public static class XiaomiProvider
{
    public static IProvider Create();       // api.xiaomimimo.com
}

public static class GroqProvider
{
    public static IProvider Create();
}
```

通用 OpenAI-compatible 不新增公共 provider 类：使用 `ProviderFactory.Create` + `OpenAiCompletions.Streams()` + `EnvApiKeyAuth` + 模型数据；具体 recipe 写入教学篇/使用文档。若后续需要便捷工厂，再登记为 D。

---

## 6. 协议行为

### 6.1 OpenAI Completions

请求：

- `POST {BaseUrl}/chat/completions`
- `Content-Type: application/json`，`Accept: text/event-stream`
- Auth：`Authorization: Bearer <ApiKey>`（若 headers 未显式提供；`null` 抑制默认头）
- Body：
  - `model = model.Id`
  - `messages = ConvertMessages(transcript)`
  - `stream = true`
  - `stream_options.include_usage = true`（compat 允许时）
  - `max_tokens` / `max_completion_tokens` 由 compat 决定
  - `tools` / `tool_choice`（有工具时）
  - `reasoning` / `reasoning_effort` / `reasoning_content` 等由 compat 选择
  - `prompt_cache_key` / `prompt_cache_retention` 由 D6 映射
- `options.OnPayload` 可在发送前改写 JsonObject。
- `options.OnResponse` 在 HTTP 响应建立后、SSE 读取前调用。

响应：

1. `start`
2. `text_start/delta/end`
3. `thinking_start/delta/end`（reasoning 字段先于 text）
4. `toolcall_start/delta/end`（`function.arguments` 片段拼装）
5. `done` 或 `error`（唯一终态）

关键映射：

- `choice.delta.content` → text；
- `reasoning_content` / `reasoning` / `reasoning_text` → thinking；
- `tool_calls[].index` → 内容块索引；id/name 可后到；
- `choice.finish_reason` → `StopReason`（stop/length/toolUse/error）；
- `chunk.usage` 或 `choice.usage` → `Usage` + `ModelOperations.CalculateCost`；
- `[DONE]` 只表示流结束，不单独产生事件。

错误：

- 非 2xx：读取错误体，终止为 `StopReason.Error`，携带 status/errorMessage；
- SSE 解析失败/流提前结束/无 finish_reason：按 compat 决定 stop/toolUse 或 Error；
- 取消：`StopReason.Aborted`，保留已累积的 partial；
- 首字节前网络错误：`ProviderRetry` 可重试；流开始后不重试。

### 6.2 OpenAI Responses

请求：

- `POST {BaseUrl}/responses`
- Body 使用 input items（不是 chat messages）；
- tools / tool_choice / reasoning / service_tier / prompt cache 由 compat；
- `store = false`；
- ChatGPT sign-in 的限制字段 S4 不实现（D1/S5）；
- `OnPayload` / `OnResponse` 语义同 completions。

响应：

- 使用 `processResponsesStream` 等价状态机：
  - response output items → text/thinking/toolCall；
  - function call item 的 arguments 增量 → `ToolCallDelta`；
  - completion/stop → `Done`/`Error`；
  - usage/service tier → `Usage`/cost；
- 事件顺序必须与 completions 的 `start/text/thinking/toolcall/done` 形状一致；
- Responses 的 item 语义不直接暴露给调用方，统一翻译成 S2 事件。

---

## 7. Provider profiles 与模型数据

| Profile | BaseUrl | Auth | API | 首批模型数据 |
|---|---|---|---|---|
| `OpenAiProvider` | `https://api.openai.com/v1` | `EnvApiKeyAuth("OpenAI API key", ["OPENAI_API_KEY"])` | `OpenAiResponses.Streams()` | `openai.json` 子集 |
| `XaiProvider` | `https://api.x.ai/v1` | `EnvApiKeyAuth("xAI API key", ["XAI_API_KEY"])` | `OpenAiResponses.Streams()` | `xai.json` 子集 |
| `DeepseekProvider` | `https://api.deepseek.com` | `EnvApiKeyAuth("DeepSeek API key", ["DEEPSEEK_API_KEY"])` | `OpenAiCompletions.Streams()` | `deepseek.json` 子集 |
| `MoonshotProvider.Create` | `https://api.moonshot.ai/v1` | `EnvApiKeyAuth("Moonshot API key", ["MOONSHOT_API_KEY"])` | `OpenAiCompletions.Streams()` | `moonshotai.json` 子集 |
| `MoonshotProvider.CreateCn` | `https://api.moonshot.cn/v1` | 同上 | 同上 | `moonshotai-cn.json` 子集 |
| `ZaiProvider.Create` | `https://api.z.ai/api/coding/paas/v4` | `EnvApiKeyAuth("Z.AI API key", ["ZAI_API_KEY"])` | `OpenAiCompletions.Streams()` | `zai.json` 子集 |
| `ZaiProvider.CreateCodingCn` | `https://open.bigmodel.cn/api/coding/paas/v4` | `EnvApiKeyAuth(..., ["ZAI_CODING_CN_API_KEY"])` | 同上 | `zai-coding-cn.json` 子集 |
| `QwenTokenPlanProvider.Create` | `https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1` | `EnvApiKeyAuth(..., ["QWEN_TOKEN_PLAN_API_KEY"])` | `OpenAiCompletions.Streams()` | `qwen-token-plan.json` 子集 |
| `QwenTokenPlanProvider.CreateCn` | `https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1` | `EnvApiKeyAuth(..., ["QWEN_TOKEN_PLAN_CN_API_KEY"])` | 同上 | `qwen-token-plan-cn.json` 子集 |
| `QwenTokenPlanProvider.CreateIndividual` | `https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1` | `EnvApiKeyAuth(..., ["QWEN_TOKEN_PLAN_API_KEY"])` | 同上 | `qwen-token-plan-individual.json` 子集 |
| `XiaomiProvider.Create` | `https://api.xiaomimimo.com/v1` | `EnvApiKeyAuth("Xiaomi API key", ["XIAOMI_API_KEY"])` | `OpenAiCompletions.Streams()` | `xiaomi.json` 子集 |
| `GroqProvider.Create` | `https://api.groq.com/openai/v1` | `EnvApiKeyAuth("Groq API key", ["GROQ_API_KEY"])` | `OpenAiCompletions.Streams()` | `groq.json` 子集 |

> 小米 token-plan / Qwen individual 的具体 baseUrl 与 env 名以 pi profile 为准；S4 先落 base profile，区域变体作为同一 family 的工厂方法与数据。

- 模型数据以 `ProviderModel` JSON 内嵌资源加载；首版只收 S4 实际要跑的 chat 模型；
- 不从 pi 全量目录复制；D9 验收是“模型可查、cost/context/temperature 可用”，不是 40 家目录全量；
- profile 不包含 OAuth 行为；OpenAI/xAI OAuth 属 D1/S5；
- 通用 OpenAI-compatible：`ProviderFactory.Create` 的 recipe，不是新公共类型。

---

## 8. Auth / identity / cache / retry / timeout / cancel

### 8.1 D14 Auth helpers 与 identity

- `EnvApiKeyAuth(name, envVars)`：stored key 优先 → 按序取第一个非空 env；返回 `AuthResult`；
- `LazyOAuthAuth(name, load)`：首次 login/refresh/toAuth 懒加载；S4 只保证 wrapper 形状与加载语义，真实 OAuth 行为 D1/S5；
- `ClientIdentityHelpers`：
  - `FormatUserAgent` / `GetClientVersion`；
  - `GetAttributionHeaders`（OpenRouter/NVIDIA/OpenCode）；
  - `GetIdentityHeaders`（User-Agent + attribution）；
  - `ClientIdentityTarget`（provider/baseUrl）。
- OpenAI/xAI profile 使用 API Key；OAuth 与 ChatGPT sign-in 后置。

### 8.2 D6 cache

- `CacheRetention`（none/short/long）映射到：
  - completions：`prompt_cache_key` / `prompt_cache_retention`；
  - responses：`prompt_cache_key` / `prompt_cache_retention` / `prompt_cache_options`；
- usage 的 `cacheRead` / `cacheWrite` 按厂商字段映射；
- 无 cache 字段时保持 0，不伪造。

### 8.3 D2 动态目录刷新

- `Models.RefreshAsync` 走 `IRefreshableProvider` / `IModelsStore`；
- `ProviderFactory` 产物在 `FetchModels` 给出时实现 `IRefreshableProvider`；
- S4 首版 profile 用静态目录；D2 用 synthetic provider 做行为验收；
- `ModelsPublication.Persist/Delete` 二选一在发布路径验证。

### 8.4 D12/D13 取消与超时

- `StreamOptions.CancellationToken` 贯穿请求建立、SSE 读取、重试 sleep；
- `TimeoutMs` 映射到 `FetchOptions.ConnectTimeout/FirstByteTimeout/IdleTimeout`（统一值，Host 可覆盖）；
- `Fetch` 为 null → provider 产出终态 Error（“未配置通讯层”），不抛出；
- 首字节前失败可重试；流开始后失败只产生终态 Error。

---

## 9. 测试与验收

### 9.1 协议级 MockFetch

每种协议至少覆盖：

1. 正常文本流；
2. thinking/reasoning 流（先 thinking 后 text）；
3. tool_calls：参数分片、乱序 index、id/name 后到；
4. usage/cost（含 cache read/write）；
5. `finish_reason` 全映射（stop/length/toolUse/未知/缺失）；
6. HTTP 非 2xx + 错误体；
7. SSE 畸形/提前结束；
8. 取消（`Aborted`，保留 partial）；
9. 首字节前重试与超限；
10. `OnPayload` / `OnResponse` 调用。

### 9.2 Provider profile 测试

- 首批 8 家 family：`OpenAiProvider` / `XaiProvider` / `DeepseekProvider` / `MoonshotProvider` / `ZaiProvider` / `QwenTokenPlanProvider` / `XiaomiProvider` / `GroqProvider` 的 id、baseUrl、auth、models；
- 各 CN/区域变体工厂方法可用；
- `GetAvailable*` 能过滤未配置 auth；
- 通用 OpenAI-compatible recipe 测试；
- 模型数据可查，cost/context/maxTokens 不丢。

### 9.3 门禁

- `dotnet build PiUnityAgent.slnx -c Release --nologo`：0 警告 / 0 错误；
- `dotnet test tests/PiUnityAgent.Ai.Tests/...`：全绿；
- `python scripts/check-deferred.py --root . --current-stage S4 --gate exit`：OK；
- `grep UnityEngine|UnityLlmTransport|GameData|Harmony|HttpClient src/PiUnityAgent.Ai`：零命中；
- 独立 subagent 偏离审计：非 FAIL。

---

## 10. 三方偏离表（S4 必附）

| 项 | pi 原版 | pi.net | 我们 | 类别 | 原因/代价 |
|---|---|---|---|---|---|
| HTTP/SSE 客户端 | OpenAI/Anthropic SDK 内置 fetch | HttpClient + 手写 SSE | Fetch 端口 + 手写 HTTP/SSE | A/B | Ai 不引用 Unity/通讯组件；自行维护 wire 语义 |
| SSE 解析 | SDK/vendor | `AiSse` | `Api/Sse` 自写 | B/D | 不依赖 Transport；需自测多行/事件类型 |
| `OpenAiCompletionsOptions.ToolChoice` | SDK union | JsonNode | JsonNode | B | C# 无 union；保留 native wire |
| `OpenAiResponsesOptions` | SDK union | 字符串/JsonNode | `ThinkingLevel`/enum/string/JsonNode | B | 值域与 pi 对齐，表达 C# 化 |
| Responses input items | `convertResponsesMessages` | JsonObject 手写 | 同语义 | B | 事件层仍翻译成 S2 事件 |
| provider profile | 45+ provider 定义 | 50 文件 | 首批 8 家 family（含 CN/区域变体） | C/D9 | 覆盖收敛；其余 profile 增量接 |
| 模型数据 | pi 全量 models | JSON 全量 | 首批子集 | C/D9 | 只做 S4 实际模型；后续补数据 |
| tool_calls 参数流 partial 快照 | `StreamingJsonParser` 容错部分解析，delta 间 `block.arguments` 显示部分 JSON | 同 | 仅原始文本累积（Task 4 `ToolCallArguments` 最小端口），块 Arguments 在 ToolCallEnd/output_item.done 才成完整 JsonObject | C | Task 4 已定型的最小基础设施；`ToolCallDelta` 仍携带原始文本增量，进度在 delta 流上可见（Task 5/6） |
| 非法工具参数 | `parseToolCallArguments` 永不抛 → `argumentsError` 落块 + 正常 Done | 同 | catch `ToolCallArguments.Finish()` 的 JsonException → ArgumentsError + 正常 Done | B | 按 pi 真实语义实现（Task 5/6 各有聚焦用例）；Task 4 旧注释语义以本表为准 |
| StreamSimple 缺 key 时机 | 同步 throw（返回流之前） | 同 | 全部进流内 catch → 终态 Error | B | 全局约束"provider 流不得抛出"；代价=StreamSimple 不再同步报缺 key（Task 5/6） |
| D2 刷新并发模型 | `Promise.all` 并发 + per-provider generation/AbortController 去抖；`structuredClone(persist)` 落库 | 同 pi 语义 | 顺序 for + 单 CancellationToken，无 generation 去抖；publication 原引用落库 | C/D | Unity netstandard2.1 顺序化收益/复杂度权衡；`ModelsStoreEntry` 为 init-only record 无别名突变面（Task 8A `24e4d80`） |
| 过期 stored OAuth 刷新凭据 | `refreshStoredOAuthCredential` 轮换 + 持久化 | 同 | 过期视为无有效凭据并跳过网络阶段；`TODO(S5,D1)` 锚落码 | C/D1 | OAuth 轮换归 D1/S5；代价=过期 OAuth 的 provider 不自动刷目录（Task 8A） |
| Hosts.Net8 Fetch 适配器 | SDK fetch | HttpClient + 手写 SSE | `UnityLlmTransportFetch`（`IStreamingHttpTransport`→Fetch 端口）+ `HttpClientFetch` | B | 唯一 transport 缝在 Hosts；Ai 层零 transport 引用；Unity 运行时 link.xml/防裁剪配置未 vendored（S13 落地时补） |
| ModelCatalog JSON 字段口径 | `models.generated.ts` 编译期数据 | `ModelData/*.json` | JSON 内 `provider` 字段优先于目录 key（plan 钉死）；条目缺 `type` 缺省 chat | B | 与 pi.net ModelCatalog 兼容；`chat:` 前缀 + `type != chat` 双保险过滤（Task 7） |
| 协议适配器辅助代码重复 | — | — | `OpenAiCompletions.cs`/`OpenAiResponses.cs` 内联重复约 1300 行私有辅助 | D/TODO | 收编债务：后续以一次性 mechanical 提取下沉 `Api/OpenAiShared.cs`（§4 预留位）；不得再造第三份拷贝（Task 6 报告遗留 #1） |
| OAuth/ChatGPT sign-in | openai/xai OAuth | OAuth 实现 | API Key 先行 | C/D1 | OAuth 行为 S5；ChatGPT sign-in 限制字段（isChatGPTSignIn/omitUnsupportedFields/订阅限额文案）不实现（Task 6） |
| Anthropic/Google | S5 | S5 | S4 不做 | C | S5 另立 spec |
| 全局 ApiRegistry | `compat.registerApiProvider` | `ApiRegistry` | 不新增；ProviderFactory 分派 | C/D | 避免重复分派层；动态注册后续按需 |
| `retryAssistantCall` | 上层 agent 重试 | 上层 | S4 不实现 | C | S4 只做 provider 首字节前重试 |
| transcript 完整重放 | S9 | S9 | S4 只 normalize | C/D11 | S9 承接 D11 |
| 图片/分类 | S4/S5 范围外 | 有 | 不做 | C/D4/D5 | 2B 后置 |

> 本表是**待审计声明**，不是真相源；S4 审计必须回到 pi/pi.net 源码核对。

---

## 11. 风险

1. **SSE 状态机**：多行 data、event、`[DONE]`、畸形流、提前结束；必须用 MockFetch 覆盖。
2. **tool_calls 参数流**：index/id/name 乱序、JSON 不完整、空参数；不能用“最终对象”假设。
3. **Responses API 复杂度**：item/tool/reasoning 与 completions 不同；先按 pi `openai-responses-shared` 结构拆函数。
4. **compat 矩阵**：DeepSeek/OpenAI/xAI 的 reasoning/cache/max tokens 差异不能写死在 profile。
5. **模型数据**：只收首批模型，但必须可查、cost/context 正确；后续增量补。
6. **错误/取消**：必须编码为终态，不得抛出；流开始后不重试。
7. **Dn 承接**：S4 plan 第一任务必须把 D2/D6/D9/D10/D12/D13/D14 转成验收测试。

---

## 12. 实施顺序（进入 plan 时）

1. **S4-0 承接 Dn**：D2/D6/D9/D10/D12/D13/D14 → 验收测试；建立 `Api/`、模型数据、Auth helpers、identity helpers。
2. **S4-1 基础设施**：SSE reader、tool args 状态机、ProviderRetry、FetchOptions/错误映射。
3. **S4-2 OpenAI Completions**：BuildParams/ConvertMessages/ConvertTools/stream 状态机/usage/stop。
4. **S4-3 OpenAI Responses**：input items/tools/reasoning/response stream；复用事件形状。
5. **S4-4 profiles/catalog**：首批 8 家 family（OpenAI/xAI/DeepSeek/Moonshot/Z.ai/Qwen/Xiaomi/Groq）+ 模型数据 + `ProviderFactory` 接线；区域变体用同一 family 工厂方法。
6. **S4-5 测试/文档/审计**：MockFetch 全矩阵、spec §10 更新、独立偏离审计、门禁。

---

## 13. 版本记录

| 日期 | 变更 |
|---|---|
| 2026-11-08 | 初版：S4 OpenAI 家族（completions + responses）范围、协议适配、首批 profile、Dn 承接、偏离表与验收 |
| 2026-11-08 | 首批 profile 扩到 8 家 family（OpenAI/xAI/DeepSeek/Moonshot/Z.ai/Qwen/小米/Groq，含 CN/区域变体）；仍不增加协议适配器 |
