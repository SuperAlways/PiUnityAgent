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

**上游增量（v1.3.0 → v1.5.0，M1 相关）**：

- `packages/agent/src`：4 个文件变更（`agent-loop.ts`、`agent.ts`、`proxy.ts`、`types.ts`），+50/−16 行。
- `packages/ai`：`types.ts`、`utils/transcript.ts`、`utils/retry.ts`、`providers/faux.ts`、`api/anthropic-messages.ts`、`api/openai-completions.ts` 均有变更。
- **M1 开工前按 v1.5.0 重读上述文件**，不要沿用 v1.3.0 的印象。

同步命令见 `sop/02-参考源同步-2026-11-05.md`。

---

## 1. M1 · PiUnityAgent.Ai（pi-ai 子集）

> **S1 状态（2026-11-05）**：✅ 消息/内容/usage/stopReason + deferred/diagnostics/images/classifier/nested 类型已按 spec 返工完成（70 条单测）；事件流与 Model 目录待 S2+。详见 `docs/learning/S1-消息与内容模型.md`。

| 上游文件 | 我们的文件 | 状态 | 偏离说明 |
|---|---|---|---|
| `packages/ai/src/types.ts` | `src/PiUnityAgent.Ai/Types/*.cs` | ✅ | S1 按 spec 返工完成：MessageContent、ContentBlock 多态、required、枚举 converter、可选类型；事件/Model 待 S2+ |
| `packages/ai/src/utils/event-stream.ts` | `src/PiUnityAgent.Ai/Events/*` | ✅ | S2 完成：12 事件 + EventStream + AssistantMessageEventStream；单消费者 |
| `packages/ai/src/utils/transcript.ts` | `src/PiUnityAgent.Ai/Context/Transcript.cs` | ⬜ | `normalizeContext` 保留语义 |
| `packages/ai/src/utils/retry.ts` | `src/PiUnityAgent.Ai/Reliability/Retry.cs` | ⬜ | 指数退避 + 抖动 |
| `packages/ai/src/utils/estimate.ts` | `src/PiUnityAgent.Ai/Tokens/Estimate.cs` | ⬜ | 粗略 token 估算 |
| `packages/ai/src/utils/abort.ts` | `src/PiUnityAgent.Ai/Reliability/Abort.cs` | ⬜ | 优先用 `CancellationToken` |
| `packages/ai/src/api/openai-completions.ts` | `src/PiUnityAgent.Ai/Api/OpenAiCompletions.cs` | ⬜ | OpenAI-compatible / DeepSeek |
| `packages/ai/src/api/anthropic-messages.ts` | `src/PiUnityAgent.Ai/Api/AnthropicMessages.cs` | ⬜ | 保留 cache/thinking 语义 |
| `packages/ai/src/providers/faux.ts` | `src/PiUnityAgent.Ai/Providers/FauxProvider.cs` | ⬜ | **第一优先**：无网测试 |
| `packages/ai/src/models.ts` / `model-catalog.ts` | `src/PiUnityAgent.Ai/Providers/ModelCatalog.cs` | 🟡 | 只保留必要模型元数据 |
| `packages/ai/src/index.ts` | — | ⛔ | C# 不建 barrel |
| `packages/ai/src/auth/**`、`oauth*.ts` | — | ⛔ | 不做 OAuth/CLI 登录 |
| `packages/ai/src/images*`、`classify*` | — | ⛔ | 不做图片/分类 |
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

## 4. 偏离登记规则

1. 任何**改名、拆分、裁剪**都写进本表"偏离说明"，不静默处理。
2. 语义偏离必须同时写进 `docs/decisions/` 决策日志。
3. 上游新增文件：先判断是否在范围内；不在范围标 ⛔，在范围标 ⬜。
4. 每个模块完成时，把相关行从 ⬜/🟡 更新为 ✅，并附验证方式（单测/对照）。
