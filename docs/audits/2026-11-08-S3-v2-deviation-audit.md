# S3 v2 偏离审查（2026-11-08）

> 审计者：独立审计 subagent（GLM，非实现会话；只读，未改任何代码/index/HEAD）
> 参考版本：pi `v1.5.0` / `f9a263655`（已 `git log` 实核 HEAD=tag v1.5.0, 2026-11-02）；pi.net `main` / `2d6757b`（已实核，2026-11-05）；UnityLlmTransport `87db637`（**未独立核验 commit，仅凭文档锁引用**；本阶段无适配器代码，不影响结论）
> 范围：`333b131..7891bc4`（15 commits，diff 包 `.superpowers/sdd/review-333b131..7891bc4.diff` 已通读；file:line 均按 HEAD=7891bc4 工作区实读）
> 结论：**FAIL**（存在多处未登记 Shape gap，集中在 Auth 类型面与 Models 门面；按 SOP §4/§8 门禁，未修复或未经用户批准登记为 D 前不得视为可交付）

---

## 0. spec §8 三方偏离表核对结果

| §8 行 | 代码实况 | 判定 |
|---|---|---|
| Api 类型 `string Api`（A） | `Types/Model.cs:34` | ✅ 一致 |
| ProviderModel 基类 + 三派生（B） | `Types/Model.cs:30-79` | ✅ 一致 |
| 选项继承 StreamOptions→SimpleStreamOptions（B） | `Types/StreamOptions.cs:10`、`Types/SimpleStreamOptions.cs:3` | ✅ 一致 |
| API 专属选项子类（A） | 尚无子类（S4/S5） | ✅ 计划内 |
| 输入模态/档位枚举 + wire converter（A） | `Types/Model.cs:9-27`、`Types/StreamOptionTypes.cs` | ✅ 一致 |
| 取消 CancellationToken 单一入口（A） | `StreamOptions.cs:25`；`StreamFn.cs` 无 token 参数 | ✅ 一致 |
| 不用 DIM、独立能力接口（A/Unity） | `Providers/ProviderCapabilities.cs` | ✅ 一致 |
| IProvider + StaticProvider（B） | `Providers/IProvider.cs`、`StaticProvider.cs` | ✅ 一致（但见 G-B4） |
| Models 同步流 + LazyStream（B） | `Models.Streaming.cs:12-55` | ✅ 一致 |
| Fetch 端口 + Host 适配器（B/D） | `Types/Fetch.cs` 全量存在；`UnityLlmTransportFetch`/`HttpClientFetch` 不在 src（合规） | ✅ 一致 |
| TransportKind wire（A） | `Types/Fetch.cs:88-124`，wire=`auto/sse/websocket/websocket-cached` 与 pi `types.ts:147` 一致 | ✅ 一致 |
| OAuth/刷新/Deferred/图片/分类 = 形状 + stub（C） | **部分成立**：Deferred/图片/分类形状齐；**Auth 形状不齐**（见 §3 A 组） | ⚠️ 名不符实 |
| Prompt cache 字段 + 忽略（C/D6） | `Model.cs:60`、`StreamOptions.cs:33` | ✅ 一致 |
| Telemetry 最小壳（C/D8） | `StreamOptionTypes.cs:62-66`；但 `LazyStream.cs:19` 的 telemetry 参数收下即弃 | ⚠️ 见 G-B5 |
| Faux ID/分块固定（D） | `Faux.cs:78-79`、`FauxProvider.cs:389-402`；`Providers/_mapping.md` §2.2 如实登记 | ✅ 一致 |
| 未知 provider LazyStream error（D/B） | `Models.Streaming.cs:64-66` 在 factory 内抛 → `LazyStream.cs:51-66` 终态 Error | ✅ 一致 |

**总评**：§8 表内各行为真；问题在**表外**——pi 公共形状里有一批成员在 §8 与 Dn 台账均未登记就被改形/省略（§3）。

---

## 1. 公共形状矩阵

图例：✅=形状与 pi 语义等价；🟡=A/B 可接受偏离（有登记或属语言强制）；❌=Shape gap（未登记）。

