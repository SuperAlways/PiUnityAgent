# S3 v2 补充 · IProvider / Models / Auth / Fetch

> **阶段**：S3 对齐返工 v2（2026-11-07）；**v3 偏离补齐见 §6（2026-11-08，以 v3 为准）**。
> **定位**：概念篇/契约篇的初版讲解仍可读；本文只记录 v2 的最终形状与边界。
> **权威**：`docs/07-通讯层集成契约.md`（Fetch 端口）、`docs/superpowers/specs/2026-11-07-S3-对齐返工-design-v2.md` 与 `docs/superpowers/specs/2026-11-08-S3-v3-偏离补齐-design.md`（v3）。

---

## 1. 最终形状

```text
Context（原始 systemPrompt/tools）
  │ TranscriptNormalization.NormalizeContext
  ▼
TranscriptContext（前导 SystemMessage）
  │
IProvider.Stream(TranscriptContext, StreamOptions)
  │
LazyStream（同步返回，返回即调度启动（Task.Run））
  │
IAssistantMessageEventStream
```

- `Stream`/`StreamSimple` 同步返回；未知 provider、认证缺失、factory 异常都编码为终态事件。
- `StreamOptions.CancellationToken` 是唯一取消入口。
- `IProvider.GetAllModels()` 必存在；可选能力用 `IRefreshableProvider`/`IDeferredProvider`/`IImagesProvider`/`IClassifierProvider`。

## 2. Fetch 端口（B 方案）

- `FetchFunction` / `FetchRequest` / `FetchOptions` / `IFetchResponse` / `FetchException` / `TransportKind` 定义在 `PiUnityAgent.Ai.Types`。
- 传输只吐 `Stream` 字节；SSE/JSON 聚合留在 S4/S5。
- `Ai` 零 `UnityEngine` / `UnityLlmTransport`；`UnityLlmTransportFetch` 适配器在 S13/S4。

## 3. Auth 与 Models

- `Models` 公开入口接收原始 `Context`，内部 normalize。
- API Key 解析顺序（**v3 已修订，见 §6**）：stored credential owns provider——显式 `ApiKey` override → stored OAuth → stored API key → 仅无 stored 时 ambient `Resolve`；stored 类型无匹配返回 null，不回落 ambient。
- `Models.RefreshAsync` 在 S3 返回空结果；OAuth/Deferred/图片/分类为 `NotSupportedException`，登记 D1/D2/D3/D4/D5。

## 4. 后置边界

| ID | 能力 | 目标阶段 |
|---|---|---|
| D1 | OAuth 登录/刷新/登出 | S5 |
| D2 | 动态模型目录刷新 | S4 |
| D3 | Deferred stream/fetch/cancel | S8 |
| D4 | 图片 provider | 2B/后续 |
| D5 | 分类 provider | 2B/后续 |
| D6 | Prompt cache 行为 | S4/S5 |
| D7 | 持久化凭据 store | S5 |
| D8 | Telemetry 行为 | 后续 |
| D9 | 模型目录数据 | S4 |
| D10 | provider 可用性过滤增强 | S4/S5 |
| D11 | transcript 重放辅助 | S9 |
| D12 | Auth 解析取消 + 可用性单次解析 | S4 |
| D13 | Fetch 超时/无默认 fetch 测试补齐 | S4 |

## 5. 验收问法

1. 为什么 `Models.Stream` 同步返回而不是 `Task<stream>`？
2. 未知 provider / 认证缺失 / factory 异常分别变成什么终态？
3. `Fetch` 端口为什么不解析 `[DONE]`？
4. 为什么 `Provider` 改为 `IProvider` 而不是继续用 record + 委托？
5. 哪些行为后置、目标阶段分别是什么？

## 6. S3 v3 偏离补齐（2026-11-08，以此为准）

> 驱动：`docs/audits/2026-11-08-S3-v2-deviation-audit.md`（FAIL）；spec：`docs/superpowers/specs/2026-11-08-S3-v3-偏离补齐-design.md`。以下与 §1/§3 冲突处一律以本节为准。

**Auth 形状**（`Providers/Auth/*.cs`）：

