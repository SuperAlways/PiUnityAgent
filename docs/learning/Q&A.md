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

## Q12. S2 是不是"把多家 SSE 转成统一格式的桥接器"？

不是。**S2 是统一语言和承载容器，桥接器是 S3/S4/S5 的 provider adapter。**

- S1 定最终消息形状（名词）；
- S2 定生成过程的事件、顺序、终态、计时、取消（时间线）；
- S3/S4/S5 把厂商 SSE/HTTP 翻译成 S2 事件（桥接）；
- S7/UI 只消费 S1+S2。

S2 本身不解析 SSE；Faux 不联网也能产出同样的事件。

## Q13. partial 为什么用不可变快照，而不是 pi 原版的活对象？

不是 C# 硬性限制，是"语言映射 + 工程取舍"：

- S1 全员 `record` + `init`；共享活对象要么给所有属性加 setter，要么另造一套可变消息模型，违背"一种消息形状"；
- provider 后台线程 push、UI/agent 另一线程读；快照免锁、无撕裂读；
- 事件里的快照可记录、可断言、可回放，不会被后续突变"追改"；
- `record` 的 `with` 让"基于旧值造新值"很自然。

pi.net 已经先选了快照（`AssistantMessageEventStream.Partial` 每次 Push 更新，事件注释写明"record 不可变，每次都是新快照"），我们跟随它。代价是每事件分配；长文本可在 provider 内部用 `StringBuilder`、按需物化。

## Q14. 取消到底是停厂商，还是本地不再理会？

两者是不同通道：

- **消费侧 token**（迭代器 / `WaitForDone`）：只是不再读；生产者可能继续推，unbounded 缓冲会涨；
- **生产侧 token**（v2 起只有 `SimpleStreamOptions.CancellationToken`）：provider 应中止 HTTP 请求或调用厂商 cancel API，并产出 `Error(Aborted)` 终态。
- **LazyStream setup 异常**（含 OCE）：按 pi/pi.net `lazyStream` 统一编码为 `Error(Error)`；Aborted 由 provider 内层流负责，不在 setup 层自行改写。

标准 SSE 没有通用的"stop generating"消息，实际做法是客户端关闭 HTTP 连接；部分厂商为延后/后台任务提供显式 cancel 端点。底座不能保证厂商立刻停止计算，只能保证本地尽快中止请求。

## Q15. Done 的四种 reason、Error 与 Aborted 怎么区分？

`Done` 是正常终态，`Reason` 有四种：

- `Stop`：自然结束；
- `Length`：达到输出上限，被截断（正常但未完成，所以不是 Error）；
- `ToolUse`：要求执行工具，进入下一轮；
- `Deferred`：厂商延后响应，之后用句柄取回。

`Error` 是"环境/服务坏了"；`Aborted` 是"被叫停"（用户停止、超时、宿主取消）。两者都走 `Error` 终态事件，但 reason 不同、重试策略不同；合并会导致"用户点了停止，系统自动重试"。

## Q16. SSE 的 chunk 是 token 吗？流式文本存在哪？

不是。chunk 是传输边界，可能含多个 token，也可能把 token 拆开。文本进入消息前已经由 SSE 解析器解码成 `string`，按内容块累加：

- `text_delta` → `TextContent.Text`；
- `thinking_delta` → `ThinkingContent.Thinking`；
- `toolcall_delta` → 参数 JSON 字符串，结束时才解析。

C# 的 `string` 不可变，直接拼是每次新建字符串；长文本建议 provider 内部用 `StringBuilder`。

## Q17. 半截输出会进入下一轮 loop 吗？

分情况：

- `ToolUse`：assistant 消息 + 工具结果进入下一轮；
- `Length`：assistant 消息保留，工具调用全部判失败，错误结果进入下一轮；
- `Error` / `Aborted`：assistant 消息保留在 transcript，但**本轮立即结束**，不执行工具；是否把它回放给下一次模型由上层策略决定。

建议：展示保留，回放前过滤或替换为说明——半截回答对模型是噪声，对玩家是信息。

## Q18. Model 和 Provider 是什么关系？是设计模式吗？

不是一个单一 GoF 模式，是组合：

- `Models` 注册表 = Registry / Service Locator；
- `Provider.StreamFn` = Strategy；
- `Provider.Models` = 一对多聚合；
- `Model.Provider` = 外键/反向引用；
- Faux = Fake Object（测试替身）。

最接近的 .NET 类比是 `DbProviderFactory`：注册表按名字找 provider，provider 提供创建连接的实现。双向结构让 Model 可序列化/可分发表，注册表再按外键解析到实现。

## Q19. Faux 是 pi 自带还是我们造的？上下游是谁？

pi 官方 `pi-ai` 自带（`packages/ai/src/providers/faux.ts`），pi.net 也有（`Pi.Ai/Providers/Faux.cs`）。我们按 S3 范围移植了确定性版本。

- 上游（调用方）：`Models` / AgentLoop，给 Model + TranscriptContext + Options + CancellationToken；
- 内部：读脚本队列，产出 S2 事件；
- 下游（消费方）：AgentLoop / 测试 / UI，消费事件并拿最终消息。

它不是测试外挂，而是注册表里与真实 provider 平级的一个实现。

## Q20. 为什么未知 provider 返回错误事件而不是抛异常？

因为 S2 的契约是"失败是数据"。S7 只应有一套失败处理路径（读终态事件），不应为 S3 单独写 try/catch；抛异常会让 agent loop 被打断，而不是决定下一步。

## Q21. 为什么 `TokensPerSecond` 不在 `SimpleStreamOptions` 里？

它是 Faux 的模拟节奏，不是真实模型请求参数。放公共选项会迫使真实 provider 实现无意义参数，并把测试旋钮污染到生产 API。它属于 `FauxProviderOptions`。

## Q22. S3 为什么不做认证、动态目录、Deferred？

S3 的范围是"模型描述 + 注册表 + StreamFn 契约 + Faux"。认证/HTTP/SSE 属于 S4/S5；动态模型目录、图片/分类 provider、Deferred fetch/cancel 属于后续阶段。A 方案下类型可以保留，行为后置，并在 spec 与 `_mapping.md` 记录。

## Q23. Faux 的确定性与 pi 原版有什么不同？

pi 原版用 `Date.now()` + `Math.random()` 生成 ID，分块大小也随机；我们改成：

- 每 provider 单调计数器（`tool:1`…）；
- 固定分块大小；
- 时间戳只通过注入的 `IClock`。

这是为测试可重复而做的有意偏离，代价是不如原版"逼真"，已记录在 S3 spec §6.9 与 `Providers/_mapping.md`。

## Q24. StreamFn 永不抛，那流自身故障怎么办？

两层区分：

- **请求建立/返回流之前的失败**：`Models.Stream/StreamSimple` 经 `LazyStream` 兜底，统一转成 `Error(Error)` 终态事件；`Aborted` 由 provider 内层流主动产出；
- **流已返回、之后内部故障**：属于基础设施错误，用 `EventStream.Fail(exception)` 关闭；迭代侧和 `WaitForDoneAsync` 都会抛原始异常，而不是编码成模型错误事件。
