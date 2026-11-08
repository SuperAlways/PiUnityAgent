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
| `SystemMessage` | `SystemMessage.cs` | `content: string \| TextContent[]` → `required MessageContent`；`Type` 不参与 JSON |
| `UserMessage` | `UserMessage.cs` | `content: string \| (Text \| Image)[]` → `required MessageContent` |
| `AssistantMessage` | `AssistantMessage.cs` | 内容块联合 → `IReadOnlyList<ContentBlock>` + 运行时校验 |
| `ToolResultMessage` | `ToolResultMessage.cs` | 条件泛型 → 非泛型 + `object? Details`；必填字段用 `required` |
| `Context` | `Context.cs` | 同名 record |
| `TranscriptContext` | `Context.cs` | 同名 record（无 branded type） |
| `Message` 联合 | `Message.cs` + 派生 record | `MessageValidation` 负责运行时不变量 |
| `AssistantMessageDiagnostic` / `DiagnosticErrorInfo` | `AssistantMessageDiagnostic.cs` | 同名 record；`code` 用 `JsonNode` |
| `DeferredHandle` | `DeferredHandle.cs` | 同名 record |
| `ImagesContext` / `ImagesStopReason` / `AssistantImages` | `ImagesTypes.cs` | 输入/输出用 `IImagesInputContent` / `IImagesOutputContent` |
| `Classifier*` | `ClassifierTypes.cs` | question/answer 联合 → 抽象 record |
| `NestedToolCallRecord` / `NestedToolCalls` | `NestedToolCalls.cs` | status 用 enum |

## 已验证的 2.1 适配

- `JsonPolymorphic`/`JsonDerivedType` 在 netstandard2.1 + STJ 8.0.5 可编译；
- `ContentBlock.Type` 必须 `[JsonIgnore]`，否则与 `type` 判别符冲突；
- `required` 通过 `RequiredMemberAttribute` 等 polyfill 支持。

## 保留但暂不使用的上游字段

`TextSignature`、`ThinkingSignature`、`ThoughtSignature`、`Namespace`、`CacheWrite1h`、`Reasoning`、`RawStopReason`、`EndTurn`、`DurationMs`、`ConstrainedSampling`。

## 类型保留、实现后置（A 方案）

`ImagesContext`、`ImagesOutputContent`、`ClassifierContext`、`NestedToolCalls`、`DeferredHandle`、`AssistantMessageDiagnostic` 已保留类型；未实现的部分标 `TODO` / `NotSupportedException`，不删除、不改语义。

`AssistantMessageEvent` 属于 S2；provider 专属字段按 provider 阶段补齐。

## S3 返工 v2 新增映射

| 上游 | 我们的文件 | 说明 |
|---|---|---|
| `FetchFunction` / `fetch?` | `Fetch.cs` | PiUnityAgent 自有端口；Host 适配器在 S13/S4 |
| `ProviderModel` / `Model` / `ImageModel` / `ClassifierModel` | `Model.cs` | C# 继承 + `Type` 判别 |
| `StreamOptions` / `SimpleStreamOptions` | `StreamOptions.cs` / `SimpleStreamOptions.cs` | 基类 + 派生 |
| `CacheRetention` / `Transport` / `ToolChoice` | `StreamOptionTypes.cs` / `Fetch.cs` | enum + wire converter |
| `normalizeContext` | `TranscriptNormalization.cs` | 前导 system 折叠 |

## S3 v3 偏离补齐新增映射

| 上游 | 我们的文件 | 说明 |
|---|---|---|
| `StreamOptions`（v3 修订） | `StreamOptions.cs` | 删 `BaseUrl`/`StopSequences`；增 `TransformHeaders`（Models 摘除后执行）/`OnPayload`（`JsonNode?`）；`TelemetrySpan`/`StreamOptions.Telemetry` 保留为 D8 最小壳 |
| `SimpleStreamOptions.reasoning` | `SimpleStreamOptions.cs` | `Reasoning`（`ThinkingLevel?`）；`ToolChoice`/`Deferred`/`ThinkingBudgets` 保留 |
| `ImagesOptions` / `ClassifierOptions` | `ImagesOptions.cs` / `ClassifierOptions.cs` | pi 独立选项面；C# 以 `: StreamOptions` 承载（B/D 登记） |
| `SimpleStreamFn` | `StreamFn.cs` | 与 `StreamFn` 并列的独立 delegate（`ProviderStreams.StreamSimple` 用） |
| `ClientIdentity`（UserAgentMode/ClientAttribution/Version） | `ClientIdentity.cs` | `UserAgentMode`/`ClientAttribution`/`AttributionSet`/`Version` + `ClientIdentityMerger.Merge` 逐字段合并；删除自造 `Headers` 字段 |

> 注：`AuthContext`/`AuthResult`/`CredentialStore`/`ModelsStore`/`ProviderFactory`/`ModelOperations`/`LazyStreamObserver` 物理落位在 `Providers/`（含 `Providers/Auth/`）与 `Events/`，映射见 `Providers/_mapping.md`。