| 区域 | pi 原版（v1.5.0） | pi.net（2d6757b） | 我们（7891bc4） | 判定 | 类别/缺口 |
|---|---|---|---|---|---|
| `FetchFunction` | `types.ts:152` `typeof globalThis.fetch` | —（HttpClient） | `Types/Fetch.cs:12-15` 委托（request/options/ct） | ✅ | B/D（docs/07 §二已钉） |
| `FetchRequest`（Body/BodyFactory） | RequestInit body | — | `Fetch.cs:17-25` | ✅ | D（BodyFactory 为 Unity 流式加固） |
| `FetchHeaders` 大小写不敏感/null 抑制 | header 语义 | — | `Fetch.cs:28-34` | ✅ | B |
| `FetchOptions`（Kind/超时/Env/SessionId） | RequestInit 分散 | — | `Fetch.cs:36-45` | ✅ | B/D |
| `IFetchResponse`（StatusCode/Headers/Body/Abort/IAsyncDisposable） | Response | — | `Fetch.cs:47-57` | ✅ | B |
| `FetchException` + `FetchErrorKind` | fetch 异常无分类 | — | `Fetch.cs:59-86` | ✅ | D（错误分类加固，docs/07 §3.1 映射表承载） |
| `TransportKind` | `types.ts:147` 字符串联合 | string | `Fetch.cs:88-95` 枚举 + wire converter | ✅ | A（wire 值一致） |
| `ProviderModel`/`Model`/`ImageModel`/`ClassifierModel` | `types.ts:842-904` 三接口 | `ModelCatalog.cs:70-117` 单 record + Type | `Types/Model.cs:30-79` 基类 + 三 record | ✅ | B（§8 已登记） |
| `ModelCost`/`ModelCostTier`/`ModelInputLimits`/`ModelImageInputLimits`/`ModelImageResizeOptions`/`ModelPromptCache` | `model-schema.ts:53-115` | 同名 | `Types/Model.cs:81-122` | ✅ | B |
| `Model.ThinkingLevelMap`（str\|null per level） | `types.ts:868` | `ThinkingLevelMap` | `Model.cs:56` `Dictionary<ModelThinkingLevel,string?>` | ✅ | B |
| `BaseModel.headers: Record<string,string>` | `types.ts:852` | `ModelCatalog.cs:100`（string） | `Model.cs:43` `string?`（允许 null 值） | 🟡 | D（值域放宽，未登记，Minor） |
| `StreamOptions`（ProviderRequestOptions+StreamOptions 全 20 字段） | `types.ts:160-270` | options 字典 | `Types/StreamOptions.cs:10-41` 逐项齐 | ✅ | B |
| **多出**：`StreamOptions.BaseUrl`、`StopSequences` | pi 无（分属 ModelAuth/API 专属选项） | 无 | `StreamOptions.cs:13,16` | ❌ | **D 未登记**（加字段无害，但 §8 必须补行；G-C1） |
| `OnPayload` payload 类型 `unknown` | `types.ts:181` | object? | `StreamOptions.cs:38` `JsonObject` | 🟡 | A（收窄，代价：无法透传非 JSON 载荷） |
| `SimpleStreamOptions` | `types.ts:377-385` | 字典 | `SimpleStreamOptions.cs:3-9` 四字段齐 | ✅ | B |
| `reasoning?: ThinkingLevel`（minimal..max，无 off） | `types.ts:380` | — | `SimpleStreamOptions.cs:6` `ThinkingLevel`（含 Off，且改名） | 🟡 | B/D 未登记（值域放宽 + 改名，Minor；G-C2） |
| `deferred?: boolean\|{window}` | `types.ts:382` | — | `StreamOptionTypes.cs:32-41` `DeferredRequest.Disabled/Enabled(Window)` | 🟡 | A（联合→record；Window 无 "15m/1h/24h" 校验） |
| `DeferredFetchOptions`/`DeferredCancelOptions` | `types.ts:274-283` | — | `StreamOptionTypes.cs:43-50` | ✅ | B |
| `ProviderResponse` | `types.ts:154-157`（headers: string） | — | `StreamOptionTypes.cs:52-54`（headers `string?`） | 🟡 | Minor |
| `ClientIdentity` | `utils/identity.ts` | 同名 | `StreamOptionTypes.cs:56-60` | ✅ | B |
| `TelemetrySpan` | 外部包 | `AiTelemetry` | `StreamOptionTypes.cs:62-66` 最小壳 | ✅ | C/D8 |
| `IProvider`（Id/Name/BaseUrl/Headers/auth/getModels/getAllModels/stream/streamSimple） | `models.ts:158-247`（**auth 必需**） | `Models.cs:31-100`（Auth 可空 DIM） | `IProvider.cs:9-29`（`Auth` 可空 `:15`） | ❌ | pi 规定 auth 必有（`models.ts:172`）；可空 + `Models.Auth.cs:34` 把 `Auth==null` 判为"已配置"=发明语义（G-B2） |
| `filterModels` + `filterAllModels` 两个可选成员 | `models.ts:203-213` | `Models.cs:60-67` 两个 DIM | `ProviderCapabilities.cs:13-15` 只有 `FilterModels`，且挂错接口（并进 IRefreshableProvider）、参数放宽为 ProviderModel | ❌ | **`FilterAllModels` 形状缺失**（G-B3，Important） |
| `refreshModels?` | `models.ts:196` | `Models.cs:57` | `IRefreshableProvider.RefreshModelsAsync` | 🟡 | B（挂到能力接口，行为 D2） |
| `StaticProvider`（≈createProvider 静态子集） | `models.ts:1077-1218`：api-map 按 model.api 分派、fetchModels、name 默认=id、auth 必需 | `CreateProvider.cs` | `StaticProvider.cs:14-30`：单 StreamFn、auth 可空、name 必填 | ❌ | api-map 分派缺失（G-B4，Important——S4 混合 API provider 需要它） |
| `Models` 门面 24 方法 | `models.ts:258-367` | `Models.cs`+`ModelsAuth.cs` | `Models.cs`+`Models.*.cs` 全齐（命名 Async 化=B） | ✅ | B |
| `CreateModelsOptions`（credentials/modelsStore/authContext/identity） | `models.ts:376-382` | `ModelsAuth.cs:7-17` 四成员 | `Models.cs:16-20` 只收 credentials+authContext | ❌ | **缺 modelsStore、identity 注入**（G-B1，Important） |
| `ModelsError` + `ModelsErrorCode`（oauth/auth/provider/stream/model_source） | `utils/models-error.ts`（导出） | `ModelsError` | **不存在**（`InvalidOperationException` 替代） | ❌ | **错误分类类型缺失**（G-B6，Important；下游无法按 code 分流） |
| `calculateCost`/`getSupportedThinkingLevels`/`clampThinkingLevel`/`modelsAreEqual`/`hasApi`/`isModelType` | `models.ts:1236-1300` 公共导出 | `ThinkingLevels` 等价物 | 全部缺失 | ❌ | 未登记省略（G-B7；可登记为 C/Dn，但必须登记） |
| `ModelsRequestTransforms.transformHeaders` | `models.ts:120-130` | `ModelsRequestOptions.cs:15-37` | 缺失 | ❌ | G-B5（Important；pi.net 都保留了） |
| `ModelsPublication.persist` 三态（undefined/null/entry） | `models.ts:76-81` | `ModelsRefresh.cs:18-21` `PersistDeleted` flag | `ModelsStore.cs:22-26` 只有 `Persist?`，**删除不可表达** | ❌ | G-A8（Minor~Important；pi.net 用 bool flag 已示范解法） |
| `CredentialStore`（read/list/modify/delete） | `auth/types.ts:65-94` | `AuthTypes.cs:92-109` 四方法全保留 | `Credential.cs:21-26` Get/Set/Delete 三方法：**缺 list、缺 modify（串行读改写），Set 为发明** | ❌ | **G-A1（Critical）**：OAuth 锁内刷新（S5）的正确性依赖 modify 语义 |
| `CredentialInfo(providerId,type)` | `auth/types.ts:40-43` | `AuthTypes.cs:85` 同构 | `Credential.cs:19` `(Type, Source)`——丢 providerId、发明 Source | ❌ | G-A2（Important） |
| `AuthContext`（env(name)/fileExists(path) 两个函数） | `auth/types.ts:97-101` | `AuthTypes.cs:112-118` `IAuthContext` | `AuthTypes.cs:36-39` 静态 Env 字典 record——**fileExists 能力消失** | ❌ | G-A3（Important；AWS profile/ADC 类 ambient 解析被排除） |
| `AuthResult { auth: ModelAuth; env?; source? }` | `auth/types.ts:104-110` | `AuthTypes.cs:121-130` 同构 | `AuthTypes.cs:22` `(Credential, Source)`——**auth/env 成员消失，Credential 为发明** | ❌ | **G-A4（Critical）**：Models 层"解析=拿 ModelAuth（key/headers/baseUrl）+env"的契约被改写；S4 applyAuth（headers/baseUrl/env 合并）将被迫返工 |
| `AuthCheck { source?, type }`（无 configured；未配置=undefined） | `auth/types.ts:112-115` | `ProviderAuth.cs:4-9` | `AuthTypes.cs:24` `(Configured, Type?)` + `Models.Auth.cs:34` `Auth==null→AuthCheck(true)` | ❌ | G-A5（Important；发明 Configured 位 + null-auth 视为已配置） |
| `AuthOperationOptions { signal? }` | `auth/types.ts:46-48` | 删除、改 ct 参数 | `AuthTypes.cs:26-28` **空 record**，且 `CheckAuthAsync/GetAvailable*/LogoutAsync/GetAuthAsync` 均无 ct 参数 | ❌ | G-A6（D12 已登记"取消未实现"行为，但**空壳 record** 既非 pi 形状也非 ct 惯用法，须二选一） |
| `ApiKeyAuth { name; login?; check?; resolve(input)→AuthResult? }` | `auth/types.ts:174-203` | `ProviderAuth.cs:44-63`（HasLogin/LoginAsync/ResolveAsync→AuthResult?） | `ProviderAuth.cs:13-17` 只有 Name + Resolve→`Task<string?>`；**缺 login、缺 check**；resolve 返回值从 AuthResult 降为 string | ❌ | G-A7（Important；check 是 pi 可用性判定的正式入口，我们用 resolve 顶替） |
| `OAuthAuth { name; isSubscription?; loginLabel?; login; refresh; toAuth }` | `auth/types.ts:225-249` | `ProviderAuth.cs:16-37`（ToAuthAsync 在） | `ProviderAuth.cs:19-28`：**缺 ToAuth**、**多出 Logout**（pi 登出=store.delete，无 provider 成员） | ❌ | G-A9（Important；toAuth 是 refresh/toAuth 拆分的一半） |
| `AuthResolutionOverrides { apiKey?; env?; minOAuthValidityMs?; signal? }` | `resolve.ts:19-25` | `ModelsAuth.cs:19-29` 同构 | `AuthTypes.cs:30-34` `{ApiKey, Credential}`——**缺 env/minOAuthValidityMs，发明 Credential** | ❌ | G-A10（Important；且我们 overrides.ApiKey 直接短路返回，不经过 provider resolve，pi 会走 `resolveApiKey`） |
| `OAuthCredential` 扩展字段（accountId/clientId/scopes/enterpriseUrl/…） | index signature | `AuthTypes.cs:47-81` 具名 7 字段 | `Credential.cs:12-17` 仅 3 字段 | ❌ | G-A11（Minor now / S5 前 Important；pi.net 的具名方案可直接借） |
| `ApiKeyCredential { key?; env? }` | `auth/types.ts:17-21` | `AuthTypes.cs:34-39`（Env 在） | `Credential.cs:10` 仅 Key | ❌ | G-A12（Minor；Cloudflare 类 provider-scoped env 装不下） |
| `AuthPrompt`/`AuthEvent`/`AuthInfoLink`/`LoginOptions`/`ProviderAuthInteraction` | `auth/types.ts:125-168` | `Interaction.cs` | `AuthInteraction.cs` 全齐；DeviceCode 含 openBrowser/copyCode（比 pi.net 更贴 pi） | ✅ | B（ProviderAuthInteraction 未实现 AuthInteraction 接口——Minor） |
| `ModelsStoreEntry { models; lastModified?; checkedAt?; etag? }` / `ModelsStore`（read/write/delete） | `models-store.ts:3-26` | 同构（IModelsStore） | `ModelsStore.cs:9-20`：entry=`{Provider, Data:JsonObject?, UpdatedAt?}`、store=Get/Set 两方法（**缺 delete、entry 丢 models/etag**） | ❌ | G-A13（Important；D2 落地时"按 provider 过滤+ETag 新鲜度"无处安放） |
| `LazyStream.Run` 第三参 | `lazy.ts:32-35` `LazyStreamObserver{onEvent,onResult}` | `Lazy.cs:12-19` `ILazyStreamObserver` | `LazyStream.cs:19` `TelemetrySpan?`（收下即弃，`pi.net` 是把 `AiTelemetry.ObserveRequest(...)` 作为 observer 传入） | ❌ | G-B5（Important；观察者面缺失，遥测后接时无挂点） |
| LazyStream 语义（同步返回/返回即 Task.Run 调度/单次启动/setup 异常含 OCE→Error/Aborted 仅内层） | `lazy.ts:60-86` | `Lazy.cs:46-77` | `LazyStream.cs:16-67` | ✅ | B（Task.Run 借 pi.net；v2 修正后与 pi 语义一致，测试 `LazyStreamTests` 覆盖） |
| `StreamFn`（同步、终态入流） | `types.ts:398-402` | 同 | `Types/StreamFn.cs:6-9` | ✅ | A（去泛型） |
| `normalizeContext` | `utils/transcript.ts:10-34` | — | `Types/TranscriptNormalization.cs:11-37`（空 prompt+空 tools 不插 system、Timestamp=0、顺序保持） | ✅ | B |
| transcript 重放辅助（getCurrentSystemMessage/getCurrentTools/collapseSystemMessages 等 12 个函数） | `utils/transcript.ts:37-263` | — | 不存在 | 🟡 | C（D11 已登记 S9，但形状锚点只指到文件级；S9 开工需补函数面） |
| Faux（队列/工厂/状态/确定性） | `providers/faux.ts` | `Providers/Faux.cs` | `Faux/FauxProvider.cs`+`Faux.cs` | ✅ | D（确定性已登记 §8+_mapping §2.2） |
| `FauxModelDefinition.inputLimits` | `faux.ts:47` | — | `Faux.cs:61-70` 缺 | ❌ | G-C3（Minor） |
| `FauxProviderState`（callCount/deferredFetchCount/cancelledDeferred） | `faux.ts:104-108` | — | `Faux.cs:83-95` 只有 CallCount+toolCallIndex | 🟡 | C（随 D3 补） |
| Faux auth（`auth:{apiKey:{name:"Faux",resolve:…}}` 恒可解析） | `faux.ts:713` | — | `FauxProvider.cs:45-50` Auth=null | 🟡 | D 未登记（连带 `Auth_Less_Provider_Is_Available` 把 null-auth 钉成"可用"；G-B2 一并裁决） |

