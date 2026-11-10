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

> **S1–S5 状态（2026-11-10）**：S1 消息/内容、S2 事件流、S3 Provider/Faux（含 v2 返工与 v3 偏离补齐）、S4 OpenAI 双协议、S5 Anthropic/Google 双协议均已合 `main` 并通过独立审计。基表于 2026-11-10 按 M1 审计 U-2 现势订正（四适配器/四公共 util 补行、estimate/abort 行注销、auth 行 D1/D7 刷新）。详见 `docs/learning/S1-*`…`S5-*` 与各阶段 spec `docs/superpowers/specs/`。

| 上游文件 | 我们的文件 | 状态 | 偏离说明 |
|---|---|---|---|
| `packages/ai/src/types.ts` | `src/PiUnityAgent.Ai/Types/*.cs` | ✅ | S1 消息/内容类型、S2 事件、S3 Model/选项均已落地；S3 返工 v2 补 `FetchFunction` 端口与形状对齐；v3 补 `StreamOptions.TransformHeaders/OnPayload`、`ClientIdentity`（UserAgentMode/Attribution/Version）、`ImagesOptions`/`ClassifierOptions`，删 `StreamOptions.BaseUrl/StopSequences` |
| `packages/ai/src/utils/event-stream.ts` | `src/PiUnityAgent.Ai/Events/*` | ✅ | S2 完成：12 事件 + EventStream + AssistantMessageEventStream；单消费者 |
| `packages/ai/src/utils/transcript.ts` | `src/PiUnityAgent.Ai/Types/TranscriptNormalization.cs` | ✅ | `normalizeContext` 折叠 system prompt/tools；重放辅助 D11 后置 |
| `packages/ai/src/utils/retry.ts` | `src/PiUnityAgent.Ai/Utils/Retry.cs` | ✅ | S8/D17：助手轮次重试（错误分类/指数退避/回调/取消）；pi 的 agent-core 不消费，调用方 2A |
| `packages/ai/src/utils/overflow.ts` | `src/PiUnityAgent.Ai/Utils/Overflow.cs` | ✅ | M1 审计 U-S1（2026-11-10）：21 家 provider 溢出文案/静默溢出/length 止步三段判定；pi index.ts:39 导出；设计登记见 `docs/superpowers/specs/2026-11-10-M1-public-utilities-design.md` |
| `packages/ai/src/utils/string-enum.ts` | `src/PiUnityAgent.Ai/Utils/StringEnum.cs` | ✅ | M1 审计 U-S1：`StringEnum.Create(values, description?, default?)` 返回 `JsonObject`（偏离 B）；pi index.ts:42 导出；同上 spec |
| `packages/ai/src/utils/uuid.ts` | `src/PiUnityAgent.Ai/Utils/Uuid.cs` | ✅ | M1 审计 U-S1：`Uuid.V7(long?)` 时间序 UUIDv7（越界/序列耗尽异常类型偏离 A/B）；pi index.ts:45 导出（仅 codex provider 消费）；同上 spec |
| `packages/ai/src/session-resources.ts` | `src/PiUnityAgent.Ai/SessionResources.cs` | ✅ | M1 审计 U-S1：注册表 + `IDisposable` 注销句柄 + `AggregateException` 聚合清理（快照遍历偏离 D）；pi index.ts:33 导出（仅 codex provider 消费）；同上 spec |
| `packages/ai/src/utils/estimate.ts` | 各适配器私有副本（`Api/OpenAiCompletions.cs`/`Api/OpenAiResponses.cs`/`Api/AnthropicMessages.cs` 内 `CharsPerToken=3.5` 估算） | 🟡 | 非 index 公共导出；粗算逻辑以私有副本存在于三适配器，规划路径 `Tokens/Estimate.cs` 不再立项（M1 审计 U-2 注销原 ⬜ 行）；收编为公共 util 归后续扩展 |
| `packages/ai/src/utils/abort.ts` | —（`CancellationToken` 承载） | ⛔ | 非 index 导出；AbortSignal 竞速/操作本地信号由 C# 各入口的 `CancellationToken` 生态映射承载（A），无独立对应物（M1 审计 U-2 注销原 ⬜ 行） |
| `packages/ai/src/types.ts`（`fetch?`） | `src/PiUnityAgent.Ai/Types/Fetch.cs` | ✅ | S3 返工 v2：自有 `FetchFunction` 端口（B 方案）；Host 适配器在 S13/S4 |
| `packages/ai/src/api/openai-completions.ts` | `src/PiUnityAgent.Ai/Api/OpenAiCompletions.cs` | ✅ | S4：请求组装/SSE/text-thinking/tool_calls/usage-cost/错误取消超时（偏离见 S4 spec §10） |
| `packages/ai/src/api/openai-responses.ts` | `src/PiUnityAgent.Ai/Api/OpenAiResponses.cs` | ✅ | S4-C：responses 协议（item/tool/reasoning 事件、input items、共享解析族 `openai-responses-shared.ts` 语义并入） |
| `packages/ai/src/api/anthropic-messages.ts` | `src/PiUnityAgent.Ai/Api/AnthropicMessages.cs` | ✅ | S5-B：messages/tool blocks/thinking/cache（偏离见 S5 spec §10） |
| `packages/ai/src/api/google-generative-ai.ts` | `src/PiUnityAgent.Ai/Api/GoogleGenerativeAi.cs` | ✅ | S5-C：Gemini 请求/响应与事件映射（candidate/usage/stop；无流式参数）；偏离见 S5 spec §10 |
| `packages/ai/src/providers/faux.ts` | `src/PiUnityAgent.Ai/Providers/Faux/*` | ✅ | S3 Faux：脚本队列/确定性分块；Deferred 行为 S8/D3 已完成（见 3C） |
| `packages/ai/src/models.ts` | `src/PiUnityAgent.Ai/Providers/Models.cs` | ✅ | S3 v3：门面形状补齐（ModelsError/FilterAllModels/transformHeaders/observer 挂点/CreateModelsOptions）+ API Key 认证/可用性路径；刷新 D2（S4）与 Deferred D3（S8）已完成；图片 D4、分类 D5 后置（C） |
| `packages/ai/src/index.ts` | — | ⛔ | C# 不建 barrel |
| `packages/ai/src/auth/**`、`oauth*.ts` | `src/PiUnityAgent.Ai/Providers/Auth/` | ✅ | S3 v3 形状：ModelAuth/AuthResult（ModelAuth+Env+Source）/AuthCheck（Type 必填）/CredentialStore（Read/List/Modify/Delete；modify null=不变）/IAuthContext/ApiKeyAuth/OAuthAuth（ToAuth，无 Logout）；OAuth 行为已完成（2026-11-10 现势：D1/D7 S5 已完成——Anthropic copy-code/manual + OpenRouter manual + FileCredentialStore；浏览器 loopback 后置 D20） |
| `packages/ai/src/images*`、`classify*` | `src/PiUnityAgent.Ai/Types/ImagesTypes.cs`、`ClassifierTypes.cs`、`Providers/ProviderCapabilities.cs`、`Providers/ProviderStreams.cs` | 🟡 | 类型 + 能力接口（`IImagesProvider`/`IClassifierProvider`）+ `ProviderImages`/`ProviderClassifier` + `ImagesOptions`/`ClassifierOptions`；`Models` stub；真实实现 D4/D5 后置（C） |
| `packages/ai/src/api/google-vertex.ts`、`azure-openai-responses.ts`、`bedrock-converse-stream.ts`、`mistral-conversations.ts`、`pi-messages.ts` | — | ⛔ | 非目标 provider（暂缓清单见 `docs/05` §2.3；`google-generative-ai` 已由 S5 实现，不再列入本行） |

