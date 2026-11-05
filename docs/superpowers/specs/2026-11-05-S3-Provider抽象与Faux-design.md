# S3 设计 spec · Provider 抽象与 Faux（2026-11-05）

> **状态**：讨论稿 v0.1，待用户 review。
> **阶段**：S3（pi-ai provider 抽象 + Faux provider）。
> **参考版本**：pi（原版）`v1.5.0` / `f9a263655`；pi.net `main` / `2d6757b`。
> **上游文件**：`pi/packages/ai/src/providers/faux.ts`、`pi/packages/ai/src/models.ts`、`pi/packages/ai/src/types.ts`（Model/BaseModel/Provider/StreamOptions）；pi.net `Pi.Ai/Providers/Faux.cs`、`Pi.Ai/Models/Models.cs`、`Pi.Ai/Models/ModelCatalog.cs`、`Pi.Ai/Types/AssistantMessageEvent.cs`。
> **前置**：S1 消息模型、S2 事件流已完成（`main` `cd2f15e`）。
> **范围边界**：S3 只做 **模型描述、provider 注册表、StreamFn 契约、Faux provider**；**不做 HTTP/SSE、OAuth、图片/分类 provider、动态模型刷新**（S4/S5/后续）。

---

## 一、S3 在系统中的位置

```
Agent Loop（S7）
    │ 只依赖 StreamFn
    ▼
Models / Provider 注册表（S3）
    ├─ Faux Provider（S3，脚本化、确定性、不联网）
    ├─ OpenAI-compatible（S4）
    └─ Anthropic（S5）
    │
    ▼
AssistantMessageEventStream（S2）
    │
    ▼
AssistantMessage（S1）
```

S3 解决四件事：

1. **Model**：描述“哪个模型、哪个协议、什么限制/价格”；
2. **Provider**：把一组模型和一个 `StreamFn` 绑定；
3. **Models 注册表**：按 provider/model 查找并分发请求；
4. **Faux**：一个由脚本决定响应的假 provider，让 S7 无网可跑。

---

## 二、范围

### 2.1 包含

- `Model` / `ModelCost` / `ModelInputModality`（chat 模型）；
- `Provider`（Id / Name / Models / StreamFn）；
- `IModels` / `Models` 注册表（Set/Delete/Clear/Get/StreamSimple）；
- `StreamFn` 委托（`Model + TranscriptContext + options + ct → Task<IAssistantMessageEventStream>`）；
- `SimpleStreamOptions`（S3 需要的字段子集 + 未来 provider 用的可选字段）；
- `FauxProvider`（脚本队列、状态、文本/思考/工具调用/错误、确定性分块、可选速率）；
- 单测：注册表、Faux 事件序列、脚本/状态/取消/错误。

### 2.2 不包含（S4/S5/后续）

- HTTP、SSE、WebSocket、连接池、代理；
- API Key 解析、凭据存储、OAuth、登录/登出；
- 动态模型目录刷新（`fetchModels`/`remote catalog`）；
- 图片生成/分类 provider；
- Faux 的 deferred fetch/cancel（类型已在 S1，行为后置）；
- Agent Loop、工具执行（S6/S7）。

---

## 三、Model

### 3.1 字段

对应 pi `BaseModel`/`Model` 的 chat 子集：

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | string | 模型 id |
| `Name` | string | 显示名 |
| `Api` | string | 协议 id（`openai-completions`、`anthropic-messages`、`faux`…） |
| `Provider` | string | provider id |
| `BaseUrl` | string | 端点 |
| `Input` | `IReadOnlyList<ModelInputModality>` | text / image |
| `Cost` | `ModelCost` | input/output/cacheRead/cacheWrite（每百万 token 或统一单位） |
| `Reasoning` | bool | 是否支持思考 |
| `ContextWindow` | int | 上下文窗口 |
| `MaxTokens` | int | 单次输出上限 |
| `ThinkingLevelMap` | `IReadOnlyDictionary<ModelThinkingLevel,string?>?` | 模型档位映射（可后置） |
| `Headers` | `IReadOnlyDictionary<string,string?>?` | 模型级请求头 |

`Model` 是**描述**，不是请求；请求由 `StreamFn` 发出。

