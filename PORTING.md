# PORTING · 上游 pi → PiUnityAgent 映射

> **用途**：逐文件追踪移植状态；每次同步参考源后更新版本卡与状态。
> **行为真相源**：最新原版 pi（TypeScript）。
> **C# 语言映射参考**：Pi.NET（`pi-study/pi.net`），只参考语言写法，不参考业务语义。
> **状态图例**：⬜ 待做 / 🟡 子集或改编 / ✅ 已移植并验证 / ⛔ 不移植。

---

## 0. 版本卡（每个模块/批次开工前更新）

| 参考 | 本地版本 | commit | 日期 | 备注 |
|---|---|---|---|---|
| 原版 pi | `v1.5.0` | `f9a263655` | 2026-11-02 | 已同步；v1.3.0 → v1.5.0 共 389 commits |
| Pi.NET | `main`（无新 tag，最新 tag `v1.4.5`） | `2d6757b` | 2026-11-05 | `v1.4.5` 之后 40 commits；其 docs 对齐上游 v1.5.0 |
| UnityLlmTransport | Phase 0–6 完成（`VERSION 0.6.0`） | `4c0cc4b` | 2026-11-08 | 通讯底座；B 方案下由 Host 适配器消费；UWR/HttpClient/WebSocket/Auth/SSE/Abstractions；Phase 2 四版本 × Mono/IL2CPP 8 组 15/15（含 2022.3.62f3c1）；正式打包另行评估 |

**上游增量（v1.3.0 → v1.5.0，M1 相关）**：

- `packages/agent/src`：4 个文件变更（`agent-loop.ts`、`agent.ts`、`proxy.ts`、`types.ts`），+50/−16 行。
- `packages/ai`：`types.ts`、`utils/transcript.ts`、`utils/retry.ts`、`providers/faux.ts`、`api/anthropic-messages.ts`、`api/openai-completions.ts` 均有变更。
- **M1 开工前按 v1.5.0 重读上述文件**，不要沿用 v1.3.0 的印象。

同步命令见 `sop/02-参考源同步-2026-11-05.md`。

---

## 1. M1 · PiUnityAgent.Ai（pi-ai 子集）

> **S1/S2/S3 状态（2026-11-08）**：S1 消息/内容、S2 事件流、S3 Provider/Faux 初版已合 `main`；S3 对齐返工 v2（IProvider/Fetch 端口/Auth 类型/LazyStream）已合 `main`；S3 v3 偏离补齐（Auth 全套形状/API Key 路径/ModelsError/ProviderFactory/ClientIdentity/Observer/Options 严格化）已在 `feature/s3-v3-deviation` 完成，独立审查 PASS 后合 `main`。详见 `docs/learning/S1-*`、`S2-*`、`S3-*` 与 v2 补充篇（含 v3 段）。