---

## 2. 行为 / wire / 生态

**行为（已验证）**
- LazyStream：同步返回（`LazyStream.cs:29`）、返回即 `Task.Run`（`:28`，与 pi.net `Lazy.cs:51` 一致；pi TS 是同步起调至首个 await——B 级调度差异，v2 spec 已声明）、factory 异常含 OCE 一律 `Error(Error)`（`:51-66`）、无 Push/End 外借。与 spec §3.8 七条逐条相符。
- Models.Stream/StreamSimple：同步返回、认证解析在 LazyStream factory 内、失败终态 Error 不抛（`Models.Streaming.cs:18-28,44-54`）。与 spec §3.6 相符。
- `Complete*` = Stream + WaitForDone（`Models.Streaming.cs:31-36,57-62`）。✅
- Stub 语义：`StreamDeferred/FetchDeferredAsync/CancelDeferredAsync/GenerateImagesAsync/ClassifyAsync/LoginAsync` 同步抛 `NotSupportedException("Deferred to Sx: … (Dn)")`（`Models.Capabilities.cs`、`Models.Auth.cs:123-129`）。pi 的 streamDeferred/generateImages/classify **从不抛**（error 结果/终态事件）；这与台账"流式入口后置失败编码为终态 Error 不抛"的标注约定相抵，属登记过的 stub 机制选择，S4/S8 承接时须改编码。
- `RefreshAsync` 返回空结果不抛（`Models.Auth.cs:115-121`）——但 spec §3.6 说"并记录错误"，代码无任何记录动作（spec/代码不符，Minor）。
- `LogoutAsync` 额外调用 `provider.Auth.OAuth.Logout`（`Models.Auth.cs:138-141`）——pi 无此成员、登出=store.delete（G-A9 关联）。
- `GetAvailable*` 用单一 `FilterModels(ProviderModel)` 过滤所有类别（`Models.Auth.cs:56-62,82-85,104-107`）——pi 对 chat 用 filterModels、全类别走 filterAllModels??chat-id 推导（G-B3 行为面）。
- 认证解析链：options→store→provider resolve（`Models.Auth.cs:146-183`）；pi 链=overrides.apiKey→**经 provider resolve**→store(oauth 走锁内刷新)→ambient（`resolve.ts:46-93`）。我们 overrides.ApiKey/Credential 直接短路（不走 provider resolve），且 oauth 存量直接当结果（D1 TODO 已注）。

