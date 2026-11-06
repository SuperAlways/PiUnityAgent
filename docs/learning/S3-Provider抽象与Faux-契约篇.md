# S3 契约篇 · Provider / Model / Faux

> **阶段**：S3（pi-ai provider 抽象 + Faux）。
> **用途**：复习时只看这里；字段细节查源码。
> **实现**：`src/PiUnityAgent.Ai/Providers/`、`Types/Model.cs`、`Types/SimpleStreamOptions.cs`、`Types/StreamFn.cs`。
> **配套**：`S3-Provider抽象与Faux-概念篇.md`、`Q&A.md` Q18–Q23。

---

## 强制级别总表

| 契约 | 强制级别 | 谁来保证 |
|---|---|---|
| Model 是描述，不是请求 | 类型 + 约定 | `Model.cs` |
| Provider 绑定一组 Model + 一个 StreamFn | 类型 + 约定 | `Provider.cs` |
| 注册表按 `Model.Provider` 分发 | 运行时 | `Models.cs` |
| 未知 provider → Error 事件，不抛 | **运行时硬契约** | `Models.StreamSimpleAsync` |
| StreamFn 永不抛 | **运行时兜底 + 生产者责任** | `Models` + 各 provider |
| 取消归 Aborted，失败归 Error | 运行时 | `Models` + Faux |
| 公共选项不混入测试旋钮 | 类型边界 | `SimpleStreamOptions` vs `FauxProviderOptions` |
| Faux 确定性 | 约定 + 测试 | Faux 实现 |
| Faux 脚本/终态语义 | 运行时 | Faux 实现 |
| 注册表/队列并发安全 | 运行时 | lock / Interlocked |

---

## 卡 1 · Model 是"描述"，不是"请求"

- **必须成立**：
  - `Model` 只描述身份、能力、限制、成本；
  - 不持有上下文、不持有选项、不发请求；
  - `Api` 是协议 id（`openai-completions` / `anthropic-messages` / `faux`）；
  - `Provider` 是注册表键，必须与所属 `Provider.Id` 一致；
  - `ThinkingLevelMap`：缺键 = 厂商默认，`null` = 该档不支持。
- **在哪强制**：`required` 字段 + 类型；Provider 一致性靠约定。
- **破坏后果**：注册表找不到 provider；下游把 Model 当请求传，上下文丢失。
- **验收问法**：`Model` 和 `SimpleStreamOptions` 各回答什么问题？

> 答案：Model 回答"对谁说话、能做什么、多少钱"；Options 回答"这一次怎么说话"。

---

## 卡 2 · Provider = 一组 Model + 一个 StreamFn

- **必须成立**：
  - Provider 是"厂商/协议族"的绑定记录；
  - 一个 Provider 可以带多个 Model；
  - `Stream` 是该族模型的统一入口；
  - `Models` 列表构造后视为不可变；
  - S3 的 Provider 不含认证信息。
- **在哪强制**：record 形状 + 约定。
- **破坏后果**：每个模型各写一份 HTTP 逻辑；注册表无法按族管理；认证污染接口。
- **验收问法**：为什么不给每个 Model 单独配一个 StreamFn？

> 答案：同族模型共用调用逻辑；Provider 负责"怎么调用"，Model 负责"调用谁"。

---

## 卡 3 · 注册表按 `Model.Provider` 分发

- **必须成立**：
  - `SetProvider` 按 `Provider.Id` **替换**；
  - `DeleteProvider` 返回是否删除；
  - `GetProvider` 找不到返回 `null`；
  - `GetModels(provider)` 过滤；`GetModels(null)` 返回全部；
  - `GetModel(provider, id)` 找不到返回 `null`；
  - `StreamSimpleAsync` 只按 `model.Provider` 查找，不按 `model.Api`。
- **在哪强制**：`Models.cs` 运行时；测试覆盖。
- **破坏后果**：换 provider 后旧实例残留；id 不一致导致"注册了却找不到"。
- **验收问法**：`model.Provider = "deepseek"`，注册表里只有 `"openai"`，会发生什么？

