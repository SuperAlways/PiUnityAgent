# S3 v3 偏离审查（2026-11-08）

> 审计者：独立审计 subagent（GLM，非实现会话；只读，未改任何代码/index/HEAD/分支）
> 参考版本（已实核）：pi `v1.5.0` / `f9a263655`（`git log` 实核 HEAD=tag v1.5.0, Release v1.5.0）；pi.net `main` / `2d6757b`（实核）；UnityLlmTransport `87db637`（**未独立核验 commit**；本分支零通讯层代码，不影响结论）
> 范围：`269f48a..a305315`（10 commits：290b263 → a305315；diff 包 `.superpowers/sdd/s3v3-audit-269f48a..a305315.diff` 已通读；file:line 按 HEAD=a305315 工作区实读）
> 结论：**PASS_WITH_REGISTERED_DEVIATIONS**
>
> 判定依据：S3 v2 审计（`docs/audits/2026-11-08-S3-v2-deviation-audit.md`）A 组 13 项 / B 组 7 项 / C 组 5 项共 25 项 Shape gap **全部核销**（§1 逐项给证据）；本轮新发现 8 项偏离**全部为 Minor**，其中 7 项未登记（需补 spec §6 行 / Dn 行后方可合并），1 项已登记但**类别标注失真**（B → 实为双方参考源都用 `ModelsError`）。无 Critical/Important 级 Shape gap。

---

## 0. spec §6 三方偏离表核对结果（逐行 → 代码）