### 3.2 辅助类型

```csharp
public enum ModelInputModality { Text, Image }

public sealed record ModelCost(
    double Input = 0,
    double Output = 0,
    double CacheRead = 0,
    double CacheWrite = 0);
```

### 3.3 与 pi 的差异

- pi 的 `Model<TApi>` 用泛型约束 api；C# 用 string `Api`（运行时校验），避免泛型爆炸；
- `Compat`（OpenAI/Anthropic 兼容参数）先不放进 `Model`，S4/S5 用 provider 配置/选项承载；
- 图片/分类 model 类型（`ImageModel`/`ClassifierModel`）不在 S3。

---

## 四、Provider 与 Models

### 4.1 Provider

```csharp
public sealed record Provider(
    string Id,
    string Name,
    IReadOnlyList<Model> Models,
    StreamFn Stream);
```

- 一个 Provider 可以包含多个 Model；
- `Stream` 是该 provider 的统一入口；
- S3 不包含认证；Faux 是 keyless。

### 4.2 StreamFn

```csharp
public delegate Task<IAssistantMessageEventStream> StreamFn(
    Model model,
    TranscriptContext context,
    SimpleStreamOptions? options,
    CancellationToken cancellationToken);
```

契约：

- **不得抛异常**；失败编码为 `error` 终态事件；
- 返回 S2 的事件流；
- 调用方（S7）不关心 provider 实现。

> 与 pi 的差异：pi 的 `StreamFn` 可同步返回 stream 或 Promise；C# 用 `Task`（pi.net 同形），但 Faux 立即返回已完成 Task。

### 4.3 Models 注册表

```csharp
public interface IModels
{
    IReadOnlyList<Provider> Providers { get; }
    Provider? GetProvider(string id);
    IReadOnlyList<Model> GetModels(string? provider = null);
    Model? GetModel(string provider, string id);

    Task<IAssistantMessageEventStream> StreamSimpleAsync(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken);
}

public sealed class Models : IModels
{
    void SetProvider(Provider provider);   // 按 Id 替换
    bool DeleteProvider(string id);
    void ClearProviders();
}
```

`StreamSimpleAsync`：

1. 按 `model.Provider` 找 Provider；
2. 找不到 → 返回一个 `error` 事件流（不抛异常）；
3. 找到 → `provider.Stream(model, context, options, ct)`。

---

## 五、SimpleStreamOptions

S3 定义字段，Faux 只消费少数；S4/S5 使用其余字段：

| 字段 | 类型 | S3 用途 |
|---|---|---|
| `ApiKey` | string? | S4/S5 |
| `BaseUrl` | string? | S4/S5 |
| `MaxTokens` | int? | Faux 可影响输出分块/用量 |
| `Temperature` | double? | S4/S5 |
| `StopSequences` | IReadOnlyList<string>? | S4/S5 |
| `SessionId` | string? | 缓存/日志（S4/S5） |
| `Headers` | IReadOnlyDictionary<string,string?>? | S4/S5 |
| `SamplingParams` | JsonObject? | S4/S5 |
| `ThinkingLevel` | ModelThinkingLevel? | S3/Faux 可影响事件类型 |
| `ToolChoice` | JsonNode? | S4/S5 |
| `TimeoutMs` | int? | S4/S5 |
| `MaxRetries` | int? | S4/S5 |
| `CancellationToken` | CancellationToken | Faux 分块中止 |

> Faux 的流式速率（`TokensPerSecond`）是测试旋钮，不放公共选项；它属于 `FauxProviderOptions`（见 §6.2），避免污染 provider 无关的 `SimpleStreamOptions`。

---

## 六、Faux Provider

### 6.1 目标

- 无网络、无 Key、确定性；
- 由脚本队列决定每次 `StreamSimpleAsync` 的响应；
- 产生与 S2 完全一致的事件序列；
- 支持文本、思考、工具调用、错误、abort、可选速率。

### 6.2 配置与默认模型

