# C# 生态 AI 底座 · 需求简报与外部调研提示（2026-11-04）

> **状态**：调研简报 v1。
> **用途**：交给可联网的 agent，调研 C#/.NET 生态中是否有可作为"游戏 mod AI 框架底座"的现成项目。
> **边界**：本文只描述需求、约束与待核实问题，**不预设结论**；文中列出的候选项目均标注为"待核实"。
> **一句话**：我们要在 C# 生态里做一个 `pi-ai + pi-agent-core` 的等价底座，供 Unity 游戏 mod 使用；请调研有没有现成轮子可复用、可组合或至少可借鉴。

---

## 〇、给外部 agent 的阅读顺序

1. 先读 §一、§二，理解"我们要造什么"。
2. 再读 §三，硬约束是**否决项**，不满足的候选直接排除或标注。
3. §五列的是**方向假设，不是推荐**，请独立核实并补充。
4. §六是评估标准；§七是必须回答的问题；§八是可直接粘贴的调研 Prompt。

---

## 一、背景与前因后果

### 1.1 项目

《太吾绘卷》感知层 mod（以下简称"说书人"）：

- 采集游戏内的事件/状态/关系，落 SQLite，形成"事实账本"；
- 上层计划做导演层（决定写什么、埋什么、控什么）和呈现层（写小说/章节）；
- 最终形态是"沙盒涌现 + 小说引导"：小说铺垫 → 游戏发生 → 玩家选择 → 小说写回。
- 架构原则：**本体层（纯逻辑，必须有单测，零游戏引用） / 胶质层（碰游戏 API，零判断）** 两层硬边界。

### 1.2 已有的 AI mod 生态（作为需求来源与参照）

| 项目 | 性质 | 现有底座 | 我们要的共性 |
|---|---|---|---|
| **太吾药老**（自研） | 游戏内 F8 问答助手，百晓册 + LightRAG | 自研 ReAct 循环（C#，netstandard2.1 Core）；UnityWebRequest 流式 | agent 循环、工具调用、会话、压缩、技能 |
| **江湖有灵**（第三方，仅反编译参考） | NPC 真办事 + 过月演化 + 江湖大事 | 自研三层（Frontend/Core/Backend），单 LLM + 工具表 | 过月批处理、幂等日志、generation、行为控制 |
| **WorldTalk**（第三方，仅反编译参考） | NPC 闲谈 + 飞鸽 + 人格画像 | 自研两层，ActionTool + 双 LLM | 工具防幻觉、候选快照、设置中心 |

> 三者共同的"底座级"能力高度重合：**LLM 调用、流式、工具调用、会话/记忆、上下文压缩、角色/技能加载、扩展注册、过月/后台调度**。目前每家都自己造了一遍。

### 1.3 结论

与其让每个 mod 再各造一遍，不如抽一个**平台无关的 C# agent 底座**，上面挂各家扩展。参照对象是 [pi](https://github.com/earendil-works/pi) 的 `pi-ai` + `pi-agent-core`，但**必须是 C#/.NET 实现**，不能引入 Node/Python 运行时。

---

## 二、要造的底座是什么

### 2.1 分层

```
┌──────────────────── 扩展层（各 mod 自己的业务）────────────────────┐
│  UI 扩展 / 知识扩展 / NPC 扩展 / 叙事扩展 / 控制扩展               │
└──────────────────────────────▲────────────────────────────────────┘
                               │ 只依赖接口
┌──────────────────────────────┴────────────────────────────────────┐
│  宿主适配器（Host）                                                │
│  ├─ Unity Host：主线程 Dispatcher、UnityWebRequest SSE、UGUI 事件  │
│  └─ .NET 8 Host（可选，太吾的后端进程）：HttpClient SSE、后台任务、  │
│      AssemblyLoadContext 扩展加载                                  │
└──────────────────────────────▲────────────────────────────────────┘
                               │ 引用
┌──────────────────────────────┴────────────────────────────────────┐
│  Core（平台无关、纯逻辑、可单测、零 Unity/游戏引用）                │
│  消息/工具契约 · Agent Loop · 会话树 · 压缩 · 技能 · 工具注册 ·    │
│  provider 抽象 · 重试 · 事件流                                     │
└───────────────────────────────────────────────────────────────────┘
```