| 上游文件 | 我们的文件 | 状态 | 偏离说明 |
|---|---|---|---|
| `packages/ai/src/types.ts` | `src/PiUnityAgent.Ai/Types/*.cs` | ✅ | S1 消息/内容类型、S2 事件、S3 Model/选项均已落地；S3 返工 v2 补 `FetchFunction` 端口与形状对齐；v3 补 `StreamOptions.TransformHeaders/OnPayload`、`ClientIdentity`（UserAgentMode/Attribution/Version）、`ImagesOptions`/`ClassifierOptions`，删 `StreamOptions.BaseUrl/StopSequences` |
| `packages/ai/src/utils/event-stream.ts` | `src/PiUnityAgent.Ai/Events/*` | ✅ | S2 完成：12 事件 + EventStream + AssistantMessageEventStream；单消费者 |
| `packages/ai/src/utils/transcript.ts` | `src/PiUnityAgent.Ai/Types/TranscriptNormalization.cs` | ✅ | `normalizeContext` 折叠 system prompt/tools；重放辅助 D11 后置 |
| `packages/ai/src/utils/retry.ts` | `src/PiUnityAgent.Ai/Utils/Retry.cs` | ✅ | S8/D17：助手轮次重试（错误分类/指数退避/回调/取消）；pi 的 agent-core 不消费，调用方 2A |
| `packages/ai/src/utils/estimate.ts` | `src/PiUnityAgent.Ai/Tokens/Estimate.cs` | ⬜ | 粗略 token 估算 |
| `packages/ai/src/utils/abort.ts` | `src/PiUnityAgent.Ai/Reliability/Abort.cs` | ⬜ | 优先用 `CancellationToken` |
| `packages/ai/src/types.ts`（`fetch?`） | `src/PiUnityAgent.Ai/Types/Fetch.cs` | ✅ | S3 返工 v2：自有 `FetchFunction` 端口（B 方案）；Host 适配器在 S13/S4 |
| `packages/ai/src/api/openai-completions.ts` | `src/PiUnityAgent.Ai/Api/OpenAiCompletions.cs` | ⬜ | OpenAI-compatible / DeepSeek |
| `packages/ai/src/api/anthropic-messages.ts` | `src/PiUnityAgent.Ai/Api/AnthropicMessages.cs` | ⬜ | 保留 cache/thinking 语义 |
| `packages/ai/src/providers/faux.ts` | `src/PiUnityAgent.Ai/Providers/Faux/*` | ✅ | S3 Faux：脚本队列/确定性分块；Deferred 行为 S8/D3 已完成（见 3C） |
| `packages/ai/src/models.ts` | `src/PiUnityAgent.Ai/Providers/Models.cs` | ✅ | S3 v3：门面形状补齐（ModelsError/FilterAllModels/transformHeaders/observer 挂点/CreateModelsOptions）+ API Key 认证/可用性路径；刷新 D2（S4）与 Deferred D3（S8）已完成；图片 D4、分类 D5 后置（C） |
| `packages/ai/src/index.ts` | — | ⛔ | C# 不建 barrel |
| `packages/ai/src/auth/**`、`oauth*.ts` | `src/PiUnityAgent.Ai/Providers/Auth/` | ✅ | S3 v3 形状：ModelAuth/AuthResult（ModelAuth+Env+Source）/AuthCheck（Type 必填）/CredentialStore（Read/List/Modify/Delete；modify null=不变）/IAuthContext/ApiKeyAuth/OAuthAuth（ToAuth，无 Logout）；OAuth 行为 C 后置 D1/D7 |
| `packages/ai/src/images*`、`classify*` | `src/PiUnityAgent.Ai/Types/ImagesTypes.cs`、`ClassifierTypes.cs`、`Providers/ProviderCapabilities.cs`、`Providers/ProviderStreams.cs` | 🟡 | 类型 + 能力接口（`IImagesProvider`/`IClassifierProvider`）+ `ProviderImages`/`ProviderClassifier` + `ImagesOptions`/`ClassifierOptions`；`Models` stub；真实实现 D4/D5 后置（C） |
| `packages/ai/src/api/vertex-ai.ts`、`bedrock*`、`mistral*`、`google*` | — | ⛔ | 非目标 provider |

---

## 2. M1 · PiUnityAgent.Core（pi-agent-core）

| 上游文件 | 我们的文件 | 状态 | 偏离说明 |
|---|---|---|---|
| `packages/agent/src/agent-loop.ts` | `src/PiUnityAgent.Core/AgentLoop.cs` | ⬜ | 主循环/turn/工具执行 |
| `packages/agent/src/agent.ts` | `src/PiUnityAgent.Core/Agent.cs` | ⬜ | 状态/双队列/订阅 |
| `packages/agent/src/stream-fn.ts` | `src/PiUnityAgent.Core/StreamFn.cs` | ⬜ | 委托 + `IAsyncEnumerable` |
| `packages/agent/src/proxy.ts` | `src/PiUnityAgent.Core/Proxy.cs` | ⬜ | 可后置，先保留接口 |
| `packages/agent/src/types.ts` | `src/PiUnityAgent.Core/Types/*.cs` | ⬜ | 一类型一文件 |
| `packages/agent/src/index.ts` | — | ⛔ | C# 不建 barrel |

---

## 3. 2A · PiUnityAgent.Runtime（coding-agent 机制）