| §6 行 | 代码实况 | 判定 |
|---|---|---|
| Auth 类型（IAuthContext 接口、delegate 化，B/A） | `Providers/Auth/Credential.cs:10-36`、`AuthTypes.cs:12-45`、`ProviderAuth.cs:7-32`、`AuthContext.cs:8-32`、`AuthInteraction.cs:10-84`——与 pi `auth/types.ts` 逐成员对齐（见 §1） | ✅ |
| `OAuthCredential` 三字段 required（B） | `Credential.cs:19-21` `required RefreshToken/AccessToken/ExpiresAt`；pi `types.ts:31-34` refresh/access/expires 必填 | ✅ |
| `AuthCheck` Type 必填（B） | `AuthTypes.cs:24` `AuthCheck(AuthType Type, string? Source = null)`；pi `types.ts:118-121` `{ source?; type }`（字段同、C# 位置参序颠倒属 B） | ✅ |
| `CredentialStore.modify/list`（B） | `Credential.cs:27-36` Read/List/Modify/Delete 四方法；Modify null=不变返回写后值（`:95-103`）= pi `types.ts:81-86`+`credential-store.ts:50-56`（`return next ?? current`） | ✅ |
| per-provider `SemaphoreSlim` 串行（A/B） | `Credential.cs:42-56`（LockFor）+ `:87-95`（await 回调不在 lock 内）＝pi `credential-store.ts:14-32` promise 链的 C# 表达 | ✅ |
| `IAuthContext`（B） | `AuthContext.cs:8-12` EnvAsync/FileExistsAsync；pi `types.ts:97-101` | ✅ |
| `AuthResult` ModelAuth+Env+Source（B） | `AuthTypes.cs:19-22`；pi `types.ts:104-110` | ✅ |
| Auth 解析顺序 stored owns（B） | `Models.Auth.cs:178-229`：override ApiKey→stored OAuth→stored ApiKey→stored 无匹配 return null（`:224`）→ambient。与 pi `resolve.ts:51-93` 分支一一对应 | ✅ |
| `AuthOperationOptions` options 内 CT（A） | `AuthTypes.cs:26-29`；pi `types.ts:46-48` `signal?` | ✅ |
| `AuthResolutionOverrides` 四成员（B/A） | `AuthTypes.cs:31-37` ApiKey/Env/MinOAuthValidityMs/Signal；pi `resolve.ts:19-25` | ✅ |
| `IProvider.Auth` 必需（—） | `IProvider.cs:15` `ProviderAuth Auth { get; }`；pi `models.ts:172` auth 必需 | ✅ |
| `IFilterableProvider` 独立接口 + nullable FilterAllModels（A/Unity） | `ProviderCapabilities.cs:16-23`；pi `models.ts:203-213` 两个可选成员 | ✅ |
| `CreateProviderOptions`/`ApiByApi`（B） | `CreateProvider.cs:23-24` 两字段 + `ProviderFactory.cs:19-21` 互斥校验（pi 是单字段联合 `api?: ProviderStreams \| Partial<Record<...>>`，`models.ts:1063`） | ✅ |
| `Models` required（—，预审计修正） | `CreateProvider.cs:17` `required IReadOnlyList<ProviderModel> Models`（a305315 改）；pi `models.ts:1057` `models` 必填 | ✅ |
| Images/Classifiers → 能力接口（B） | `ProviderFactory.cs:36` `CreatedProvider : IProvider, IFilterableProvider, IImagesProvider, IClassifierProvider` + `:90-100` 按 `model.Api` 分派 | ✅ |
| `StreamSimple` 独立 `SimpleStreamFn`（B） | `ProviderStreams.cs:12-13`、`Types/StreamFn.cs:12-15`；pi `types.ts:319-332` stream/streamSimple 分立 | ✅ |
| `ProviderImages`/`ProviderClassifier` 独立 options（B） | `ProviderStreams.cs:19-28`（`ImagesOptions?`/`ClassifierOptions?`）；pi `types.ts:340-364` | ✅ |
| `ImagesOptions`/`ClassifierOptions : StreamOptions`（B/D） | `Types/ImagesOptions.cs:7`、`ClassifierOptions.cs:7`；代价（多出 stream-only 字段）已写在类型注释 | ✅ |
| `ModelsError`（B；RequireProvider 例外见 §4.1） | `ModelsError.cs:5-32`，6 code 与 pi `utils/models-error.ts:3`（model_source/model_validation/provider/stream/auth/oauth）一一对应；`WithCauseDetail` 逐字对齐 pi `:15-20` | ⚠️ 见 N1（B 标注失真） |
| `CreateModelsOptions` 四成员（B） | `Models.cs:12-18` Credentials/ModelsStore/AuthContext/Identity；pi `models.ts:376-382` | ✅ |
| `ClientIdentity` pi 形状 + Merger（B/A） | `Types/ClientIdentity.cs:6-61`；pi `utils/identity.ts:28-46`（`attribution !== undefined` ↔ `AttributionSet` 哨兵，`:55-56`） | ✅ |
| `LazyStreamObserver` 按 pi 顺序（B） | `Events/LazyStream.cs:47-53` Push→OnEvent、End→OnResult ✅；**setup 失败路径 `:68-70` 缺 `OnEvent(error)`**（pi `api/lazy.ts:79-90` catch 块先 `outer.push` → `observer?.onEvent?.(event)` → `outer.end` → `observeResult`） | ⚠️ 见 N2 |
| `TransformHeaders` base 字段（B/D） | `StreamOptions.cs:37` + Models 摘除 `Models.Streaming.cs:29,55`（`with { TransformHeaders = null }`）；pi `models.ts:120-130` intersection | ✅ |
| `BaseUrl`/`StopSequences` 删除（—） | `StreamOptions.cs` 无此二字段（grep 零命中） | ✅ |
| `Reasoning`（B/A） | `SimpleStreamOptions.cs:6` `ThinkingLevel?`；`ThinkingLevel.cs:8` 枚举 Minimal..Max 无 Off＝pi `model-schema.ts` ThinkingLevel 值域 | ✅ |
| `OnPayload` JsonNode?（A） | `StreamOptions.cs:40`；pi `types.ts:181` unknown | ✅ |
| `ModelsStore`（B） | `Auth/ModelsStore.cs:9-27` entry 四字段+store 三方法＝pi `models-store.ts:3-26`；`ModelsPublication.Delete`（`:62-67`）承载 pi `persist:null`（`models.ts:76-81`） | ✅ |
| `StaticProvider`（D） | `StaticProvider.cs:9-55`，唯一额外公共类型，auth 非空校验 `:27-29` | ✅ |
| Faux 确定性（D） | `Faux/Faux.cs:84-109` 计数器；`FauxProvider.cs:54-59` keyless auth | ✅ |
| OAuth/刷新/Deferred/图片/分类 C/Dn | `Models.Auth.cs:152-157`（Login D1）、`:146-150`（Refresh D2）、`Models.Capabilities.cs:10-43`（D3/D4/D5）；`check-deferred --gate all` OK（D1=1, D2=1, D3=3, D4=2, D5=2, D8=1） | ✅ |
| Utilities → `ModelOperations`（B） | `ModelOperations.cs:8-99` 七函数；pi `models.ts:1196-1300` + `utils/model-operations.ts` | ✅ |
| Fetch 端口（B/D） | `Types/Fetch.cs` 本范围零改动；`grep UnityEngine\|UnityLlmTransport\|GameData\|Harmony\|HttpClient` 于 src 零命中 | ✅ |

**总评**：§6 表 32 行全部可对应代码；2 行带瑕疵（N1 类别失真、N2 顺序未完全按 pi），表外另有 7 项未登记偏离（§3）。

---

## 1. 公共形状矩阵（v2 遗留项核销 + 全量对比）

图例：✅=形状与 pi 语义等价；🟡=A/B/D 偏离（已登记或语言强制）；❌=Shape gap。

### 1.1 v2 审计 A 组（Auth）核销

