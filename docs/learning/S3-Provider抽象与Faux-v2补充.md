# S3 v2 补充 · IProvider / Models / Auth / Fetch

> **阶段**：S3 对齐返工 v2（2026-11-07）。
> **定位**：概念篇/契约篇的初版讲解仍可读；本文只记录 v2 的最终形状与边界。
> **权威**：`docs/07-通讯层集成契约.md`（Fetch 端口）与 `docs/superpowers/specs/2026-11-07-S3-对齐返工-design-v2.md`。

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
- API Key 解析顺序：request overrides → `CredentialStore` → provider `ApiKeyAuth.Resolve` → `AuthContext.Env`（由 Resolve 读取）。
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

## 5. 验收问法

1. 为什么 `Models.Stream` 同步返回而不是 `Task<stream>`？
2. 未知 provider / 认证缺失 / factory 异常分别变成什么终态？
3. `Fetch` 端口为什么不解析 `[DONE]`？
4. 为什么 `Provider` 改为 `IProvider` 而不是继续用 record + 委托？
5. 哪些行为后置、目标阶段分别是什么？
