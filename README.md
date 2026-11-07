# PiUnityAgent

> **Unity 游戏 AI mod 的类 pi 底座**——让 AI mod 只写自己的业务，不用再关心底层。

- **参照物**：[pi](https://github.com/earendil-works/pi) 的 `pi-ai` + `pi-agent-core`（不是 `pi-coding-agent`）
- **语言/生态**：纯 C#，**不依赖 Node/Python 运行时**
- **目标框架**：底座（Ai/Core/Runtime）= `netstandard2.1`（Unity 2021.2+ / 太吾 2022.3）；Hosts.Net8 = `net8.0`
- **状态**：S1–S3 初版已实现并合入 `main`（127 项测试）；S3 形状返工（spec v2，B 通讯端口方案）待执行；通讯底座 `UnityLlmTransport` Phase 0/1/2 已完成（独立仓库）

---

## 一、一句话定位

游戏 mod 作者想做一个 AI 助手/NPC/叙事导演时，不应该从零实现：

- LLM 多 provider 调用与流式
- agent 循环与工具调用
- 会话持久化与上下文压缩
- 技能/案例/提示词的按需加载
- 扩展注册与生命周期
- Unity 主线程与游戏 API 的桥接

这些统一放进这个底座；mod 只写"我是谁、我能读什么、我能改什么、我的提示词是什么"。

---

## 二、不是什么

- **不是 pi 的 C# 翻译**：只搬 `pi-ai` + `pi-agent-core` 的机制与契约，不搬 `coding-agent`（终端 UI、bash/文件/编辑工具）。
- **不是某个具体 mod**：说书人、药老、NPC 对话 mod 都是它的上层扩展。
- **不是服务器框架**：面向游戏内/游戏旁的 mod 分发，不是云端 agent 平台。
- **不是"全能 agent"**：默认不开放文件/进程/网络；游戏写操作必须白名单 + 回读 + 幂等。

---

## 三、分层

```
┌──────────────────── 扩展层（各 mod 自己的业务）────────────────────┐
│  UI 扩展 / 知识扩展 / NPC 扩展 / 叙事扩展 / 控制扩展               │
└──────────────────────────────▲────────────────────────────────────┘
                               │ 只依赖接口
┌──────────────────────────────┴────────────────────────────────────┐
│  宿主适配器（Host）                                                │
│  ├─ Unity Host：主线程 Dispatcher、UnityWebRequest SSE、UGUI 事件  │
│  └─ .NET 8 Host（可选）：HttpClient SSE、后台任务、ALC 扩展加载     │
└──────────────────────────────▲────────────────────────────────────┘
                               │ 引用
┌──────────────────────────────┴────────────────────────────────────┐
│  PiUnityAgent.Ai + Core + Runtime（netstandard2.1，纯逻辑，可单测）      │
│  消息/工具契约 · Agent Loop · 会话树 · 压缩 · 技能 · provider 抽象 │
└───────────────────────────────────────────────────────────────────┘
```

- **Core 是类库，不是进程**；"在下方"指依赖方向。
- 主流 Unity 游戏只有一个 Unity 宿主；太吾绘卷因 mod 框架特殊，多一个 .NET 8 宿主（前端 Master / 后端 Slaver，管道 RPC）。
- 同一份 Core 编译进两个宿主；双宿主是部署拓扑，不是框架假设。
- **通讯层（B 方案，2026-11-07）**：`Ai` 只依赖自有的 `FetchFunction` 端口；`UnityLlmTransport` 作为实现/适配方由 `Hosts.Unity` / `Ext` 消费，`Ai` 不直接依赖。详见 `docs/07-通讯层集成契约.md`。

---

## 四、命名约定

| 层级 | 名称 |
|---|---|
| 仓库 / 文件夹 | `PiUnityAgent` |
| 解决方案 | `PiUnityAgent.slnx` |
| 程序集 / 命名空间 | `PiUnityAgent.Ai` / `PiUnityAgent.Core` / `PiUnityAgent.Runtime` / `PiUnityAgent.Hosts.*` |
| 扩展接口 | `PiUnityAgent.Core.Extensions.IExtension` |

理由：文件夹名负责"一眼知道"，代码命名空间负责"少打字"。

---

## 五、目录规划

```
PiUnityAgent/
├─ README.md
├─ PORTING.md                    # 上游文件 → 我们的文件映射 + 版本卡
├─ docs/
│  ├─ 00-项目定位与边界.md
│  ├─ 01-调研简报-CSharp生态.md
│  ├─ 02-Pi.NET复用评估.md
│  ├─ 03-开发方法-拆解学习与移植约定.md
│  ├─ 04-开发流程分轨与模型路由.md
│  ├─ 05-教学阶段路线.md
│  └─ learning/                  # 教学与心智模型（导读/Q&A/逐阶段/参考索引）
├─ sop/
│  ├─ README.md
│  ├─ 01-GitHub推送与代理配置-2026-11-05.md
│  └─ 02-参考源同步-2026-11-05.md
├─ src/
│  ├─ PiUnityAgent.Ai/            # pi-ai 子集：消息/流式/工具/provider
│  ├─ PiUnityAgent.Core/          # pi-agent-core：循环/状态/工具执行
│  ├─ PiUnityAgent.Runtime/       # 会话/上下文/压缩/扩展/资源
│  ├─ PiUnityAgent.Hosts.Unity/   # Unity 宿主适配
│  └─ PiUnityAgent.Hosts.Net8/    # .NET 8 宿主适配
├─ tests/
│  └─ PiUnityAgent.Core.Tests/    # 纯逻辑单测（待建）
└─ samples/                 # 最小示例（待建）
```

---

## 六、能力清单（摘要）

**P0**：统一消息/流式事件、provider 抽象（OpenAI-compatible + Anthropic）、Agent Loop、工具注册与执行、会话持久化、上下文压缩、技能按需加载、重试降级、纯逻辑可单测。

**P1**：扩展生命周期、多 agent 编排、token/预算、JSON Schema 防幻觉、provenance 挂点。

**P2**：RAG/向量、多模态、本地模型、可观测性。

完整清单与硬约束见 `docs/00-项目定位与边界.md` 与 `docs/01-调研简报-CSharp生态.md`。

---

## 七、与现有项目的关系

| 项目 | 关系 |
|---|---|
| **说书人**（上层使用者，另一仓库） | 上层扩展之一：感知查询、导演循环、案例 skills、控制 Intent、发布闸 |
| **太吾药老** | 上层扩展之一：F8 问答、百晓册 skills、LightRAG 工具 |
| **UnityLlmTransport**（独立仓库） | 通讯底座：UWR/HttpClient/SSE/取消/超时/主线程。按 B 方案，`Ai` 只依赖自有 Fetch 端口，Host 层适配器消费它；不直接依赖。见 `docs/07` |
| **江湖有灵 / WorldTalk** | 反编译参考；其领域能力（过月编排、幂等日志、ActionTool 防幻觉）作为扩展设计参照，不照抄代码 |

---

## 八、当前状态（2026-11-07）

- ✅ 需求与边界（`docs/00`）、C# 生态调研（`docs/01`）、Pi.NET 复用评估（`docs/02`）
- ✅ **S1 消息与内容模型**（已合 `main`）
- ✅ **S2 流式事件协议**（已合 `main`）
- ✅ **S3 Provider 抽象与 Faux**（初版已合 `main`；累计 127 项测试）
- 🟡 **S3 对齐返工 spec v2**：B 通讯端口方案，待 review → plan → 实施
- ✅ **通讯底座 UnityLlmTransport Phase 0/1/2**（独立仓库，2026-11-07；6 组 15/15）
- ⬜ **S4 OpenAI-compatible / DeepSeek**（S3 返工完成后开工）
- ⬜ Core MVP 设计 / S6–S8
- ⬜ S13 Unity 宿主
- ⬜ 药老迁移验证

---

## 九、边界声明

1. 反编译源码仅用于学习借鉴，不照抄业务逻辑。
2. 不评估 Node/Python 作为运行时；它们只能作为开发期参照或可选外部服务。
3. 目标是 mod 分发，不是服务器部署；任何需要玩家安装运行时/配置环境变量的方案都要标注。
4. 版本与生态变化快：所有候选结论以调研当日为准，需附版本号与日期。