| v2 项 | v2 证据 | v3 现状 | 判定 |
|---|---|---|---|
| A1 CredentialStore 缺 list/modify | `Credential.cs:27-36` 四方法全落，modify 唯一写路径、null=不变、返回写后值 | ✅ 核销 |
| A2 AuthResult 改形 `(Credential, Source)` | `AuthTypes.cs:19-22` `{Auth, Env, Source}`；`GetAuthAsync(model)` 合并 model.Headers（`Models.Auth.cs:23-42`，方向=模型头胜过 auth 头，同 pi `models.ts:703-725`） | ✅ 核销 |
| A3 CredentialInfo 丢 providerId | `Credential.cs:25` `(string ProviderId, AuthType Type)` | ✅ 核销 |
| A4 AuthContext 无 fileExists | `AuthContext.cs:8-32` 接口 + Default（`~` 展开、File.Exists） | ✅ 核销 |
| A5 ApiKeyAuth 缺 login/check、Resolve 降型 | `ProviderAuth.cs:16-22` Name/Login?/Check?/Resolve→`Task<AuthResult?>` | ✅ 核销 |
| A6 OAuthAuth 缺 ToAuth、多 Logout | `ProviderAuth.cs:24-32` Name/IsSubscription/LoginLabel/Login/Refresh/ToAuth，无 Logout；`Models.Auth.cs:159-176` 登出只删 store | ✅ 核销 |
| A7 AuthCheck 发明 Configured + null-auth 视为已配置 | `AuthTypes.cs:24` 无 Configured；`IProvider.Auth` 非空（`IProvider.cs:15`）；Faux keyless（`FauxProvider.cs:54-59`）；测试 `ModelsAuthTests.Keyless_Auth_Provider_Is_Available:232` | ✅ 核销 |
| A8 AuthResolutionOverrides 缺 env/minOAuthValidityMs、Credential 发明 | `AuthTypes.cs:31-37` 四成员，无 Credential；override ApiKey 走 provider Resolve 并带 Env（`Models.Auth.cs:188-192`，合成凭据 `new ApiKeyCredential(overrides.ApiKey){Env=overrides.Env}`）＝pi `resolve.ts:53-62` | ✅ 核销 |
| A9 ModelsStore 改形 | `ModelsStore.cs:9-27` entry={Models,LastModified,CheckedAt,Etag}+read/write/delete | ✅ 核销 |
| A10 AuthOperationOptions 空壳 | `AuthTypes.cs:26-29` CT 成员；`CheckAuthAsync/LogoutAsync/GetAvailable*` 全链传 CT（`Models.Auth.cs:44-49,165,84-85`） | ✅ 核销 |
| A11 凭据扩展字段缺失 | `ApiKeyCredential.Env`（`Credential.cs:14`）；`OAuthCredential.Extra`（`:22`，index-signature 的 JsonNode 映射）；pi.net 具名 7 字段未采——Extra 字典可承载，形状不缺失 | ✅ 核销（B/A） |
| A12 ModelsPublication 双态 | `ModelsStore.cs:62-67` Persist?/Delete/Update；pi `persist: null`=删除 ↔ `Delete` flag（pi.net `ModelsRefresh` bool flag 同型） | ✅ 核销（A） |
| A13 ProviderAuthInteraction 未实现接口 | `AuthInteraction.cs:63-84` `: AuthInteraction` + linked CT | ✅ 核销 |

### 1.2 v2 审计 B 组（Models/Provider 门面）核销

| v2 项 | v3 现状 | 判定 |
|---|---|---|
| B1 CreateModelsOptions 缺 modelsStore/identity | `Models.cs:12-35` 四成员 + ctor 注入 + `Identity` 属性 | ✅ 核销 |
| B2 IProvider.Auth 可空 + null=已配置 | 非空；`Models.Auth.cs:52-53` 的 `auth is null` 残留为死分支（无害，Auth 类型非空） | ✅ 核销 |
| B3 FilterAllModels 缺失、FilterModels 挂错接口 | `ProviderCapabilities.cs:16-23` `IFilterableProvider{FilterModels, FilterAllModels?}`；`Models.Auth.cs:132-143` FilterAllModels 优先、null→保序 chat-id fallback＝pi `models.ts:627-639`；测试 `ModelsTests.cs:225`（保序）+`:260`（非 null 胜出） | ✅ 核销 |
| B4 缺 api-map 分派/createProvider | `ProviderFactory.cs:13-101`：单 Api/ApiByApi 分派（`:82`）、缺映射→`LazyStream` 内 `ModelsError(Stream, "Provider X has no API implementation for \"Y\"")`（`:84-87`，消息逐字同 pi `models.ts:1104-1107`）、空实现/双形式拒绝（`:25-31`）、name 默认=id（`:53`） | ✅ 核销 |
| B5 transformHeaders 缺失 + LazyStream 观察者面缺失 | `StreamOptions.TransformHeaders`（`StreamOptions.cs:37`）+ Models 执行并摘除（`Models.Streaming.cs:124-146`）；`ILazyStreamObserver`（`LazyStreamObserver.cs:9-13`）作 `Run` 第三参（`LazyStream.cs:20`）；TelemetrySpan 保留（`StreamOptionTypes.cs:56-60`） | ✅ 核销 |
| B6 ModelsError 缺失 | `ModelsError.cs` 全落；用于 Logout/Resolve/Check/Read 包裹（`Models.Auth.cs:162,173,210,238,254,276,294`）与 factory 校验（`ProviderFactory.cs:17,20,30`） | ✅ 核销 |
| B7 utilities 未落 | `ModelOperations.cs` 七函数；pi `hasApi`（`models.ts:1196-1205`，非 chat 永 false＝`:25-26`）、`calculateCost`（tier 规则+1h cache×2＝`:32-61`）、thinking levels/clamp（`:63-99`）、modelsAreEqual（`:28-30`） | ✅ 核销 |

### 1.3 v2 审计 C 组（Options/Faux）核销