> 答案：返回 Error 事件流（`Unknown provider: deepseek`），不抛。

---

## 卡 4 · 未知 provider 也是事件，不是异常

- **必须成立**：
  - 找不到 provider → 返回一个 `Error` 终态的事件流；
  - 不抛异常；
  - 错误消息可读（`Unknown provider: {id}`）；
  - 这是 S2 "失败是数据"在 S3 的延续。
- **在哪强制**：`Models.StreamSimpleAsync` 运行时硬契约；测试覆盖。
- **破坏后果**：agent loop 被异常打断；玩家看到崩溃而不是"模型不可用"。
- **验收问法**：为什么未知 provider 不直接 `throw`？

> 答案：S7 应通过统一的终态事件处理失败，而不是为 S3 单独写 try/catch。

---

## 卡 5 · StreamFn 永不抛

- **必须成立**：
  - `StreamFn` 返回 `Task<IAssistantMessageEventStream>`；
  - 同步异常和异步异常都不能穿透调用方；
  - 失败编码为 S2 终态事件；
  - `Models.StreamSimpleAsync` 兜底：
    - `OperationCanceledException` → `Error(Aborted)`；
    - 其他异常 → `Error(Error)`；
  - 若 provider 已返回流、之后流自身故障，属于 S2 `Fail`（基础设施），不是 StreamFn 的同步失败。
- **在哪强制**：`Models.cs` 的 try/catch；各 provider 责任；测试覆盖。
- **破坏后果**：S7 到处写 try/catch；失败语义分裂成"异常"和"事件"两套。
- **验收问法**：provider 内部抛 `InvalidOperationException`，调用方拿到什么？

> 答案：一个 Error 事件流；不是异常。

---

## 卡 6 · 取消归 Aborted，失败归 Error

- **必须成立**：
  - `OperationCanceledException` → `StopReason.Aborted`；
  - 其他失败 → `StopReason.Error`；
  - 两个取消通道都要尊重：
    - `StreamFn` 的 `CancellationToken`；
    - `SimpleStreamOptions.CancellationToken`；
  - Faux 用 linked CTS 同时监听两者；
  - 取消前已生成的内容要保留（S2 partial 语义）。
- **在哪强制**：`Models` 的 catch 分支；Faux 的 linked CTS 与 abort 快照。
- **破坏后果**：用户停止被当成环境错误 → 自动重试；取消后丢掉半截内容。
- **验收问法**：用户点了停止，最终消息的 `StopReason` 应该是什么？

> 答案：`Aborted`，不是 `Error`。

---

## 卡 7 · 公共选项不混入测试旋钮

- **必须成立**：
  - `SimpleStreamOptions` 只放**请求级**参数（ApiKey/BaseUrl/MaxTokens/Temperature/…/CancellationToken）；
  - `TokensPerSecond`、`MinTokenSize`、`MaxTokenSize` 只属于 `FauxProviderOptions`；
  - 测试旋钮不污染公共 API。
- **在哪强制**：类型边界（不同类）；约定。
- **破坏后果**：真实 provider 被迫实现无意义的测试参数；公共选项越来越脏。
- **验收问法**：为什么 `TokensPerSecond` 不在 `SimpleStreamOptions` 里？

> 答案：它是 Faux 的模拟节奏，不是真实模型请求参数。

---

## 卡 8 · Faux 确定性

- **必须成立**：
  - ID 用每 provider 单调计数器（`tool:1`…），不用时间/随机；
  - 分块大小固定（`min(MinTokenSize, MaxTokenSize)`），不随机；
  - 时间戳只通过注入的 `IClock`；
  - 同一脚本、同一 provider 实例，事件序列可重复；
  - `CallCount` 用 `Interlocked`。