**wire（已验证）**
- `TransportKind`→`auto/sse/websocket/websocket-cached`、`ModelType`→`chat/image/classifier`、`CacheRetention`→`none/short/long`、`ToolChoice`→`auto/none`、`AuthType`→`api_key/oauth`：全部与 pi 字符串一致（`Fetch.cs:97-124`、`Model.cs:124-149`、`StreamOptionTypes.cs:68-118`、`Auth/AuthTypes.cs:49-72`），测试锚 `FetchPortTests/ModelShapeTests/StreamOptionsTests/AuthShapeTests` 的 Theory 覆盖。✅
- `DeferredRequest.Enabled.Window` 为自由 string，pi 是 `"15m"|"1h"|"24h"` 字面量联合——wire 校验缺失（A 联合映射的后续件，建议 S4/S8 补 validation）。

**生态（已验证）**
- `PiUnityAgent.Ai.csproj`：netstandard2.1、仅 System.Text.Json 8.0.5 + System.Threading.Channels 8.0.0；`grep UnityEngine|UnityLlmTransport` 于 src 零命中。✅（spec §10 验收项通过）
- 无 DIM（能力接口 + `is` 判断）；无 net9+ API（`Task.Run`/`WaitForDoneAsync`/lock 均为 2.1 面）。✅
- Fetch 端口"只吐字节"：`Types/Fetch.cs` 无 SSE/JSON 聚合逻辑。✅
- 适配器不在 S3：`UnityLlmTransportFetch`/`HttpClientFetch` 全仓无命中。✅