| v2 项 | v3 现状 | 判定 |
|---|---|---|
| C1 StreamOptions.BaseUrl/StopSequences 多出 | 已删 | ✅ 核销 |
| C2 SimpleStreamOptions.ThinkingLevel 改名+值域放宽 | `Reasoning : ThinkingLevel?`，值域 Minimal..Max（无 Off） | ✅ 核销 |
| C3 Faux 缺 inputLimits/onResponse | `Faux.cs:67` `InputLimits`；onResponse 随 S4 选项映射（spec §5.2 未承诺） | ✅ 核销（onResponse 仍缺，属 S4 provider 行为面，v3 spec 未列入目标） |
| C4 Headers 值域 string? | `Model.cs:43` `IReadOnlyDictionary<string,string>?`、`StreamOptionTypes.cs:52-54` ProviderResponse Headers 非 null 值＝pi `types.ts:154-157,852` | ✅ 核销 |
| C5 Deferred Window 无校验 | 仍为自由 string（`StreamOptionTypes.cs:38`）；spec §5.1 记 S8 前补（D3） | ✅ C 登记 |

### 1.4 全量形状对比（本轮新核对，v2 未覆盖或需复核项）

| 区域 | pi 原版 | pi.net | 我们 | 判定 | 类别/备注 |
|---|---|---|---|---|---|
| `IProvider.getAllModels?` 可选 | `models.ts:190-197`（可省，Models 回落 getModels） | DIM 可空 | `IProvider.cs:18` **必需** | 🟡 | A/Unity（无 DIM）；成员存在、语义被吸收，但 pi 的"可省略"约束未登记 → **N5** |
| `Provider.fetchDeferred?/cancelDeferred?` | `models.ts:218-228` 可选成员 | DIM | `ProviderStreams.cs:14-15` 可选委托 + `IDeferredProvider`（`ProviderCapabilities.cs:26-42`） | ✅ | A（no-DIM 双表达） |
| `refreshModels?` | `models.ts:196` | DIM | `IRefreshableProvider`（`ProviderCapabilities.cs:10-13`） | ✅ | A；Factory 产物在给 `FetchModels` 时**不**实现 IRefreshableProvider（pi `createProvider:1117-1143` 会装配 refreshModels）——行为 D2，见 N6 |
| `Models` 门面 27 方法 | `models.ts:258-367` | 同 | `Models.cs/Models.Auth.cs/Models.Streaming.cs/Models.Capabilities.cs` 全齐（Async 化=B） | ✅ | B |
| `ModelsPublication` | `models.ts:76-81` 三态 | bool flag | `ModelsStore.cs:62-67` | ✅ | A；spec §3.6 称"运行期验证二选一"——**验证代码尚不存在**（D2 发布路径才消费）→ N7（Minor） |
| InMemoryModelsStore 深拷贝 | `models-store.ts:33-40` structuredClone | — | `ModelsStore.cs:34-49` 引用存取 | 🟡 | C（spec §3.6 明记待 D2） |
| `AuthPrompt`/`AuthEvent` 变体 | `types.ts:125-168` | 同 | `AuthInteraction.cs:10-55` 四 prompt + 四 event，DeviceCode 五可选字段齐 | ✅ | B |
| `LoginOptions` | `types.ts:251-263` | 同 | `AuthTypes.cs:41-45` | ✅ | B |
| pi 公共导出 `envApiKeyAuth`/`lazyOAuth` | `auth/helpers.ts:9-63`（index.ts:20 导出） | `pi.net Auth/Helpers.cs:12,64` 已移植 | **缺失** | ❌→🟡 | **N4（未登记，Minor）**：成员面缺失，S4 provider 开工需要 envApiKeyAuth |
| pi 公共导出 `withClientIdentity` | `utils/identity.ts:148-195` | — | 缺失（功能被 `CreateModelsOptions.Identity`+Merger 覆盖大半） | 🟡 | **N4 同批登记**（Minor） |
| `getModelType/isModelType` | `model-operations.ts:14-21`（type 缺省=chat） | 同 | `ModelOperations.cs:21-23`（Type 非空，三 record 表达，B） | ✅ | B |
| `ModelsErrorCode` 六值 | `models-error.ts:3` | 同 | `ModelsError.cs:5-13` | ✅ | B |

---

## 2. 行为 / wire / 生态

