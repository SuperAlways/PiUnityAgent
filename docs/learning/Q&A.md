# Q&A · 疑问、误解与纠正（append-only）

> 记录开发过程中真实出现的问题与回答；后人读到"为什么这样想是错的"，比读到结论更有价值。
> 新条目追加到末尾，不改旧条目；需要订正时写明"订正"。

---

## Q1. 为什么不直接拿 Pi.NET 当底座？

Pi.NET 是独立社区的 .NET 9/10 移植，不是官方；目标框架是 net9/net10，Unity 2021.2 的 Mono 用不了。
我们的目标是 **netstandard2.1 + Unity**，所以只能移植/重构，不能直接引用。
Pi.NET 的价值是"同一 TS 特性在 C# 里怎么写"的参考，以及它踩过的坑。

## Q2. 为什么是 netstandard2.1，不是 net8？

- Unity 2021.2+ 的 .NET Standard 2.1 profile 能消费；
- 太吾后端是 .NET 8，能引用 netstandard2.1；
- 它是"Unity 前端 + .NET 8 后端"的共同分母。
- Unity 2020.3 及更早只有 2.0；如果以后要覆盖，再考虑多目标 `netstandard2.0`。

## Q3. 为什么拆成 Ai / Core / Runtime？

- `Ai`：只负责"和模型说话"（协议、流式、工具调用、provider）。
- `Core`：只负责"让模型用工具干活"（循环、状态、工具执行、双队列）。
- `Runtime`：长运行所需的服务（Session、Context、Compaction、Extensions、Resources）。
三者混在一起就会变成 pi-coding-agent 那种 7.6 万行的应用；拆开后，厂商可以只引 `Ai`，mod 可以引 `Core`，完整 agent 才引 `Runtime`。

## Q4. 为什么 Pi.NET 只作参考，不能当行为标准？

上游 pi 是行为真相源；Pi.NET 是移植，可能有偏离或 port 缺陷。
写代码前先读原版；Pi.NET 只回答"C# 怎么写"。
两者冲突时，按原版实现，并把差异登记在讲解卡和决策日志。

## Q5. 为什么这一层叫 Runtime，不叫 Harness / Kernel / Platform？

- `Harness` 容易被理解成"整个 PiUnityAgent 栈"，和产品名冲突。
- `Kernel` 容易和 Semantic Kernel 混淆。
- `Platform` 太大、太虚。
- `Runtime` 准确：运行所需的会话、上下文、压缩、扩展、资源服务；和 `Ai`/`Core`/`Hosts` 并列。

## Q6. 为什么文件名要尽量贴合上游？

为了追踪：`agent-loop.ts` → `AgentLoop.cs`，读我们的代码时可以同时打开上游文件对照。
但 C# 有一文件一类型的惯例，`types.ts` 这类多类型文件拆到 `Types/`，用 `_mapping.md` 保留原名。
不为同名牺牲 C# 结构。

## Q7. 为什么先做 Faux provider？

它让 Agent Loop 在无网络、无密钥、无真实模型的情况下跑完整事件流。
这是"本体层可测"的前提；没有它，每次验证都要花 token、等网络、受模型波动影响。

## Q8. 为什么第一阶段不做 Session/Compaction？

M1 的目标是"最小可用 agent"：循环、工具、流式、可挂 Unity。
Session/Compaction 属于 2A，晚一点加不会阻碍 M1 验证；先做会让第一里程碑变重、变慢。
但 M1 的接口要预留（`IToolExecutor`、事件流、消息列表可替换），避免 2A 返工。

## Q9. 为什么 `SystemMessage` / `UserMessage` 不做一个 `string | blocks` 的 union 类型？

C# 没有 TS 的 discriminated union。S1 用了两个可空字段（`Content` / `ContentBlocks`）+ `MessageValidation`，要求恰好设置一个。

- 好处：构造直观，不需要为每条消息套一层包装；
- 代价：类型系统不强制，必须在校验/边界处调用 `Validate`；
- 备选：定义 `MessageContent` 抽象（string 或 blocks），类型更严格但多一层包装。S1 先不引入，等真实调用点觉得别扭再改。

## Q10. 为什么 `ToolResultMessage` 不做成泛型？

上游的 `ToolResultMessage<TDetails>` 是条件类型，保证 `details` 可 JSON 序列化。C# 里这属于编译期约束，S1 先用 `JsonNode? Details`：

- 足够表达任意 JSON 结构；
- 避免每个工具定义都带一个泛型参数；
- 需要强类型工具结果时，可在工具层做 `JsonSerializer.Deserialize<T>`，不污染消息类型。

## Q11. 为什么 A 方案"不砍能力"？

因为项目要求是"**可以用不到，但不能没有**"：pi-ai / pi-agent-core 的公共类型、API、能力面必须完整，哪怕是图片生成、分类、OAuth、42 家 provider 这些游戏 mod 暂时用不到的部分。

处理方式是：

- **类型/契约先完整**；
- **实现按阶段推进**，未实现部分标 `TODO` / `NotSupportedException`；
- 不删除类型、不改语义；
- 只有不属于这两个包的东西（CLI/TUI、bash/文件工具、project trust 等）才真正不移植。