- **在哪强制**：Faux 实现 + 测试；无运行时校验。
- **破坏后果**：测试不确定；"两次运行结果一致"无法断言。
- **验收问法**：两个独立的 Faux 实例跑同一脚本，工具调用 ID 相同吗？

> 答案：相同（每个 provider 从 1 开始），这是有意偏离 pi 的随机 ID。

---

## 卡 9 · Faux 脚本与终态语义

- **必须成立**：
  - 队列线程安全；`SetResponses` 替换、`AppendResponses` 追加；
  - 每次调用取一条；队列空 → `Error`；
  - `Factory` 抛错 → `Error`，不穿透；
  - `StopReason.Pending` → `Error`（"ended without a stop reason"）；
  - 脚本 `Error/Aborted` → 先回放内容，再发终态 `Error`；
  - 未知 `ContentBlock` → 受控抛错，落为 `Error` 事件；
  - 无 id 的 `ToolCall` 在**任何事件发出前**统一分配 `tool:{n}`。
- **在哪强制**：Faux 实现 + 测试。
- **破坏后果**：测试拿到半成品；事件里的 ID 与最终消息不一致；异常穿透。
- **验收问法**：Faux 脚本里放一个没有 id 的 ToolCall，ToolCallStart 和 Done 里的 id 一致吗？

> 答案：一致，规范化发生在推任何事件之前。

---

## 卡 10 · 并发与生命周期

- **必须成立**：
  - 注册表字典有锁，读操作返回快照/副本；
  - Faux 队列有锁；
  - `CallCount` 用 `Interlocked`；
  - 事件流单消费者；
  - `FauxProvider` 的发射是 fire-and-forget，但所有抛出路径都被 `EmitAsync` 捕获；
  - 已知限制：`Models` 的并发替换与读取是"锁内复制"，不是无锁快照。
- **在哪强制**：运行时实现。
- **破坏后果**：并发注册/读取抛 `InvalidOperationException`；事件丢失。
- **验收问法**：一边 `SetProvider` 一边 `GetModels`，会崩吗？

> 答案：不会；字典访问在锁内，结果返回副本。

---

## S3 相对 pi / pi.net 的偏离

| 项 | pi / pi.net | 我们 | 原因 |
|---|---|---|---|
| Api 类型 | 泛型 `Model<TApi>` | `string Api` | 避免泛型爆炸；运行时校验 |
| 认证 | pi.net 集成 Auth/ModelSpec | S3 无 auth | S4/S5 再引入 |
| 注册表 | 完整目录/刷新/图片/分类 | 最小 chat 注册表 | S3 范围 |
| Faux ID | 时间+随机 | 单调计数器 | 确定性测试 |
| Faux 分块 | 随机 3–5 token | 固定 chunk | 确定性测试 |
| Deferred | 支持 fetch/cancel | 类型保留、行为后置 | S3 范围 |
| Usage | 估算 + prompt cache | 简化估算、成本 0 | 测试用，不是计费 |

---

## 下游义务（S4/S5/S7 必须遵守）

1. **S4/S5**：实现 `StreamFn`，失败编码为事件，不抛；取消映射到 `Aborted`。
2. **S4/S5**：`Model.Provider` 必须与注册的 `Provider.Id` 一致。
3. **S7**：只依赖 `IModels`/`StreamFn`，不直接 new 厂商 SDK。
4. **测试**：用 Faux 时不要依赖墙钟；用注入的 `IClock` 或忽略时间戳。
5. **任何人**：不要把 `TokensPerSecond` 之类测试旋钮塞进 `SimpleStreamOptions`。

---

## 验收问题

1. Model 和 Provider 分别抽象了什么？为什么不能合并成一个？
2. 未知 provider 为什么返回 Error 事件而不是抛异常？
3. `OperationCanceledException` 和普通异常在 S3 里分别变成什么？
4. Faux 的确定性体现在哪四点？
5. 为什么 `TokensPerSecond` 不属于 `SimpleStreamOptions`？
6. 无 id 的 ToolCall，ID 是在什么时候、由谁分配的？