**行为（已验证）**
- **Auth 解析顺序**＝pi `resolve.ts:51-93` 逐分支对应：override（`Models.Auth.cs:188-192`）→stored OAuth（`:196-213`，ToAuth 失败包 `ModelsError(OAuth)`＝pi `resolve.ts:217-221`）→stored ApiKey（env 叠加方向=override 胜，`:218-220`＝pi `resolve.ts:66-68`）→stored 无匹配 return null（`:224`＝pi `:71-72`）→ambient（`:228`）。stored OAuth 不被 ambient key 顶掉：测试 `ModelsAuthTests.GetAuth_Stored_OAuth_Wins_Over_Ambient_ApiKey:40`。✅
- **CredentialStore.ModifyAsync**：null=不变返回 current（测试 `AuthShapeTests.Modify_With_Null_Leaves_Entry_And_Returns_Current:38`）、per-provider 串行（`:51`）、await 不持 lock（`Credential.cs:95` 在信号量内、lock 仅护字典读写）。✅
- **CheckAuth**：stored OAuth→`AuthCheck(OAuth,"OAuth")`（`Models.Auth.cs:56-61`＝pi `models.ts:666-668`）；`ApiKeyAuth.Check` 存在即权威（`:65-68`）；缺失回退 Resolve（`:70-72`＝pi `models.ts:672-676`）。✅
- **GetAvailable\***：每 provider 单次 `CheckAuthAsync`（`:82,126`）；fallback 保序（测试 `ModelsTests.GetAvailableAll_Fallback_Preserves_Order_And_Filters_Chat_Only:225`，断言 `["img1","chatB","cls1"]` 原序）。✅
- **LazyStream**：Push→OnEvent、End→OnResult（`LazyStream.cs:47-53`）；OnResult 恰一次（`:190` 测试）；setup 失败 `OnResult(errorMessage)` 非 null（`:173` 测试）——但失败路径缺 OnEvent（N2）。Task.Run 调度=B 已登记。
- **Models.Stream/StreamSimple**：同步返回、认证在 factory 内、未配置→`ModelsError(Auth)`→LazyStream 终态 Error（测试 `ModelsAuthTests.Missing_Auth_Becomes_Error_On_Stream:19`）；baseUrl 覆盖（`:28,54`＝pi `models.ts:842`）；TransformHeaders 进 provider 前执行并摘除（`:29,55`＝pi `models.ts:860` 从 requestOptions 剥除）；Identity 逐字段合并（`:26,52`＝pi `models.ts:862`）。✅
- **ProviderFactory**：空/全 null ApiByApi 无其他实现→`ModelsError(Provider)`（a305315，测试 `ProviderFactoryTests.Factory_Rejects_Empty_ApiByApi_Without_Other_Implementation:72`）；Api+ApiByApi 同给拒绝（`:121`）；auth 至少一法拒绝（`:121`）；缺映射→Stream 终态 `ModelsError(Stream)`（`:41`）＝pi `models.ts:1104-1107`。✅
- **Logout**：只删 store、失败包 `ModelsError(Auth)`（`Models.Auth.cs:159-176`＝pi `models.ts:1014-1022`）；**多出**未知 provider→`ModelsError(Provider)` 检查（`:161-162`，pi 无）→ N8。
- **pi 有而我们无（行为面）**：① applyAuth 把请求级 `env` 传入 getAuth 参与解析（pi `models.ts:838`；我们 `Models.Streaming.cs:73-77` 只传 ApiKey/Signal）→ N3；② session/identity 头注入（pi `models.ts:851-855`、`identity.ts:119-145`）→ N4 相关；③ 未配置错误消息字面量（pi `"Provider is not configured: X"`，`models.ts:845`；我们 `"Authentication not configured for provider: X"`，`Models.Streaming.cs:79-80`）——终态 errorMessage 文案不同，Minor，随 N3 一并订正或登记。

**wire（已验证）**
- `AuthType`→`api_key/oauth`（`AuthTypes.cs:47-64`）、`ModelType`→`chat/image/classifier`（`Model.cs:124-149`）、`CacheRetention`/`ToolChoice`（`StreamOptionTypes.cs:62-112`）、`TransportKind`（Fetch.cs，未触碰）：与 pi 字符串一致。✅
- `ModelsError` 消息：`WithCauseDetail` 与 pi `models-error.ts:15-20` 逐字同构。✅
- 缺 api 映射的错误消息模板与 pi 逐字相同（§1.2 B4）。✅

**生态（已验证）**
- `netstandard2.1`；System.Text.Json + Channels；`grep UnityEngine|UnityLlmTransport|GameData|Harmony|HttpClient`（src/PiUnityAgent.Ai，*.cs）零命中；无 DIM（能力接口 + `is` 判断）；无 net9+ API。✅（与控制器门禁记录一致）

---

## 3. 未登记偏离 / Shape gap（按严重度；全部 Minor）

> 判定口径（SOP §4）：以下各项在 pi（必要时 pi.net）有公共形状/语义，我们缺失或改了表达，且 spec §6 与 Dn 台账均无登记。无一项达到 Critical/Important；合并前需完成登记（或一行修复），不需重新审计（SOP §5.1 第 6 步仅约束 Shape gap/Critical 修复）。