---

## 3. 未登记偏离 / Shape gap（按严重度）

> 判定口径（SOP §4）：以下各项 pi 有公共形状、我们改形或缺员，且不属于 A（语言/生态强制——pi.net 已示范可行 C# 表达）、B（照抄 pi.net 即可）、C（有 Dn 登记）、D（无收益/代价声明）。

### A 组 · Auth 类型面（spec §3.7 声称"按 pi auth/types.ts 补齐"，实际未对齐）

| # | 严重度 | 发现 | 证据 | 建议 |
|---|---|---|---|---|
| A1 | **Critical** | `CredentialStore` 缺 `list` 与 `modify`（串行读改写唯一写路径），另发明 `Set` | `Credential.cs:21-26` vs pi `auth/types.ts:65-94`、pi.net `AuthTypes.cs:92-109` | 照 pi.net 补 `ListAsync/ModifyAsync`（Set 可保留为 InMemory 便利方法但不进接口） |
| A2 | **Critical** | `AuthResult` 改形：pi `{auth: ModelAuth, env?, source?}` → 我们 `(Credential, Source)` | `Auth/AuthTypes.cs:22` vs pi `auth/types.ts:104-110` | 恢复 `{Auth: ModelAuth, Env?, Source?}`；`GetAuthAsync(model)` 同时补 model.headers 合并（pi `models.ts:764-773`） |
| A3 | Important | `CredentialInfo` 改形 `(Type, Source)`：丢 `providerId`、发明 `Source` | `Credential.cs:19` vs pi `auth/types.ts:40-43` | 改 `(ProviderId, Type)` |
| A4 | Important | `AuthContext` 由 env()/fileExists() 两函数改为静态 Env 字典 record：fileExists 能力消失 | `Auth/AuthTypes.cs:36-39` vs pi `auth/types.ts:97-101` | 改接口 `IAuthContext`（照 pi.net `AuthTypes.cs:112-118`） |
| A5 | Important | `ApiKeyAuth` 缺 `login?`、`check?`；`Resolve` 返回 `Task<string?>` 而非 `Task<AuthResult?>` | `Auth/ProviderAuth.cs:13-17` vs pi `auth/types.ts:174-203` | 补两成员；Resolve 返回 AuthResult?（A2 修复后自然对齐） |
| A6 | Important | `OAuthAuth` 缺 `ToAuth`、多出 `Logout` | `Auth/ProviderAuth.cs:19-28` vs pi `auth/types.ts:225-249` | 补 ToAuth；Logout 若保留须登记 D（代价：登出语义从"删凭据"扩为"provider 参与撤销"） |
| A7 | Important | `AuthCheck` 发明 `Configured` 位 + `Type?` 可空；且 `Auth==null→AuthCheck(true)` 把无认证 provider 判为已配置 | `Auth/AuthTypes.cs:24`、`Models.Auth.cs:34`、测试 `ModelsAuthTests.Auth_Less_Provider_Is_Available:142` | pi 语义：未配置=返回 undefined。要么回归 pi，要么把"Auth 可空视为已配置"显式登记 D |
| A8 | Important | `AuthResolutionOverrides` 发明 `Credential`、缺 `env`/`minOAuthValidityMs`/signal | `Auth/AuthTypes.cs:30-34` vs pi `resolve.ts:19-25` | 照 pi 补齐；overrides 路径改为仍经 provider resolve |
| A9 | Important | `ModelsStore`/`ModelsStoreEntry` 改形：entry 丢 `models/lastModified/checkedAt/etag`、store 缺 `delete` | `Auth/ModelsStore.cs:9-20` vs pi `models-store.ts:3-26` | D2（S4）开工前必须先复位形状，否则 D2 无处落地 |
| A10 | Minor | `AuthOperationOptions` 空壳（无 signal），各 auth 方法又无 ct 参数 | `Auth/AuthTypes.cs:26-28`、`Models.Auth.cs:28-47` | 与 D12 二选一：删空 record 改 ct 参数（pi.net 路线），或补 Signal 成员 |
| A11 | Minor | `ApiKeyCredential` 缺 `env`；`OAuthCredential` 缺 pi.net 具名扩展字段（accountId/clientId/scopes/enterpriseUrl/availableModelIds/gatewayConfig/scope） | `Credential.cs:10-17` vs pi.net `AuthTypes.cs:34-81` | S5 OAuth 开工前补（D1 形状锚点应补记此成员面） |
| A12 | Minor | `ModelsPublication.Persist` 双态，丢 `persist:null`=删除语义 | `ModelsStore.cs:22-26` vs pi.net `ModelsRefresh.cs:18-21` | 照 pi.net 加 `PersistDeleted` flag |
| A13 | Minor | `ProviderAuthInteraction` 未实现 `AuthInteraction` 接口；`OAuthAuth.Login` 委托收 `AuthInteraction` 而非规范化交互 | `AuthInteraction.cs:63-80` | 实现接口（pi.net `Interaction.cs:68-86` 写法可借） |