- **Core 是类库，不是进程**；"在下方"指依赖方向。
- 主流 Unity 游戏只有一个 Unity 宿主；太吾因 mod 框架特殊，多一个 .NET 8 宿主（前端 Master / 后端 Slaver，管道 RPC）。
- 同一份 Core 编译进两个宿主；双宿主是部署拓扑，不是框架假设。

### 2.2 能力清单

**P0（没有就不是底座）**

1. 统一消息模型：user / assistant / tool result，兼容文本、工具调用、推理内容（如 DeepSeek `reasoning_content`）、多段 content。
2. 统一流式事件：text delta / reasoning delta / tool call start-delta-end / usage / done。
3. Provider 抽象：至少 OpenAI-compatible（DeepSeek、硅基流动、本地 vLLM/Ollama 等）+ Anthropic；可选 Google。
4. Agent Loop：组装上下文 → 调模型 → 执行工具 → 回写结果 → 判断是否继续；支持中止。
5. 工具注册与执行：工具声明 schema、执行位（InProcess / Unity 主线程 / 后端 RPC / HTTP）、超时、错误回传。
6. 会话持久化：可恢复的历史；理想是**树形会话**（分支/fork），至少是线性 JSONL。
7. 上下文压缩：阈值触发摘要，原始记录保留；压缩后行为可解释。
8. 技能按需加载：提示词素材/案例/声线卡等，先给摘要再按需取全文。
9. 重试与降级：指数退避、超时、失败不阻塞游戏。
10. 纯逻辑可单测：Core 不依赖 Unity/游戏，测试覆盖循环、工具、压缩。

**P1（决定好不好用）**

11. 扩展生命周期：注册工具 / 命令 / 事件钩子 / context transform；编译期注册 + 运行期加载（后端）。
12. 多角色/多 agent 编排（导演、角色、审计）——可后置，但接口要留。
13. token 估算与预算、单次/每月调用预算。
14. 结构化输出/JSON Schema 约束、防止模型编造工具参数。
15. 会话摘要、事实与叙述分离（provenance）的挂点。

**P2（锦上添花）**

16. 向量检索 / RAG 工具（可外挂）。
17. 多模态（图片）。
18. 本地模型（llama.cpp / ONNX）。
19. 可观测性：事件日志、trace、成本统计。

**明确不要的**

- 终端 UI、bash/文件系统工具、代码编辑工具（那是 `pi-coding-agent`，不是我们要的）。
- 默认开放文件/进程/网络权限的"全能 agent"。
- 依赖 Node/Python 运行时。

---

## 三、硬约束（否决项）

| # | 约束 | 说明 |
|---|---|---|
| C1 | **Unity 兼容** | 代码要能在 Unity 的 Mono / netstandard2.1 环境运行；至少 Core 必须。Unity 版本以目标游戏为准（太吾为 Unity + Mono，前端 netstandard2.1） |
| C2 | **零/轻依赖** | Unity mod 分发不能随意带大量 NuGet 依赖；优先零依赖或可合并进单个 DLL |
| C3 | **无 Node/Python 运行时** | 终端用户不应额外安装运行时；所有能力走 C# |
| C4 | **流式 + 工具调用** | 必须支持 SSE 流式与 function/tool calling，不能只支持一次性 completion |
| C5 | **主线程分离** | 游戏 API 只能在 Unity 主线程调用；Core 必须允许工具执行被路由/排队 |
| C6 | **可单测** | Core 纯逻辑，能在无 Unity 环境跑单测 |
| C7 | **许可证可用** | MIT / Apache-2.0 等允许免费 mod 分发与闭源组合；GPL 类需单独评估 |
| C8 | **可打包** | 能打进 mod 的 Plugins 目录，体积可接受；支持 IL2CPP 的游戏需评估 AOT/裁剪 |
| C9 | **多 provider** | 至少 OpenAI-compatible + Anthropic；国内网络可用 |
| C10 | **可扩展** | 第三方 mod 作者能注册工具/技能，不需要改 Core |