| # | 严重度 | 发现 | 证据 | 为什么不是 A/B/C/D（现状） | 建议 |
|---|---|---|---|---|---|
| **N1** | Minor（登记失真） | `Models.Streaming.RequireProvider` 抛 `InvalidOperationException`；spec §4.1/§6 称"登记 B（pi.net 同）"，**实核 pi.net 也抛 `ModelsError(Provider)`**（`pi.net src/Pi.Ai/Models/Models.cs:254-257`），pi 同（`models.ts:924-928`）。我们与两参考源都不同，类别应为 D（弱化错误分类）而非 B | `Models.Streaming.cs:66-68`；spec §4.1"（实现口径：未包 ModelsError，登记 B 偏差）" | 已登记但**类别标注失真**——B 的定义是"借 pi.net 的 C# 表达"，此处 pi.net 恰恰相反 | 二选一：① 改 `throw new ModelsError(ModelsErrorCode.Provider, $"Unknown provider: {providerId}")`（一行，向两参考源同时看齐）；② spec §4.1/§6 改标 D 并写明代价（消费方拿不到 Code=Provider）。公开 API 今日不可观察（私有方法，异常被 LazyStream 转终态消息，文案同 pi） |
| **N2** | Minor（未登记行为偏离） | LazyStream **setup 失败路径**不调 `observer.OnEvent(error 事件)`；pi 在 catch 里先 `outer.push(event)` → `observer?.onEvent?.(event)` → `outer.end(message)` → `observeResult(message)` | `LazyStream.cs:55-71`（catch 仅 Push→End→OnResult）vs pi `api/lazy.ts:79-90` | spec §0.1/§4.5 声称"顺序按 pi"，但失败路径漏了 OnEvent；无 A/B/C/D 论证 | 一行修复（catch 中 `outer.Push(...)` 后加 `observer?.OnEvent(...)`）或 spec §4.5 补登记；顺带补"失败路径收到 error 事件"测试断言 |
| **N3** | Minor（未登记行为偏离） | Stream 认证解析未把请求级 `Env` 传入 `AuthResolutionOverrides`：pi `applyAuth` 调 `getAuth(model, { apiKey, env: options?.env, signal })`（`models.ts:836-840`），请求 env 会 overlay 解析期 AuthContext（`resolve.ts:55-57,96-104`）；我们只传 ApiKey+Signal，请求 env 只参与**事后**合并 | `Models.Streaming.cs:70-77` vs pi `models.ts:837` | spec §4.6.2 未列此项，§6 无行；非语言强制（AuthResolutionOverrides.Env 字段存在） | S4 ambient/D10 开工前修复（一行：`Env = options?.Env`）或登记 C（目标 S4）＋验收测试 |
| **N4** | Minor（未登记形状缺失） | pi 公共导出（`index.ts:20`）`auth/helpers.ts` 的 `envApiKeyAuth(name, envVars)`（标准 env-key 认证工厂，stored key 胜→按序查 env）与 `lazyOAuth` 未移植；pi.net 已移植（`Auth/Helpers.cs:12,64`）。同批：`withClientIdentity`（`identity.ts:148-195`）、identity 头生成族 `formatUserAgent/getAttributionHeaders/getIdentityHeaders/getClientVersion/ClientIdentityTarget` | pi `auth/helpers.ts:9-63`、`identity.ts:17-195`；pi.net `src/Pi.Ai/Auth/Helpers.cs` | 头生成族在 spec §5.3 记"属 D8 后置"，但 **Dn D8 行只写 Telemetry**（`docs/待办-后置能力.md:18`），头生成没有 Dn 家；helpers/withClientIdentity 则完全无登记 | ① Dn 新增一行（或 D8 扩行）："身份/会话头生成 + ClientIdentityTarget"（形状锚点=identity.ts 函数清单，目标 S4/S5）；② helpers.ts 与 withClientIdentity 补进 spec §6（B 类，pi.net 已示范），建议 S4 开工承接任务顺带移植 |
| **N5** | Minor（未登记形状约束变化） | `IProvider.GetAllModels()` 必需；pi `getAllModels?` 可选（"Providers with only chat models may omit it; Models then uses getModels()"，`models.ts:190-197,484`） | `IProvider.cs:18` | no-DIM 是真约束（A/Unity），但"可选成员→必需成员"的方向变化 §6 无行 | spec §6 补行：`getAllModels 可选 → 必需 \| DIM 不可用 \| A/Unity \| 代价=实现方必须提供（仅 chat provider 返回 GetModels 同表）` |
| **N6** | Minor（C 类锚点缺口） | `CreateProviderOptions.FetchModels` 存在，但 `ProviderFactory` 产物**不实现 `IRefreshableProvider`**（`ProviderFactory.cs:36`）；pi `createProvider` 在给 fetchModels 时装配 refreshModels（`models.ts:1117-1143`，含 stored 恢复/publish/known-type 过滤） | 同左 | 行为后置 D2 成立，但 D2 台账锚点未含"Factory 产物装配"这一半，S4 承接时易漏 | D2 台账形状锚点补一句"ProviderFactory 产物在 FetchModels 给出时实现 IRefreshableProvider（S4）" |
| **N7** | Minor（spec 承诺超前） | spec §3.6 称 `ModelsPublication.Persist` 与 `Delete`"运行期验证（必须二选一）"；当前无任何代码消费/校验 ModelsPublication（全仓仅定义处） | `ModelsStore.cs:62-67`；grep 无消费点 | 属 D2 发布路径行为；spec 文字超前于实现 | spec §3.6 该句改为"验证随 D2 发布路径落地"或 D2 验收口径补一条 |
| **N8** | Minor（未登记 D） | `LogoutAsync` 对未知 provider 抛 `ModelsError(Provider,"Unknown provider")`；pi `logout` 无此检查（未存凭据的 provider 也安静删除） | `Models.Auth.cs:161-162` vs pi `models.ts:1014-1022` | 主动加固（D）但 §6 无行（§4.1 正文提及、未分类） | spec §6 补一行 D（收益=早失败；代价=与 pi 行为分歧，未知 provider 登出报错而非 no-op） |

