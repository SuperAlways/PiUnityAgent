# REFERENCES · pi 教学项目参考索引

> **用途**：把 `pi-study/` 下的教学项目映射到 S0–S13；每个阶段告诉你"先读源码、再读哪一章"。
> **原则**：我们的教学文档是主线；这些项目是**延伸阅读**，不是标准。行为以原版 pi v1.5.0 源码为准。

---

## 一、项目画像

| 项目 | 适合 | 不适合 | 说明 |
|---|---|---|---|
| **pi-bluebook** | 整体使用、概念、案例 | 源码级细节 | 5 模块 14 课 + 8 案例；内容核验到 2026-09，可能落后 |
| **pi-agent-book** | 设计取舍、运行时/上下文/记忆/工具/扩展 | 逐行源码 | 8 章；基准 v0.85.1，路径可能与当前源码不同 |
| **how-pi-agent-works** | 概念 + 源码拆解 + 4 个渐进 demo | 最新 API | 概念最清晰；适合作为 S1–S3、S6–S11 的主参考 |
| **dg-ai-notes** | SDK 实战 + TS/Python 源码精读 | 逐行最新实现 | 7 章 SDK + 10 章源码；可作并行阅读 |
| **pi-book** | 端到端深挖（`Agent.prompt` → `agent_end`） | 全貌（只发布 3 章） | 第 2 章适合 S7 深读 |
| **exercises** | 工具拦截小练习 | 系统课程 | `01-block-tool` 可用于 S6/S8 |
| **Pi-学习路线.md** | 元路线（选读顺序） | 章节内容 | 本索引的"上游说明书" |

---

## 二、S0–S13 参考索引

| 阶段 | 主参考（读 1 篇） | 次参考（按需） | 原版源码 |
|---|---|---|---|
| **S0 总览** | `pi-bluebook/docs/guide/how-pi-works.md` | `pi-agent-book/book/01-first-agent.md`；`how-pi-agent-works/docs/concepts/what-is-agent.md`；`how-pi-agent-works/docs/concepts/pi-architecture.md` | `coding-agent/docs/how-pi-works.md` |
| **S1 消息模型** | `how-pi-agent-works/docs/concepts/message-and-stream.md` | `pi-agent-book/book/02-runtime.md` §2.2；`dg-ai-notes/.../第6章-消息系统...` | `ai/src/types.ts` |
| **S2 流式事件** | `how-pi-agent-works/docs/source/model-protocol.md` | `dg-ai-notes/.../第7章-事件驱动...`；`pi-book/agent/02-end-to-end.md`（事件落地段） | `ai/src/types.ts`、`ai/src/utils/event-stream.ts` |
| **S3 Provider/Faux** | `pi-agent-book/book/02-runtime.md` §2.1 | `how-pi-agent-works/docs/demos/01-loop.md`；`dg-ai-notes/.../第4章-模型调用...` | `ai/src/providers/faux.ts`、`ai/src/models.ts` |
| **S4 OpenAI 兼容** | `pi-bluebook/docs/guide/connect-model.md` | `Pi.NET/docs/usage/01-pi-ai.md`（C# 参考）；`dg-ai-notes/.../第4章` | `ai/src/api/openai-completions.ts`、`utils/transcript.ts`、`utils/retry.ts` |
| **S5 Anthropic** | `pi/packages/ai/README.md`（provider/thinking 段） | `Pi.NET/docs/usage/01-pi-ai.md`；`dg-ai-notes/.../第4章` | `ai/src/api/anthropic-messages.ts` |
| **S6 工具契约** | `how-pi-agent-works/docs/concepts/tools.md` | `how-pi-agent-works/docs/source/tools-extensions.md`；`pi-agent-book/book/05-tools.md`；`exercises/01-block-tool` | `agent/src/types.ts`、`agent/src/agent-loop.ts`（工具段） |
| **S7 Agent Loop** | `how-pi-agent-works/docs/source/agent-loop.md` + `demos/01-loop.md` | `pi-book/agent/02-end-to-end.md`（深读）；`pi-agent-book/book/02-runtime.md` §2.4–2.6；`dg-ai-notes/.../第3章-Agent Loop...` | `agent/src/agent-loop.ts` |
| **S8 队列/钩子** | `pi-agent-book/book/02-runtime.md` §2.7–2.8 | `pi-agent-book/book/06-extensibility.md` §6.2；`how-pi-agent-works/docs/concepts/tools.md`（hooks） | `agent/src/agent.ts`、`agent/src/types.ts` |
| **S9 上下文** | `pi-agent-book/book/03-context-engineering.md` | `how-pi-agent-works/docs/concepts/context.md`；`pi-bluebook/docs/guide/files-and-context.md` | `ai/src/utils/transcript.ts`、`coding-agent/src/core/system-prompt.ts` |
| **S10 会话树** | `how-pi-agent-works/docs/concepts/sessions.md` + `demos/03-session-tree.md` | `pi-agent-book/book/04-memory.md` §4.2–4.3；`dg-ai-notes/.../第10章-会话管理...`；`pi-bluebook/docs/guide/sessions.md` | `coding-agent/src/core/session-manager.ts` |
| **S11 压缩** | `how-pi-agent-works/docs/source/session-compaction.md` + `demos/04-compaction.md` | `pi-agent-book/book/03` §3.6、`04` §4.4；`pi-bluebook/docs/guide/context-and-compaction.md`、`cases/compaction-before-after.md`；`dg-ai-notes/.../第9章-上下文压缩...` | `coding-agent/src/core/compaction/compaction.ts`、`branch-summarization.ts` |
| **S12 扩展/资源/技能** | `pi-agent-book/book/06-extensibility.md` | `how-pi-agent-works/docs/source/tools-extensions.md`；`pi-bluebook/docs/guide/skills-extensions-packages.md`、`guide/first-extension.md`；`pi-bluebook/docs/guide/first-skill.md` | `coding-agent/src/core/extensions/*`、`skills.ts`、`prompt-templates.ts`、`resource-loader.ts` |
| **S13 Unity 宿主** | 无直接对应 | 本仓库 `docs/02-Pi.NET复用评估.md`；药老/WorldTalk 的宿主写法（反编译参考） | 新写 |