补充约束（非否决，但影响评分）：

- 太吾后端是 **.NET 8**，前端是 **netstandard2.1**；Core 最好能同时被两者引用。
- 部分 Unity 游戏用 **IL2CPP**（不能运行时加载程序集、反射受限）；如果候选依赖重度反射/动态代码，需要标注。
- 主流 mod 加载器：BepInEx / MelonLoader / UnityModManager / 游戏自带；底座不应绑死某一个。
- 国内用户可能无法稳定访问 NuGet/GitHub；依赖越少越好。

---

## 四、参照物：pi 是什么

- 仓库：`https://github.com/earendil-works/pi`（本地参考 v1.3.0，2026-10-19）
- 本地路径：`D:\shuoshu\taiwuask\pi-study\pi`
- 关键文档：`packages/coding-agent/docs/how-pi-works.md`（60 行，信息密度最高）

### 4.1 要复刻的

| pi 组件 | 内容 | 规模 |
|---|---|---|
| `pi-agent-core` | Agent Loop、Agent 状态、steering/follow-up、传输抽象 | 约 2,519 行 TS |
| `pi-ai` 子集 | 多 provider 统一 API、流式、工具调用、模型目录、重试 | 全量 2.7 万行；只需 3 家 provider |
| Session 树 | JSONL 树、branch/fork/clone、active branch 重建 | 机制简单 |
| Compaction | 摘要条目替换旧消息、原文保留 | 机制简单 |
| Skills | 按需加载的指令/素材 | 机制简单 |
| Extensions | 工具/命令/钩子/上下文变换的注册与生命周期 | 概念可借，C# 需重设计 |

### 4.2 不要复刻的

- `pi-coding-agent`（7.6 万行）：CLI、TUI、bash/read/edit/grep 等编码工具。
- 无权限系统的"全能 agent"默认姿态；游戏写操作必须走白名单 + 回读 + 幂等。
- Node 生态的运行时与包管理。

---

## 五、候选方向（待核实，不是推荐）

> 以下只是检索起点。请核实**当前版本**的目标框架、依赖、许可证、维护状态、Unity 可行性，并补充遗漏。

### 5.1 .NET 官方/大厂抽象

- **Microsoft.Extensions.AI**：`IChatClient` / `IEmbeddingGenerator` 抽象、中间件、工具调用。
- **Microsoft Agent Framework**（Semantic Kernel + AutoGen 的后续统一，名称/状态待核实）。
- **Semantic Kernel**：plugins、planners、connectors、agents。
- **AutoGen for .NET**：多 agent 编排。

关注点：目标框架是否 netstandard2.1；依赖树大小；是否可在 Unity Mono 跑；是否只面向服务端。

### 5.2 社区 agent/LLM 框架

- **BotSharp**：.NET 对话平台，插件式。
- **LangChain.NET** 及其各社区分支（质量参差，逐一核实）。
- **LLamaSharp**：本地 llama.cpp 绑定（netstandard2.0）。
- **OllamaSharp**：Ollama 客户端。
- **SemanticKernel 衍生/其他 agent 库**（请补充）。

### 5.3 单点能力库

- **OpenAI 官方 .NET SDK**、**Anthropic 社区 SDK**、**Azure.AI.OpenAI**：provider 客户端。
- **SSE 客户端**：HttpClient 流式、`DownloadHandlerScript`（Unity）。
- **JSON Schema**：NJsonSchema、JsonSchema.Net、Microsoft.TypeChat。
- **本地推理**：LLamaSharp、ONNX Runtime、Unity Sentis（评估是否适合 LLM）。
- **会话/持久化**：是否有树形会话/压缩的现成实现（大概率没有，需自研）。