```csharp
public sealed record FauxProviderOptions
{
    public string Api { get; init; } = "faux";
    public string Provider { get; init; } = "faux";
    public IReadOnlyList<FauxModelDefinition>? Models { get; init; }
    public double TokensPerSecond { get; init; } = 0; // 0 = 无延迟
    public int MinTokenSize { get; init; } = 3;
    public int MaxTokenSize { get; init; } = 5;
    public IClock? Clock { get; init; }
}

public sealed record FauxModelDefinition
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public bool Reasoning { get; init; }
    public IReadOnlyList<ModelInputModality>? Input { get; init; }
    public ModelCost? Cost { get; init; }
    public int ContextWindow { get; init; } = 128000;
    public int MaxTokens { get; init; } = 16384;
}

public interface IClock { long UtcNowMs(); }
public sealed class SystemClock : IClock
{
    public long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
```

默认模型：

- Provider/API：`faux`
- Model id：`faux-1`
- Name：`Faux Model`
- BaseUrl：`http://localhost:0`
- ContextWindow：128000
- MaxTokens：16384
- Input：text + image
- Cost：全 0
- Reasoning：false（可配置）

### 6.3 脚本条目

```csharp
public abstract record FauxResponseStep
{
    public sealed record Response(AssistantMessage Message) : FauxResponseStep;
    public sealed record Factory(FauxResponseFactory Factory) : FauxResponseStep;
}

public delegate Task<AssistantMessage> FauxResponseFactory(
    TranscriptContext context,
    SimpleStreamOptions? options,
    FauxProviderState state,
    Model model);
```

- `Response`：静态脚本消息；
- `Factory`：按上下文/选项/状态现场生成；
- 队列空 → 返回 `error` 事件，消息为 “No more faux responses queued”。

### 6.4 工厂辅助

| 方法 | 产出 |
|---|---|
| `Faux.Text(string)` | `TextContent` |
| `Faux.Thinking(string)` | `ThinkingContent` |
| `Faux.ToolCall(name, args, id?)` | `ToolCall` |
| `Faux.AssistantMessage(content, stopReason?, errorMessage?, responseId?, timestamp?)` | `AssistantMessage`（usage 由 Faux 估算） |

### 6.5 状态与操作

```csharp
public sealed class FauxProviderState
{
    public int CallCount { get; }
}

public sealed class FauxProvider
{
    public FauxProvider(FauxProviderOptions? options = null);
    public Provider Provider { get; }
    public IReadOnlyList<Model> Models { get; }
    public FauxProviderState State { get; }
    public void SetResponses(IEnumerable<FauxResponseStep> steps);
    public void AppendResponses(IEnumerable<FauxResponseStep> steps);
    public int PendingResponseCount { get; }
    public Model GetModel(string? id = null);
}
```

- `CallCount` 每次 `StreamSimpleAsync` 递增（Interlocked）；
- 脚本队列线程安全（lock）；
- `SetResponses` 替换、`AppendResponses` 追加。

### 6.6 流式行为

每次请求：

1. 取脚本条目；空 → `Error(StopReason.Error, message)`；
2. 解析 `Factory`（异常 → `Error` 事件，不抛）；
3. 规范化 content：string → `TextContent`；单块 → 列表；列表原样；
4. 按内容块顺序发事件：
   - Text：`TextStart` → 3–5 字符 `TextDelta` → `TextEnd`；
   - Thinking：`ThinkingStart` → `ThinkingDelta` → `ThinkingEnd`；
   - ToolCall：`ToolCallStart` → 参数 JSON 分块 `ToolCallDelta` → `ToolCallEnd`；
5. 每个事件带当前 `Partial` 快照；
6. 终态：`StopReason.Pending` → `Error`（“Faux response ended without a stop reason”）；`Error/Aborted` → `Error`；其余 → `Done`；
7. 分块之间检查 `CancellationToken`；取消 → `Error(Aborted)`。

### 6.7 确定性

- **ID**：`tool:1`、`tool:2`… 用单调计数器，不用 `DateTime.Now`/`Random`；
- **Timestamp**：使用注入的 `IClock`（默认 `DateTimeOffset.UtcNow`），测试可固定；
- **分块**：固定 3–5 字符（可配置 min/max），不随机；
- **速率**：`TokensPerSecond > 0` 时按固定间隔 `Task.Delay`；默认 0，测试无延迟。

