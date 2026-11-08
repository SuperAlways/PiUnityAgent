# Providers · 上游映射（pi v1.5.0）

| 上游 | 我们 | 说明 |
|---|---|---|
| `packages/ai/src/types.ts` `Model` / `BaseModel` | `Types/Model.cs` | `ProviderModel` 继承族 + `Type` 判别；无泛型 api 约束 |
| `packages/ai/src/models.ts` `Models` | `Providers/Models.cs` + `Models.*.cs` | 完整门面形状（S3 v2）；刷新 D2/Deferred D3/图片 D4/分类 D5 stub 后置 |
| `packages/ai/src/providers/faux.ts` `createFauxCore` | `Providers/Faux/FauxProvider.cs` + `Faux.cs` | 脚本队列/工厂/状态；无 deferred（D3） |
| `pi.net Providers/Faux.cs` | 同名 | pi.net 的 auth/ModelSpec/Deferred 不采用 |
| `pi.net Models/Models.cs` | `Models.cs` | 同步 Stream/LazyStream 形状对齐 pi |

## 2.1 适配

- ToolCall id 可省（`Faux.ToolCall(name, args, id?)`）；缺省 id 由 provider 单调计数器
  在发射前规范化为 `tool:{n}`（`FauxProviderState.NextToolCallIndex`），不用时间/随机；
- 时间用 `IClock`；
- 分块固定大小；
- 默认零延迟；`TokensPerSecond` 仅 Faux 选项；
- 无 HTTP/SSE/Auth。

## 2.2 已知偏差（如实记录）

- 有效分块大小固定为 `min(MinTokenSize, MaxTokenSize)`，`MaxTokenSize` 只起钳位作用，
  不做随机区间取值（pi/pi.net 是随机大小分块）；
- `ToolCallDelta` 的 partial 快照携带 `Arguments = {}` 起步，不随 delta 增长
  （pi 会逐步拼出完整参数）；完整参数只在 `ToolCallEnd` 出现；
- 空队列错误（"No more faux responses queued"）携带零 usage；
- 中途取消/中止的终态文案为 `"Request was aborted"`，消息取流上最新 Partial 快照
  （保留已累积内容、Usage 与原时间戳）；工厂等待期取消为空内容 + `_clock.UtcNowMs()`。

## S3 返工 v2 新增映射

| 上游 | 我们 | 说明 |
|---|---|---|
| `Provider` | `IProvider.cs` | 接口 + 可选能力接口；不用 DIM；v3 起 `Auth` 必需（放弃 pi.net C43 可空） |
| `Provider` capability methods | `ProviderCapabilities.cs` | `IRefreshableProvider` / `IDeferredProvider` / `IImagesProvider` / `IClassifierProvider`；不用 DIM |
| `createProvider` | `StaticProvider.cs` | 静态 provider 骨架 |
| `Models` | `Models.cs` + `Models.*.cs` | 注册/认证/流式/能力分 partial |
| `auth/types.ts` | `Auth/*.cs` | 类型面；API Key 解析可用 |
| `lazyStream` | `Events/LazyStream.cs` | 同步返回、返回即调度启动（Task.Run） |
| `providers/faux.ts` | `Faux/FauxProvider.cs` | 迁移到 IProvider/同步流/单取消 |

## S3 v3 偏离补齐新增映射

| 上游 | 我们 | 说明 |
|---|---|---|
| `auth/types.ts`（ModelAuth/AuthResult/AuthCheck） | `Auth/AuthTypes.cs` | `AuthResult(Auth, Env, Source)`；未配置=`AuthResult?` null；`AuthCheck.Type` 必填 |
| `auth/types.ts`（authContext env/fileExists） | `Auth/AuthContext.cs` | `IAuthContext` + `DefaultAuthContext`（process env + File.Exists） |
| `auth/types.ts`（Credential/CredentialStore） | `Auth/Credential.cs` | OAuthCredential 三字段 required；store=`Read/List/Modify/Delete`；modify null=不变、返回写后值，per-provider `SemaphoreSlim` |
| `auth/types.ts`（ApiKeyAuth/OAuthAuth） | `Auth/ProviderAuth.cs` | `ApiKeyAuth.Resolve` 必需（Check/Login 可选，Check 权威）；`OAuthAuth`（Login/Refresh/ToAuth，无 Logout=store delete） |
| `models.ts`（modelsStore/publication） | `Auth/ModelsStore.cs` | `IModelsStore`/`ModelsStoreEntry`（models/lastModified/checkedAt/etag）/`ModelsPublication`（persist 与 delete 二选一） |
| `createProvider` | `CreateProvider.cs` + `ProviderFactory.cs` | `CreateProviderOptions`（`Models` required，回归 pi 形状）；单 `Api` 或 `ApiByApi` 按 `model.Api` 分派；“至少一个实现”校验数**非 null 实现**（models.ts:1087-1091）——**空/全 null `ApiByApi` 无其他实现抛 `ModelsError(Provider)`**，缺 api 终态 `ModelsError(Stream)`；Images/Classifiers 产物实现能力接口 |
| `Provider.streamSimple` | `ProviderStreams.cs` | `SimpleStreamFn` 独立 delegate；`FetchDeferred`/`CancelDeferred` 形状（D3） |
| `filterModels`/`filterAllModels` | `ProviderCapabilities.cs` | `IFilterableProvider`；`FilterAllModels` 未实现返回 null，Models fallback 保序 |
| `ModelsError` | `ModelsError.cs` | `ModelsErrorCode` 六值（ModelSource/ModelValidation/Provider/Stream/Auth/OAuth） |
| `calculateCost`/thinking/equality 工具 | `ModelOperations.cs` | `CalculateCost`/`GetSupportedThinkingLevels`/`ClampThinkingLevel`/`ModelsAreEqual`/`HasApi`/`IsModelType` |
| `api/lazy.ts` observer | `Events/LazyStreamObserver.cs` | `ILazyStreamObserver` 为 `LazyStream.Run` 第三参；Push→`OnEvent`、End→`OnResult`；setup 失败 `OnResult(错误终态消息)` |