### 5.4 需要重点核实的"坑"

- Unity Mono 下 `HttpClient` 与 `UnityWebRequest` 的流式差异；SSE 分帧。
- netstandard2.1 与 net8.0 的 API 差异（`System.Text.Json`、`HttpClient`、`AssemblyLoadContext`）。
- IL2CPP/AOT 对反射、动态加载、序列化的限制。
- NuGet 依赖在 Unity 中的合并/冲突（尤其 Newtonsoft.Json、System.Text.Json）。
- 许可证：Steam 创意工坊免费 mod 分发 + 可能的闭源组合。

---

## 六、评估标准

| # | 维度 | 权重 | 否决条件 |
|---|---|---|---|
| 1 | Unity/netstandard2.1 兼容 | 高 | 只能 net8+ 且无法裁剪 → 排除 |
| 2 | 依赖数量与体积 | 高 | 依赖树不可合并/体积过大 → 排除 |
| 3 | 流式 + 工具调用完整性 | 高 | 不支持 → 排除 |
| 4 | Provider 覆盖（OpenAI-compatible + Anthropic） | 高 | 只有一家且难扩展 → 降级 |
| 5 | 会话/压缩/技能 | 中 | 没有 → 自研补 |
| 6 | 扩展模型 | 中 | 无扩展点 → 自研补 |
| 7 | 许可证 | 高 | GPL/AGPL 需单独评估 |
| 8 | 维护活跃度与版本稳定性 | 中 | 长期无维护 → 降级 |
| 9 | 可测试性（纯逻辑） | 中 | 与 Unity 强耦合 → 降级 |
| 10 | 中文文档/社区 | 低 | — |
| 11 | IL2CPP/AOT 可行性 | 中 | 依赖重度动态代码 → 标注风险 |
| 12 | 迁移成本（从药老 Core 迁） | 中 | — |

---

## 七、需要外部 agent 回答的问题

1. **候选总表**：C#/.NET 生态里，哪些项目最接近"pi-ai + pi-agent-core"？请给出仓库、许可证、目标框架、依赖、最近提交、star、维护状态。
2. **能力对照**：用 §2.2 的 P0/P1/P2 清单逐项打分（有 / 部分 / 无），并给出源码或文档证据。
3. **Unity 可行性**：每个候选能否在 Unity Mono / netstandard2.1 下运行？有没有 Unity 集成案例？IL2CPP 下有什么限制？
4. **组合方案**：如果单个项目不完整，最合理的组合是什么（例如"A 的 provider + B 的循环 + 自研会话/压缩"）？给出依赖图和最小代码骨架。
5. **自研 vs 采用**：分别估算"基于候选改造"与"从零自研 Core"的工作量、风险、长期维护成本。
6. **许可证与分发**：哪些方案可以安全用于 Steam 创意工坊的免费 mod？哪些需要额外授权？
7. **坑清单**：Unity + .NET 生态做 LLM agent 的实际踩坑（流式、线程、AOT、JSON、依赖冲突、代理/网络）。
8. **结论**：给出 2–3 个推荐方案（含"纯自研"作为基线），说明适用场景与不适用场景。

**输出格式建议**：

- 一张候选总表；
- 一张 P0/P1/P2 能力矩阵；
- 每个推荐方案一段"为什么是它 + 最大风险"；
- 附关键证据链接（源码文件/文档/issue）。

---

## 八、可直接粘贴的调研 Prompt

