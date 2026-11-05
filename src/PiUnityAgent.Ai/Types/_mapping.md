# Types · 上游映射（pi v1.5.0）

> 上游：`pi/packages/ai/src/types.ts`
> 原则：类型名与上游一致；C# 只做 union→record、可选字段、命名空间的机械变换。

| 上游 | 我们的文件 | 偏离 |
|---|---|---|
| `TextContent` | `TextContent.cs` | 同名 record |
| `ThinkingContent` | `ThinkingContent.cs` | 同名 record |
| `ImageContent` | `ImageContent.cs` | 同名 record |
| `ToolCall` | `ToolCall.cs` | `arguments: JsonObject` → `JsonObject Arguments` |
| `Usage` / `cost` | `Usage.cs`（`Usage` + `UsageCost`） | 嵌套 cost 拆成 record |
| `StopReason` | `StopReason.cs` | 字面量联合 → enum + `ToWire/FromWire` |
| `Tool` / `ToolReference` | `Tool.cs` | 同名 record |
| `SystemMessage` | `SystemMessage.cs` | `content: string \| TextContent[]` → `Content` / `ContentBlocks` 二选一 |
| `UserMessage` | `UserMessage.cs` | `content: string \| (Text \| Image)[]` → `Content` / `ContentBlocks` 二选一 |
| `AssistantMessage` | `AssistantMessage.cs` | 内容块联合 → `IReadOnlyList<ContentBlock>` + 运行时校验 |
| `ToolResultMessage` | `ToolResultMessage.cs` | 条件泛型 → 非泛型 + `JsonNode? Details` |
| `Context` | `Context.cs` | 同名 record |
| `TranscriptContext` | `Context.cs` | 同名 record（无 branded type） |
| `Message` 联合 | `Message.cs` + 派生 record | `MessageValidation` 负责运行时不变量 |

## 保留但暂不使用的上游字段

`TextSignature`、`ThinkingSignature`、`ThoughtSignature`、`Namespace`、`CacheWrite1h`、`Reasoning`、`RawStopReason`、`EndTurn`、`DurationMs`、`ConstrainedSampling`。

## 暂不移植

`ImagesContext`、`ImagesOutputContent`、`ClassifierContext`、`NestedToolCalls`、`DeferredHandle`、`AssistantMessageEvent`（S2）、provider 专属字段（后续按需补）。