**空串/空白 env 边缘语义（并入 N3 处理，不单列动作）**：三处与 pi 不一致——① override ApiKey 空串：pi `!== undefined`（空串=显式 override，`resolve.ts:53`），我们 `IsNullOrEmpty`（跳过，`Models.Auth.cs:188`）；② OverlayAuthContext：pi `env[name] || base.env(name)`（空串回落 base，`resolve.ts:98-99`），我们 TryGetValue（空串胜出，`Models.Auth.cs:317-320`）；③ DefaultAuthContext：pi 对空白 env 视为 absent（`auth/context.ts:41-43`），我们原样返回（`AuthContext.cs:16-20`）。影响仅限空串/全空白 env 的病态输入；随 N3 修复或登记时一并写明。

**观察（不计动作）**：`ModelOperations.ClampThinkingLevel` 在"可用档位为空"的病态模型上会 `available[0]` 越界（`ModelOperations.cs:88,98`），pi 返回 `availableLevels[0] ?? "off"`（`models.ts:1288,1300`）——仅当 off..high 全被 map 为 null 才触发，S4 顺手加 `?? Off` 兜底即可。`StaticProvider.StreamSimple` 把 `SimpleStreamOptions` 委派给单一 `StreamFn`（`StaticProvider.cs:50-54`），属已登记 D（StaticProvider 便捷类）的实现细节，代价=stream 实现需容忍 Simple 选项。

---

## 4. C 类 Dn 检查（台账 `docs/待办-后置能力.md`）

| Dn | 形状锚点 | 验收测试锚点 | 目标阶段 | 状态 | 审计判定 |
|---|---|---|---|---|---|
| D1 OAuth | `OAuthAuth`(Login/Refresh/ToAuth)、`CredentialStore.ModifyAsync/ListAsync`、`AuthInteraction` ✅ 与代码一致 | `AuthShapeTests.OAuthAuth_Has_ToAuth_And_No_Logout:115` ✅、`ModelsAuthTests.Login_Is_Deferred_To_S5:274` ✅ | S5 | 未开工 | ✅ |
| D2 目录刷新 | `IModelsStore/ModelsStoreEntry/ModelsPublication`+`IRefreshableProvider`、`Models.RefreshAsync` ✅；**缺 N6 的 Factory 装配半边、N7 的发布验证** | 台账写 `AuthShapeTests.InMemoryModelsStore_Reads_Writes_And_Deletes`——**实际在 `Auth/ModelsStoreTests.cs:9`**（文件名过时，测试本身存在） | S4 | 未开工 | ⚠️ 锚点文件名订正 + N6/N7 补记 |
| D3 Deferred | `IDeferredProvider`、`ProviderStreams.FetchDeferred/CancelDeferred`、Models 三方法、Faux `DeferredFetchCount/CancelledDeferred` ✅（`Faux.cs:94,99` 形状锚点注释在） | `ModelsCapabilityTests.Deferred_Methods_Are_Deferred_To_S8` ✅ | S8 | 未开工 | ✅ |
| D4 图片 | `IImagesProvider/ProviderImages/ImagesOptions`、`Models.GenerateImagesAsync` ✅ | `ModelsCapabilityTests.Images_And_Classify_Are_Deferred_To_2B` ✅、`ProviderFactoryTests.Factory_Exposes_Images_And_Classifier_Capabilities:91` ✅ | 2B | 未开工 | ✅ |
| D5 分类 | 同上镜像 ✅ | 同上 ✅ | 2B | 未开工 | ✅ |
| D6 Prompt cache | `Model.PromptCache/StreamOptions.CacheRetention` ✅ | ModelShape/StreamOptions Tests ✅ | S4/S5 | 未开工 | ✅ |
| D7 持久化 store | `CredentialStore`(Read/List/Modify/Delete)、`InMemoryCredentialStore` ✅ | `AuthShapeTests.InMemoryCredentialStore_Reads_Lists_Modifies_And_Deletes:10` ✅ | S5 | 未开工 | ✅ |
| D8 Telemetry | `ILazyStreamObserver`（`LazyStream.Run` 第三参）+ TelemetrySpan 保留 + `LazyStreamObserver.cs:15` TODO ✅；**身份/会话头生成无 Dn 家（N4）** | `StreamOptionsTests.StreamOptions_Carries_The_Complete_Request_Shape:13` ✅ | 后续扩展 | 未开工 | ⚠️ N4 补行 |
| D9 目录数据 | 纯数据（表内自述） | `ModelShapeTests.ProviderModel_Defaults_Are_Stable` ✅ | S4 | 未开工 | ✅ |
| D10 可用性过滤 | `IFilterableProvider.FilterModels/FilterAllModels`、`GetAvailable*` ✅ | `ModelsAuthTests.CheckAuth_And_GetAvailable_Filter_Configured_Providers:204` ✅（保序/非null胜出另在 `ModelsTests:225,260`） | S4/S5 | 进行中 | ✅ |
| D11 重放辅助 | `TranscriptNormalization`（文件级） | `TranscriptNormalizationTests` ✅ | S9 | 未开工 | ✅ |
| D12 Auth 取消+单次解析 | `ResolveAuthAsync`(Signal 贯穿)、`CheckAuthAsync`、单次 Check ✅ | **S4 承接**（台账明记；spec §7.1 同步声明此例外） | S4 | 进行中 | ✅（声明一致） |
| D13 Fetch 超时测试 | `FetchOptions.*`/`MockFetch` | S4 新增 | S4 | 未开工 | ✅ |