| 上游文件 | 我们的文件 | 状态 | 偏离说明 |
|---|---|---|---|
| `packages/coding-agent/src/core/session-manager.ts` | `src/PiUnityAgent.Runtime/Session/SessionManager.cs` | ⬜ | JSONL 树/branch/fork |
| `packages/coding-agent/src/core/agent-session.ts` | `src/PiUnityAgent.Runtime/AgentSession.cs` | 🟡 | 只取会话编排，不取 CLI |
| `packages/coding-agent/src/core/system-prompt.ts` | `src/PiUnityAgent.Runtime/Context/SystemPrompt.cs` | ⬜ | 分段 prompt |
| `packages/coding-agent/src/core/compaction/compaction.ts` | `src/PiUnityAgent.Runtime/Compaction/Compaction.cs` | ⬜ | 阈值/摘要/保留 |
| `packages/coding-agent/src/core/compaction/branch-summarization.ts` | `src/PiUnityAgent.Runtime/Compaction/BranchSummarization.cs` | ⬜ | 分支摘要 |
| `packages/coding-agent/src/core/skills.ts` | `src/PiUnityAgent.Runtime/Skills/Skills.cs` | ⬜ | SKILL.md 发现/按需加载 |
| `packages/coding-agent/src/core/prompt-templates.ts` | `src/PiUnityAgent.Runtime/Prompts/PromptTemplates.cs` | ⬜ | 文件式模板 |
| `packages/coding-agent/src/core/extensions/types.ts` | `src/PiUnityAgent.Runtime/Extensions/ExtensionTypes.cs` | ⬜ | 事件/工具/命令契约 |
| `packages/coding-agent/src/core/extensions/runner.ts` | `src/PiUnityAgent.Runtime/Extensions/ExtensionRunner.cs` | ⬜ | 生命周期/派发 |
| `packages/coding-agent/src/core/extensions/loader.ts` | `src/PiUnityAgent.Runtime/Extensions/ExtensionLoader.cs` | 🟡 | Unity 编译期注册；后端 ALC |
| `packages/coding-agent/src/core/resource-loader.ts` | `src/PiUnityAgent.Runtime/Resources/ResourceLoader.cs` | ⬜ | 资源发现/诊断 |
| `packages/coding-agent/src/core/package-manager.ts` | `src/PiUnityAgent.Runtime/Resources/PackageManager.cs` | 🟡 | 简化为文件夹/DLL 包 |
| `packages/coding-agent/src/core/tools/**` | — | ⛔ | bash/read/write/edit/grep/find 不搬 |
| `packages/coding-agent/src/cli*`、`modes/**`、`tui/**` | — | ⛔ | 不做 CLI/TUI |

---

## 3A. S6 工具契约补充映射（2026-11-09）

| pi 源 | 我们的落点 | 状态 |
|---|---|---|
| `packages/agent/src/types.ts`（工具段 `AgentTool/AgentToolResult/AgentToolUpdateCallback`） | `src/PiUnityAgent.Core/Tools/*` | ✅ S6 |
| `packages/ai/src/utils/validation.ts` | `src/PiUnityAgent.Ai/Utils/ToolValidation.cs` | ✅ S6 |
| `packages/schema/src/*`（compiler/coerce/checks/explain/formats/resolver） | `src/PiUnityAgent.Ai/Utils/Schema/*`（internal 子集） | 🟡 S6（D21 缺三项） |

---

## 3B. S7 Agent Loop 补充映射（2026-11-09）

| pi 源 | 我们的落点 | 状态 |
|---|---|---|
| `packages/agent/src/agent-loop.ts` | `src/PiUnityAgent.Core/Agent/*` | ✅ S7 |
| `packages/agent/src/stream-fn.ts` | `src/PiUnityAgent.Core/Agent/DefaultStreamFn.cs` | ✅ S7 |
| `packages/agent/src/proxy.ts` | —（D22） | 🟡 后续扩展 |
| `packages/ai/src/utils/streaming-json.ts`、`json-parse.ts` | `src/PiUnityAgent.Ai/Utils/{StreamingJson,JsonParse}.cs` | ✅ S7/D16 |

---

## 3C. S8 Agent 门面补充映射（2026-11-09）

| pi 源 | 我们的落点 | 状态 |
|---|---|---|
| `packages/agent/src/agent.ts` | `src/PiUnityAgent.Core/Agent/Agent.cs` + `Agent.Run.cs` + `Agent.Queues.cs` | ✅ S8 |
| `packages/ai/src/utils/retry.ts` | `src/PiUnityAgent.Ai/Utils/Retry.cs` | ✅ S8/D17 |
| `packages/ai/src/models.ts`（deferred 段） | `src/PiUnityAgent.Ai/Providers/Models.Capabilities.cs` | ✅ S8/D3 |
| `packages/ai/src/providers/faux.ts`（deferred 段） | `src/PiUnityAgent.Ai/Providers/Faux/FauxProvider.cs` | ✅ S8/D3 |

---

## 4. 偏离登记规则

1. 任何**改名、拆分、裁剪**都写进本表"偏离说明"，不静默处理。
2. 语义偏离必须同时写进 `docs/decisions/` 决策日志。
3. 上游新增文件：先判断是否在范围内；不在范围标 ⛔，在范围标 ⬜。
4. 每个模块完成时，把相关行从 ⬜/🟡 更新为 ✅，并附验证方式（单测/对照）。
