# S3 概念篇 · Provider 抽象与 Faux

> ⚠️ **v2 已实现（2026-11-07）**：本文保留初版概念讲解；S3 返工 v2 的最终形状（`IProvider`、完整 `StreamOptions`、Auth、Fetch 端口、`LazyStream`）见 [v2 补充](S3-Provider抽象与Faux-v2补充.md)。

> **阶段**：S3（pi-ai provider 抽象 + Faux）。
> **原则**：先理解"谁去说话、怎么找到它、没有网怎么办"；字段细节留给契约篇。
> **上游**：pi `packages/ai/src/models.ts`、`packages/ai/src/providers/faux.ts`、`packages/ai/src/types.ts`（Model/Provider/StreamOptions）；pi.net `src/Pi.Ai/Models/Models.cs`、`src/Pi.Ai/Providers/Faux.cs`。
> **我们的实现**：`src/PiUnityAgent.Ai/Providers/`、`Types/Model.cs`、`Types/SimpleStreamOptions.cs`、`Types/StreamFn.cs`。
> **配套**：`S3-Provider抽象与Faux-契约篇.md`、`Q&A.md` Q18–Q23。

---

## 1. 它解决什么问题

S1 定消息，S2 定流；还差一个角色：**谁真正去和模型说话？**

没有 S3：

- agent loop 直接绑某家 SDK，换模型就要改 loop；
- 测试必须联网、必须有 key；
- 模型目录、价格、上下文窗口散落各处；
- 未知厂商只能抛异常；
- Faux 不存在，S7 的循环无法稳定测试。

S3 提供四件东西：

1. **Model**：模型描述；
2. **Provider**：一组模型 + 一个调用函数；
3. **Models 注册表**：按 provider/model 查找与分发；
4. **Faux**：由脚本决定输出的假 provider。

---

## 2. 在系统中的位置

```
S7 agent loop
     │ 只依赖 StreamFn
     ▼
Models 注册表（S3）
     ├─ Faux（S3，脚本化、确定性、不联网）
     ├─ OpenAI-compatible（S4）
     └─ Anthropic（S5）
     │
     ▼
S2 事件流 → S1 消息
```

S3 是**接口层**，不是实现层。真正的 HTTP/SSE 在 S4/S5。

---

## 3. Model：一张"模型身份证 + 能力清单"

它不是请求，只是数据：

- 身份：`Id`、`Name`、`Api`、`Provider`、`BaseUrl`；
- 能力：`Input`（text/image）、`Reasoning`、`ThinkingLevelMap`；
- 限制：`ContextWindow`（默认 128000）、`MaxTokens`（默认 16384）；
- 成本：`Cost`；
- 其他：`Headers`。

它回答：**对谁说话、用什么协议、能做什么、多少钱。**

请求由 `TranscriptContext` + `SimpleStreamOptions` 组成；Model 只描述"对谁说话"。

---

## 4. Provider：一组 Model + 一个 StreamFn（v2：IProvider）

```csharp
IProvider { Id, Name, BaseUrl, Headers, Auth, GetModels, GetAllModels, Stream, StreamSimple }
```

- 一个 provider 可以带多个模型（同协议、不同型号）；
- `Stream`/`StreamSimple` 是 provider 的统一入口，**同步返回**事件流；
- v2 的 Provider 带 `Auth` 形状；API Key 解析可用，OAuth 行为后置 D1；
- 可选能力用独立接口（`IRefreshableProvider` / `IDeferredProvider` / `IImagesProvider` / `IClassifierProvider`），不用 DIM。

**Provider 抽象的是"怎么调用"，Model 描述的是"调用谁、有什么能力"。**

---

## 5. Models 注册表：查号台

- `SetProvider` / `DeleteProvider` / `ClearProviders`：注册生命周期；
- `GetProvider` / `GetModels` / `GetModel`：按 id 查找；
- `Stream/StreamSimple`：按 `model.Provider` 找 provider → 调 `StreamFn` → 返回 S2 流；
- 找不到 provider → 返回 **Error 事件流**，不抛异常。

它只按 `model.Provider` 分发，不按 `model.Api`；`Api` 留给 provider 内部决定协议。

---

## 6. StreamFn / Models.Stream：唯一的调用契约（v2 修正）

```csharp
IAssistantMessageEventStream StreamFn(
    Model model,
    TranscriptContext context,
    StreamOptions? options)
```