> `dg-ai-notes` 的源码精读有 TS 与 Python 两套，优先 TS；读不通时切 Python 对照。

---

## 三、怎么读（每个阶段）

1. **先读原版源码**：只看当前阶段相关文件，不展开全部。
2. **再读主参考 1 篇**：补整体图和"为什么"。
3. **写讲解卡/代码/测试**。
4. **做完后按需读次参考**：对比自己的理解，补充教学篇。
5. 不要求读完整本书；每阶段 1 篇主参考 + 1–2 篇次参考即可。

---

## 四、版本漂移提醒

| 项目 | 基准 | 注意 |
|---|---|---|
| 原版 pi（本地） | **v1.5.0 / f9a263655 / 2026-11-02** | 唯一行为真相源 |
| pi-bluebook | 内容核验至 2026-09 | 可能早于 v1.5.0；路径/API 以源码为准 |
| pi-agent-book | v0.85.1 / 2026-09-14 | `packages/agent` 结构已变；只取概念 |
| how-pi-agent-works | 概念/教学版 | 不是逐行移植；适合理解，不适合照抄 |
| dg-ai-notes | 未标固定基准 | 以 TS 源码为准；Python 版作对照 |
| pi-book | 基准 `cd20a8d2e`，只发布 3 章 | 第 2 章可深读，其他章节暂无 |

**规则**：书里写的路径找不到时，先 `git -C pi-study/pi log -- <path>` 看历史，再看 v1.5.0 的当前位置；不要因为书过时就否定概念。

---

## 五、暂不读 / 暂不做

- coding-agent CLI/TUI/slash commands/themes/keybindings；
- bash/read/write/edit/grep/find 工具章节；
- MCP、codemode、durable、env；
- pi-bluebook 安装/更新/卸载/VPS/Windows 安装；
- Pi.NET 的 OAuth/图片/分类/42 家 provider；
- pi-book 的 web/gateway/书籍引擎。

---

## 六、新增参考的规则

1. 只加"与某阶段直接相关"的章节，不堆书单。
2. 标注：主参考 / 次参考 / 版本 / 一句话理由。
3. 不复制书里的内容；我们的教学篇自己写，引用链接即可。
4. 书与源码冲突时，以源码为准，并在 Q&A 记录差异。