### B 组 · Models/Provider 门面

| # | 严重度 | 发现 | 证据 | 建议 |
|---|---|---|---|---|
| B1 | Important | `Models` 构造缺 `modelsStore`、`identity` 注入（pi `CreateModelsOptions` 四成员，pi.net 同） | `Models.cs:16-20` vs pi.net `ModelsAuth.cs:7-17` | 补构造选项（identity 是 pi 默认身份合并链的入口） |
| B2 | Important | `IProvider.Auth` 可空 + null 视为已配置/可用，pi 规定 auth 必有 | `IProvider.cs:15`、`Models.Auth.cs:34,196`、`ModelsTests` | auth 必需（Faux 补 apiKey resolve），或登记 D 并写明代价 |
| B3 | Important | `FilterAllModels` 形状缺失；`FilterModels` 参数从 chat `Model[]` 放宽为 `ProviderModel` 并挂进 `IRefreshableProvider` | `ProviderCapabilities.cs:10-16` vs pi `models.ts:203-213` | 照 pi/pi.net 补两成员（可放独立 `IFilterableProvider`） |
| B4 | Important | `StaticProvider` 缺 createProvider 的 api-map 分派（`api?: Partial<Record<TApi,ProviderStreams>>`）/fetchModels/name 默认=id | `StaticProvider.cs:14-30` vs pi `models.ts:1032-1090` | S4 多 API provider 前补"按 model.api 分派"构造或补 `CreateProvider` 等价物 |
| B5 | Important | `ModelsRequestTransforms.transformHeaders` 缺失；`LazyStream` 观察者面缺失（第三参 `TelemetrySpan?` 收下即弃，pi/pi.net 是 LazyStreamObserver） | `StreamOptions.cs`（无该字段）、`LazyStream.cs:19` vs pi `models.ts:120-130`、pi.net `Lazy.cs:12-19` | 补 `ILazyStreamObserver`（onEvent/onResult）作 Run 第三参；transformHeaders 登记 Dn 或补形状 |
| B6 | Important | `ModelsError`+`ModelsErrorCode`（oauth/auth/provider/stream/model_source）缺失，用 `InvalidOperationException` 顶替 | 全仓无命中 vs pi `models.ts:72`、pi.net `ModelsError` | 补类型并在 RequireProvider/ResolveAuth 等处使用（下游按 code 分流依赖它） |
| B7 | Minor | pi 公共导出 `calculateCost`/`getSupportedThinkingLevels`/`clampThinkingLevel`/`modelsAreEqual`/`hasApi`/`isModelType` 未落，也未登记 | pi `models.ts:1236-1300` | 登记 Dn（建议 S4：cost/thinking 与 provider 实现同期）或补形状 |