代码锚点核对：`TODO(S4,D2)`×1、`NotSupportedException(...Dn)`×8（D1×1/D3×3/D4×2/D5×2）、`TODO(后续扩展, D8)`×1——`check-deferred.py --gate all` 实跑 OK（D1=1, D2=1, D3=3, D4=2, D5=2, D8=1；registry 13 条一致）。✅

**spec §7.1 声明与测试实况的两处出入（Minor）**：
1. "**Check 存在时 null 不回退 Resolve**"——无此测试。`ApiKeyAuth.Check` 仅在 `AuthShapeTests:103` 以形状出现，从未经 `CheckAuthAsync` 权威路径 exercised。spec §7.1 称"以上测试均已落地（例外：D12）"，此处不实。
2. "**异步回调不被 lock 阻塞**"——`Modify_Is_Serialized_Per_Provider`（`AuthShapeTests:51`）证明的是串行性，不能区分"信号量"与"lock 内阻塞"实现。低风险（实现实读为信号量），但声明超额。

---

## 5. 必需动作（合并/交付前）

- [ ] **N1**：`RequireProvider` 改抛 `ModelsError(Provider, "Unknown provider: ...")`（一行，向 pi 与 pi.net 同时看齐），或 spec §4.1/§6 把该偏离改标 D 并写明代价。**推荐前者**（同时消掉 §4.1 那段"例外"说明）。
- [ ] **N2**：LazyStream catch 路径补 `observer?.OnEvent(error 事件)`（一行）或 spec §4.5 补登记；建议随 N1 同批修复并加断言。
- [ ] **N3**：`ResolveAuthForStreamAsync` 补 `Env = options?.Env`（一行）或登记 C（目标 S4，D10/D12 承接）；顺带把未配置错误文案与 pi 对齐（`Provider is not configured: ...`）或登记。
- [ ] **N4**：Dn 新增（或 D8 扩）"身份/会话头生成 + ClientIdentityTarget"行；`envApiKeyAuth`/`lazyOAuth`/`withClientIdentity` 补进 spec §6（B，pi.net 已示范），目标阶段 S4。
- [ ] **N5/N6/N7/N8**：spec §6 补 4 行（GetAllModels 必需化 A/Unity；Logout 未知 provider 检查 D；FetchModels→IRefreshableProvider 装配随 D2；ModelsPublication 验证随 D2），D2 台账锚点同步（文件名 `ModelsStoreTests` 订正）。
- [ ] **spec §7.1**：把"Check null 不回退 Resolve"从"已落地"改为实际状态（补测试或移入 S4 承接清单）。
- [ ] 完成上述登记/一行修复后**无需新一轮完整审计**（本轮无 Shape gap/Critical）；若选择改代码（N1/N2/N3 一行族），建议由控制器做聚焦 diff 复核即可。

## 6. 审计者附注

- **无法验证项**：① UnityLlmTransport `87db637` 未独立核验（本分支零通讯层代码，风险为零）；② 未运行构建/测试（只读审计），"Release 0/0、229/229"引用控制器门禁记录；`check-deferred --gate all` 为本审计唯一实跑命令（只读脚本）。
- **预审计修正核验**：a305315 实diff 确认 `CreateProviderOptions.Models` 改 `required`、工厂校验改为**数非 null 实现**（`ProviderFactory.cs:25-28`），与用户裁决一致；plan Task 3 的"空 ApiByApi 合法"旧口径已被 spec §4.3/§6 表注 + `ProviderFactoryTests:72` 取代，不再作为缺陷。
- **误报排除（沿用 v2 口径）**：方法 Async 化、`IAsyncEnumerable` 代替 `result()`、枚举化 wire 联合、`Task.Run` 调度、union→record（DeferredRequest/ClientAttribution/Deferred options）、`Models` 类 vs `createModels` 工厂、`UsageCost` 不可变返回替代 pi 的 usage.cost 原地突变——均 A/B 合规，不计缺口。
- **范围外观察**：pi `index.ts` 导出的 `lazyApi`/`LazyApiCapabilities`（动态 import API 模块包装，`api/lazy.ts:97-127`）无 C# 等价物——属 TS 模块加载生态机制，S3 无 API 实现可包装；S4 API 实现开工时决定是否需要并登记。`session-resources.ts`、`utils/{retry,overflow,streaming-json}.ts` 等工具族同属后续阶段范围（此前批次已划定）。
- 本审计未发现 wire 字符串错误、未发现越界引用、未发现无主 TODO/stub；v2 FAIL 的 25 项全部核销属实。