- `AuthResult(ModelAuth Auth, Env, Source)` 是请求时最终载荷；**未配置认证 = `AuthResult? == null`**，不是 `AuthCheck(false)`。`AuthCheck(AuthType Type, Source?)` 的 `Type` 必填。
- `CredentialStore` 只有四法：`Read/List/Modify/Delete`；`ModifyAsync` 的回调返回 `null` = 保持不变（不是删除），返回值为写后凭据；内存实现按 provider `SemaphoreSlim` 串行，不在 `lock` 内阻塞异步回调。旧 S3 发明的 `GetAsync/SetAsync` 已删。
- `IProvider.Auth` **必需**（pi 形状；放弃 pi.net C43 的可空便利）；Faux 提供无密钥 `ApiKeyAuth`（Resolve 返回空 `ModelAuth`，Source `"Faux"`）。
- 解析顺序：显式 `overrides.ApiKey`（合成凭据带 Env 走 provider `Resolve`）→ stored OAuth `ToAuth` → stored API key `Resolve` → 仅无 stored 时 ambient；**stored 类型无匹配返回 null，不回落 ambient**；provider/store 异常统一包 `ModelsError(Auth/OAuth)`；`GetAuth(model)` 会合并 `model.Headers`。
- `OAuthAuth` 无 `Logout`（登出 = `CredentialStore.DeleteAsync`）；`OAuthCredential` 的 refresh/access/expires 三字段 `required`。

**Models/Provider 门面**：

- `ModelsError`/`ModelsErrorCode`（ModelSource/ModelValidation/Provider/Stream/Auth/OAuth）替代裸异常；例外：流式 setup 的未知 provider 现抛 `InvalidOperationException`，由 LazyStream 编码为终态 Error（实现口径，登记 B）。
- `ProviderFactory.Create(CreateProviderOptions)`：`Models` **required**（回归 pi 形状；pi.net 默认 `[]` 不采）；单 `Api` 或 `ApiByApi` 按 `model.Api` 分派；“至少一个实现”校验数**非 null 实现**（对齐 pi `models.ts:1087-1091`）——**空/全 null `ApiByApi` 无其他实现抛 `ModelsError(Provider)`**（预审计修正，推翻早前“空表合法”裁定），合法 provider 缺 api 在 Stream 终态 `ModelsError(Stream)`；有 `Images/Classifiers` 时产物实现 `IImagesProvider`/`IClassifierProvider`。`StaticProvider` 保留为唯一额外公共便捷类型（D 登记）。
- 可用性过滤拆到 `IFilterableProvider`（`FilterModels` + nullable `FilterAllModels`，未实现返回 null；fallback 保序按 chat id 过滤）；`IRefreshableProvider` 只剩 refresh。
- `ClientIdentity` 改 pi 形状：`UserAgentMode`/`ClientAttribution`/`AttributionSet`/`Version` + `ClientIdentityMerger.Merge` 逐字段合并（不是 `??`）；删除自造 `Headers` 字段；身份头生成属 D8 后置。

**LazyStreamObserver / TransformHeaders / Options**：

- `LazyStream.Run` 第三参改为 `ILazyStreamObserver?`；顺序按 pi `api/lazy.ts`：外层 `Push` 之后 `OnEvent`、外层 `End` 之后 `OnResult`；setup 失败 `OnResult(错误终态消息)`（不是 null）。遥测（D8）以 observer 包装接入。
- `StreamOptions.TransformHeaders` 是 pi `ModelsRequestTransforms` 的 C# intersection 模拟：Models 在进 provider 前执行并置 null，provider 不感知。
- Options 严格化：删 `StreamOptions.BaseUrl/StopSequences`；`SimpleStreamOptions.Reasoning`（`ThinkingLevel?`）；`SimpleStreamFn` 为独立 delegate；新增 `ImagesOptions`/`ClassifierOptions`（`: StreamOptions`，B/D 登记）；`OnPayload` 用 `JsonNode?`。
- `ModelOperations` 补 pi 公共工具函数：`CalculateCost/GetSupportedThinkingLevels/ClampThinkingLevel/ModelsAreEqual/HasApi/IsModelType`。