### C 组 · Options/Faux 杂项

| # | 严重度 | 发现 | 证据 | 建议 |
|---|---|---|---|---|
| C1 | Minor | `StreamOptions.BaseUrl`/`StopSequences` 为 pi 所无的加字段（spec §3.2 有、§8 无） | `StreamOptions.cs:13,16` | §8 补 D 行（收益：省一层 API 专属子类；代价：与 pi 选项面漂移） |
| C2 | Minor | `SimpleStreamOptions.reasoning` 改名 `ThinkingLevel` 且值域放宽（含 Off） | `SimpleStreamOptions.cs:6` vs pi `types.ts:380` | §8 补行；或回归 `Reasoning` 名与 ThinkingLevel（minimal..max）值域 |
| C3 | Minor | `FauxModelDefinition` 缺 `inputLimits`；Faux 缺 `onResponse` 回调触发 | `Faux.cs:61-70`、`FauxProvider.cs:103-150` vs pi `faux.ts:47,534` | 补 inputLimits；onResponse 随 S4 选项映射 |
| C4 | Minor | `ProviderModel.Headers`/`ProviderResponse.Headers`/`ModelAuth.Headers` 值域 `string?`（pi 为 string，null 抑制语义仅属请求头） | `Model.cs:43`、`StreamOptionTypes.cs:52-54` | 登记或收窄 |
| C5 | Minor | `DeferredRequest.Enabled.Window` 无 "15m/1h/24h" 校验 | `StreamOptionTypes.cs:38` | S8 前补 validation |

**无"普通 code review 范畴"发现**（按 SOP §3 不审）。

---

## 4. C 类 Dn 检查（台账 `docs/待办-后置能力.md`）