```text
你是一个资深 .NET/Unity 技术调研员。请联网调研以下问题，并给出有源码/文档证据的结论。

【背景】
我们要为一款 Unity 游戏（《太吾绘卷》，mod 前端 netstandard2.1 / Mono，部分 mod 有 .NET 8 后端进程）开发一个"AI mod 框架底座"，参照 pi（https://github.com/earendil-works/pi）的 pi-ai + pi-agent-core：
- 统一多 provider LLM 流式 API（OpenAI-compatible/DeepSeek + Anthropic 至少）
- Agent Loop + 工具调用
- 会话持久化 + 上下文压缩
- 技能/提示词按需加载
- 可扩展（第三方 mod 作者注册工具/技能）
- 纯 C#，不依赖 Node/Python 运行时
- Core 必须能在 Unity Mono / netstandard2.1 下跑，且可单测

【硬约束】
1. Unity netstandard2.1 兼容（至少 Core）
2. 依赖少、可合并进 mod DLL
3. 支持 SSE 流式 + function/tool calling
4. 主线程与工具执行可分离
5. MIT/Apache 等宽松许可证
6. 能打包进 Steam 创意工坊的 mod（体积可接受）

【请回答】
1. C#/.NET 生态有哪些现成项目最接近这个底座？请列出仓库、许可证、目标框架、依赖、维护状态。
2. 重点核实：Microsoft.Extensions.AI、Microsoft Agent Framework、Semantic Kernel、AutoGen for .NET、BotSharp、LangChain.NET 各分支、LLamaSharp、OpenAI/Anthropic 官方或社区 SDK，以及你发现的其他项目。
3. 用以下能力清单逐项打分（有/部分/无）并给证据：消息模型、流式事件、多 provider、agent loop、工具注册与执行、会话树、上下文压缩、技能按需加载、重试/降级、扩展生命周期、JSON Schema 防幻觉、预算、可观测性。
4. 每个候选能否在 Unity Mono/netstandard2.1 下运行？IL2CPP 下有什么限制？有无 Unity 集成案例？
5. 给出 2–3 个推荐组合方案（含"纯自研"基线），并估算工作量与风险。
6. 列出 Unity + .NET 做 LLM agent 的实际坑（流式、线程、AOT、JSON、依赖冲突、网络）。
7. 所有结论请附源码文件、文档或 issue 链接。

【输出】
先给候选总表，再给能力矩阵，最后给推荐方案与风险。不要泛泛而谈，要有版本号和证据。
```

---

## 九、术语表

| 术语 | 含义 |
|---|---|
| **Unity / Mono** | 游戏引擎与其 C# 运行时；mod 通常以 DLL 注入 |
| **netstandard2.1** | Unity 前端可用的 .NET API 面 |
| **net8.0** | 太吾后端进程的目标框架 |
| **IL2CPP** | Unity 的 AOT 编译模式；运行时加载程序集/反射受限 |
| **SSE** | Server-Sent Events，LLM 流式输出常用协议 |
| **Tool calling / Function calling** | 模型输出结构化工具调用，由宿主执行并回传结果 |
| **Agent Loop** | 组装上下文 → 模型 → 工具 → 回写 → 继续/结束 的循环 |
| **Session 树** | 会话按 id/parent 组成树，可分支、fork、回溯 |
| **Compaction** | 上下文超限时用摘要条目替换旧消息，原文保留 |
| **Skills** | 按需加载的指令/素材包（案例、声线卡、设定） |
| **ALC** | AssemblyLoadContext，.NET 的运行时程序集加载/卸载机制（Unity Mono 基本不可用） |
| **provenance** | 事实来源标记（游戏/导演/补写/叙述） |

---

## 十、边界声明

1. 江湖有灵、WorldTalk 的反编译源码**仅用于学习借鉴**，不得照抄业务逻辑；本文只把它们作为能力参照。
2. 本文不评估 Node/Python 方案（pi 本体、LightRAG 等）作为运行时；它们可作为开发期参照或可选外部服务。
3. 目标是"游戏 mod 分发"，不是服务器部署；任何需要玩家安装运行时/配置环境变量的方案都要标注。
4. 版本与生态变化快：所有候选结论以调研当日为准，需附版本号与日期。