---

## 2. M1 · PiUnityAgent.Core（pi-agent-core）

> **2026-11-10 现势订正（M1 审计 U-2）**：本表原列的四条路径（`Core/AgentLoop.cs`/`Core/Agent.cs`/`Core/StreamFn.cs`/`Core/Types/*.cs`）不存在，实际落点在 `src/PiUnityAgent.Core/Agent/` 下，状态 S6–S8 已全部完成合 `main`。工具段增量见 §3A，S7/S8 增量见 §3B/§3C。

| 上游文件 | 我们的文件 | 状态 | 偏离说明 |
|---|---|---|---|
| `packages/agent/src/agent-loop.ts` | `src/PiUnityAgent.Core/Agent/AgentLoop.cs` + `AgentLoop.Run.cs` + `AgentLoop.Stream.cs` + `ToolExecution.cs` + `ToolLoadout.cs` | ✅ | S7：双层循环/工具批次/单调用管线/装载公告 |
| `packages/agent/src/agent.ts` | `src/PiUnityAgent.Core/Agent/Agent.cs` + `Agent.Run.cs` + `Agent.Queues.cs` + `AgentState.cs` + `AgentOptions.cs` | ✅ | S8：门面/双队列/abort/订阅/状态归约 |
| `packages/agent/src/stream-fn.ts` | `src/PiUnityAgent.Core/Agent/DefaultStreamFn.cs`（委托形状在 `src/PiUnityAgent.Ai/Types/StreamFn.cs`） | ✅ | S7：全局默认流函数；委托收基类 `StreamOptions?`（窄别名未单列，S7 spec §6 N-1 注） |
| `packages/agent/src/proxy.ts` | —（台账 D22） | 🟡 | server RPC 路线；目标后续扩展 |
| `packages/agent/src/types.ts` | `src/PiUnityAgent.Core/Agent/AgentContext.cs`、`AgentEvent.cs`、`AgentLoopConfig.cs`、`AgentLoopTypes.cs`、`ToolCallHooks.cs`、`AgentToolCallOutcome.cs`、`QueueMode.cs`、`Tools/AgentTool.cs` | ✅ | S6–S8：一类型一文件（工具契约见 §3A） |
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