| Dn | 形状锚点 | 验收测试锚点 | 目标阶段 | 状态 | 审计判定 |
|---|---|---|---|---|---|
| D1 OAuth | `OAuthAuth/AuthInteraction/CredentialStore`（**但缺 ToAuth/list/modify**，§3-A1/A6/A11） | `AuthShapeTests.AuthPrompt_And_AuthEvent_Variants_Are_Present` ✅、`ModelsAuthTests.Login_Is_Deferred_To_S5:177` ✅ | S5 | 未开工 | ⚠️ C 成立但形状锚点失真，须随 A 组修复改写 |
| D2 目录刷新 | `IRefreshableProvider/RefreshAsync/RefreshModelsContext`（**ModelsStoreEntry 改形**，A9） | `ModelsAuthTests.Refresh_Returns_Empty_Result_Without_Throwing:167` ✅ | S4 | 未开工 | ⚠️ 同上 |
| D3 Deferred | `IDeferredProvider`+`Models` 三方法 ✅ | `ModelsCapabilityTests.Deferred_Methods_Are_Deferred_To_S8:11` ✅ | S8 | 未开工 | ✅（Faux 状态字段随补） |
| D4 图片 | `IImagesProvider`+`GenerateImagesAsync` ✅ | `ModelsCapabilityTests:40` ✅ | 2B | 未开工 | ✅ |
| D5 分类 | `IClassifierProvider`+`ClassifyAsync` ✅ | 同上 ✅ | 2B | 未开工 | ✅ |
| D6 Prompt cache | `Model.PromptCache`/`StreamOptions.CacheRetention` ✅ | ModelShape/StreamOptions Tests ✅ | S4/S5 | 未开工 | ✅ |
| D7 持久化 store | `CredentialStore/InMemoryCredentialStore` ✅（A1 修复后锚点随之更新） | `AuthShapeTests.InMemoryCredentialStore_RoundTrips_And_Deletes` ✅ | S5 | 未开工 | ⚠️ |
| D8 Telemetry | `TelemetrySpan`/`LazyStream.Run(...)` ✅（观察者面缺，B5） | StreamOptionsTests ✅ | 后续 | 未开工 | ⚠️ |
| D9 目录数据 | 无代码锚点（纯数据，表内自述） | ModelShapeTests.ProviderModel_Defaults_Are_Stable ✅ | S4 | 未开工 | ✅ |
| D10 可用性过滤 | `GetAvailable*`/`FilterModels` ✅（FilterAllModels 缺，B3） | `ModelsAuthTests.CheckAuth_And_GetAvailable_Filter_Configured_Providers:122` ✅ | S4/S5 | 进行中 | ⚠️ |
| D11 重放辅助 | `TranscriptNormalization`（文件级；函数面未落） | `TranscriptNormalizationTests.Original_Message_Order_Is_Preserved` ✅ | S9 | 未开工 | ✅（S9 开工时把函数面补进锚点） |
| D12 Auth 取消/单次解析 | `ResolveApiKeyAsync/ResolveAuthAsync/GetAvailable*` ✅ | S4 新增（表内自述） | S4 | 未开工 | ✅（与 A10 联动） |
| D13 Fetch 超时/无默认 fetch 测试 | `FetchOptions.*`/`MockFetch`/`Models.Streaming` ✅ | S4 新增（表内自述） | S4 | 未开工 | ✅ |

代码锚点核对：`TODO(S4,D2)`/`TODO(S5,D1)`/`NotSupportedException("Deferred to Sx: … (Dn)")` 共 8 处，全部能在台账找到 ID，无无主 stub。✅

---

## 5. 必需动作（优先级序）

1. [ ] **修 A2（AuthResult）/A1（CredentialStore modify+list）**——Critical；S4/S5 的 applyAuth 与锁内刷新都压在这两个形状上，先修可避免下游返工。
2. [ ] **修 A4/A5/A6/A8/A9/A11**（AuthContext 接口化、ApiKeyAuth.Check/Login、OAuthAuth.ToAuth、AuthResolutionOverrides、ModelsStore 形状、凭据扩展字段）——或逐项由用户明确批准登记为 D 并写入 spec §8。
3. [ ] **修 B3（FilterAllModels）/B5（LazyStreamObserver+transformHeaders）/B6（ModelsError）/B1（CreateModelsOptions 四成员）/B4（api-map 分派）**，或登记 D/Dn。
4. [ ] **裁决 B2/A7**：`Auth` 可空 + null=已配置 是保留（登记 D + 改 Faux 补 auth）还是回归 pi（auth 必需）；现行测试 `Auth_Less_Provider_Is_Available` 钉住了发明语义，须随裁决改。
5. [ ] **spec §8 补行**：C1/C2/C4 + 上述选择保留为 D 的各项（pi/pi.net/我们/类别/原因/代价）；A 组若修复则同步改 §3.7 文字。
6. [ ] **台账更新**：D1/D2/D7/D10 的形状锚点改指修复后的真实成员；D11 锚点在 S9 前细化为函数清单；B7 若走 Dn 需新行。
7. [ ] 修复后由**新的独立审计者**复核（SOP §5.1 第 6 步）。

## 6. 审计者附注

- **无法验证项**：① UnityLlmTransport `87db637` commit 未独立核验（本阶段无适配器代码，风险低）；② 未运行任何构建/测试（只读审计），"测试全绿"引用实现方门禁记录；③ `ModelThinkingLevel`/`ThinkingLevel` 的 wire converter 属 S2 遗产，本轮未逐一核对（S3 未触碰）。
- **误报排除**：pi `Models.refresh` vs 我们 `RefreshAsync`、`LoginAsync` 命名 Async 化、`Provider` 单接口 vs `MutableModels` 拆分、枚举化 wire 联合、`Task.Run` 调度、`IAsyncEnumerable` 迭代代替 `result()`——均已属 A/B 合规，不计缺口。
- spec §3.6 "RefreshAsync…记录错误" 与代码（无记录动作）不符——Minor，随 D2 实现自然消解。
- 审计中未发现 wire 字符串错误、未发现 UnityEngine/UnityLlmTransport 引用、未发现无登记的裸 TODO；Fetch 端口形状与 docs/07 §二逐字段一致。