- 同步返回 S2 流；`Models.Stream` 经 `LazyStream` **返回即启动** setup/factory，不等认证/分派完成；
- setup 失败（包括 OCE）编码为 `Error(Error)`；`Aborted` 由 provider 内层流主动产出；
- 取消唯一入口是 `StreamOptions.CancellationToken`；
- 不得向调用方抛异常；Faux、OpenAI、Anthropic 都实现这一个形状。

---

## 7. Faux：不是玩具，是"假电话局"

### 上下游

```
上游：Models / AgentLoop
      给 Model + TranscriptContext + Options + CancellationToken
              │
              ▼
Faux：从脚本队列取一条 → 切成 S2 事件 → 推流
              │
              ▼
下游：AgentLoop / 测试 / UI
      消费 S2 事件，拿最终 AssistantMessage
```

它在系统里的位置**不是"测试代码"**，而是"注册表里的一个 provider"：

```csharp
models.SetProvider(faux.Provider);      // 测试
// 或
models.SetProvider(deepSeek.Provider);  // 生产
```

### 为什么需要它

- 无网、无 key、CI 可跑；
- 在 S4/S5 尚未实现时先开发/测试 S7；
- 精确制造文本、思考、工具调用、错误、取消、流式节奏；
- 给真实 provider 当事件序列的对照基准。

### 确定性

- ID 用每 provider 单调计数器（`tool:1`…），不用时间/随机；
- 分块固定，不随机；
- 时间戳只通过注入的 `IClock`；
- 默认零延迟；`TokensPerSecond` 可模拟节奏。

> Faux 是 pi 官方 `pi-ai` 包自带的 provider，pi.net 也有；我们按 S3 范围移植了确定性版本，Deferred 与认证后置。

---

## 8. 设计模式

不是一个单一 GoF 模式，而是组合：

| 结构 | 模式 |
|---|---|
| `Models` 注册表 | Registry / Service Locator |
| `IProvider.Stream` | Strategy |
| `Provider.Models` | 一对多聚合 |
| `Model.Provider` | 外键 / 反向引用 |
| `FauxProvider` | Fake Object（测试替身） |
| S4/S5 adapter | Adapter |

最接近的 .NET 现成类比是 **`DbProviderFactory`**：注册表按名字找 provider，provider 提供创建连接的实现。

双向结构（Provider 有 Models、Model 有 Provider）是为了让 Model 可序列化、可分发表，同时注册表能按外键解析到实现。

---

## 9. 边界（S3 不做什么）

- 不解析 HTTP/SSE（S4/S5）；
- **API Key 解析 + 内存凭据 store 已实现**；OAuth/持久化凭据 D1/D7 后置 S5；
- 不做动态模型目录刷新（D2，S4）；
- 不做图片/分类 provider 真实实现（D4/D5，2B/后续；接口与 stub 已在 S3）；
- 不实现 Deferred 行为（D3，S8；接口与 stub 已在 S3）；
- 不执行工具、不跑 agent loop（S6/S7）。

---

## 10. 常见误区

1. **"Provider 就是厂商 SDK"** —— 不是；Provider 是 pi 的绑定记录，SDK 只是 `StreamFn` 内部实现。
2. **"Model 就是请求"** —— 不是；Model 是描述，请求由 context/options 组成。
3. **"注册表必须是全局单例"** —— 不是；可实例化，测试可隔离。
4. **"Faux 只是占位"** —— 不是；它是确定性测试替身。
5. **"未知 provider 应该抛异常"** —— 不；按 S2 契约编码成 Error 事件。
6. **"S3 要处理所有认证"** —— 不；S3 只补 Auth 形状 + API Key 解析/内存 store；OAuth 登录/刷新与持久化 D1/D7 在 S5。

---

## 11. 验收问题

1. Model 和 Provider 分别抽象了什么？为什么不能合并成一个？
2. 为什么 Provider 要同时绑定"一组 Models"和"一个 StreamFn"？
3. 未知 provider 为什么返回错误事件，而不是抛异常？
4. Faux 的上游和下游分别是谁？
5. Faux 的确定性具体体现在哪几个点？
6. S3 和 S4/S5 的边界在哪？

---

## 12. 延伸阅读

- `docs/learning/S3-Provider抽象与Faux-契约篇.md`：十张契约卡。
- `docs/superpowers/specs/2026-11-05-S3-Provider抽象与Faux-design.md`：S3 设计决策与偏离。
- pi `packages/ai/src/providers/faux.ts`：官方 Faux 的完整能力（含 Deferred）。
- pi.net `src/Pi.Ai/Providers/Faux.cs`：C# 版 Faux 与更多辅助类型。