### 6.8 Usage 估算

- 输入 token：系统消息 + 消息文本长度 / 4（向上取整）；
- 输出 token：assistant 文本长度 / 4；
- `TotalTokens = input + output`；
- `Cost` 全 0；
- 这是**测试估算**，不是真实计费。

### 6.9 与 pi/pi.net 的差异

| 项 | pi / pi.net | S3 |
|---|---|---|
| ID 生成 | 时间 + 随机 | 单调计数器（确定性） |
| Deferred | Faux 支持 fetch/cancel | 不实现（类型保留） |
| Auth | Provider 需要 auth 语义 | S3 不含 auth；Faux keyless |
| Usage | 估算 + 成本 | 简化估算，成本 0 |
| Provider 注册表 | 完整 Models（认证/刷新/图片/分类） | 最小 chat 注册表 |
| Token 分块 | 随机大小 | 固定 3–5 字符（可配置） |

---

## 七、Unity / netstandard2.1 适配

| 事项 | 决定 |
|---|---|
| 目标框架 | `netstandard2.1`；测试 `net10.0` |
| 依赖 | 仅用 S1/S2 + BCL；无 Unity/HTTP/OAuth |
| 异步 | `Task` / `IAsyncEnumerable`；无 `Task.WaitAsync` |
| 取消 | `CancellationToken`；Faux 分块间检查 |
| 时间 | `IClock` 抽象（默认 UTC）；测试可注入固定时间 |
| 随机/ID | 禁止 `Random`/`DateTime.Now` 参与 ID；计数器 |
| 线程 | Faux 队列 lock；CallCount `Interlocked`；单消费者流 |
| AOT | 无反射/动态代码；Faux 纯对象构造 |
| 延迟 | 默认 0；`TokensPerSecond` 使用 `Task.Delay`，可取消 |

---

## 八、测试计划

1. **Models 注册表**：Set/Replace/Delete/Clear/GetProvider/GetModels/GetModel；
2. **StreamSimple 分发**：按 model.Provider 找到 provider；未知 provider → error 事件，不抛；
3. **Faux 文本**：`Start → TextStart → TextDelta... → TextEnd → Done` 顺序；Partial 快照；
4. **Faux 思考**：thinking 事件序列；
5. **Faux 工具调用**：toolcall start/delta/end；停止原因 ToolUse；
6. **Faux 错误**：StopReason.Error/Aborted → Error 事件；空队列 → Error；
7. **Faux 工厂**：Factory 按 context/options/state/model 生成；抛错 → Error；
8. **状态**：CallCount、Set/Append/PendingCount；
9. **确定性**：连续两次相同脚本 → 相同事件/ID（除 timestamp 可固定）；
10. **取消**：取消后 Error(Aborted)；
11. **速率**：TokensPerSecond > 0 时事件之间有时间间隔（可选，测试用宽松阈值）；
12. **Usage 估算**：文本长度 → token 估算，Cost 0。

---

## 九、验收标准

- [ ] Model/Provider/Models/StreamFn/SimpleStreamOptions 类型完整；
- [ ] Faux 能产生 S2 的 12 类事件中的文本/思考/工具调用/终态；
- [ ] 脚本队列/状态/工厂/空队列错误行为正确；
- [ ] 未知 provider 返回 error 事件流，不抛异常；
- [ ] 确定性：ID/分块/时间可固定；
- [ ] netstandard2.1 编译 0 错误；测试全绿；Release 0 错误；
- [ ] 无 HTTP/SSE/Unity/OAuth 代码。

---

## 十、开放问题

1. **Faux 的 `TokensPerSecond` 放哪**：`FauxProviderOptions`（推荐）还是 `SimpleStreamOptions`？本 spec 选前者，避免污染公共选项。
2. **Usage 估算公式**：字符/4 是近似；是否需要更精确的 tokenizer？S3 不需要。
3. **Deferred Faux**：pi/pi.net 支持；S3 先不做，等长任务/会话阶段再补。
4. **Auth**：S4/S5 再引入凭据存储；S3 的 Provider 没有 auth 字段。
5. **Image/Classifier provider**：S3 只做 chat；图片/分类 provider 后续。
