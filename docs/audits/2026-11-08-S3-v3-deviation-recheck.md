# S3 v3 偏离审查 · 复核（2026-11-08）

> 审计者：独立复核审计 subagent（GLM，非实现会话、非上一轮审计者；只读，未改任何代码/spec/README/index/HEAD/分支）
> 参考版本（本会话实核）：pi `v1.5.0` / `f9a263655`（`git log` 实核 HEAD=tag v1.5.0, Release v1.5.0）；pi.net `main` / `2d6757b`（实核 `git log -1`）；UnityLlmTransport `87db637`（未独立核验 commit；本分支零通讯层代码，风险为零）
> 范围：`269f48a..d5142d8`（11 commits，HEAD=d5142d8；其中 `a305315..d5142d8` 为审计后修复单提交）；diff 包 `.superpowers/sdd/s3v3-audit-recheck-269f48a..d5142d8.diff` 与 `git diff 269f48a..d5142d8` 文件集一致（均 52 文件，实核）
> 驱动：上一轮审计 `docs/audits/2026-11-08-S3-v3-deviation-audit.md`（PASS_WITH_REGISTERED_DEVIATIONS，N1–N8）+ 修复报告 `.superpowers/sdd/s3v3-audit-fix-report.md`
> 结论：**PASS_WITH_REGISTERED_DEVIATIONS**
>
> 判定依据：上一轮 8 项 Minor（N1–N8）**全部核销**（§0 逐项 file:line 证据）；修复改动本身与 pi 语义逐句一致、**未引入新 Shape gap**（§1）；上一轮已 PASS 的关键形状抽查**全部未被破坏**（§2）；spec §6/§7.1 与代码无现势性矛盾、Dn 台账 14 条 check-deferred 可解析（§3）。本轮新发现仅 1 项 Minor 登记质量问题（R1，D14 形状锚点指向尚不存在的文件）+ 2 项观察（不计动作），无未登记 Shape gap / Critical / Important。

---

## 0. 上一轮 N1–N8 逐项核销核验

图例：✅ 已核销（代码+测试+spec 三位齐）；判定只基于本会话实读的当前 HEAD=d5142d8 工作区与 pi/pi.net 源码。

| # | 上一轮要求 | 现状证据（file:line） | 判定 |
|---|---|---|---|
| **N1** | `RequireProvider` 抛 `ModelsError(Provider)`；spec 删除 `InvalidOperationException` 主张与"B 例外" | `src/PiUnityAgent.Ai/Providers/Models.Streaming.cs:66-68`：`GetProvider(providerId) ?? throw new ModelsError(ModelsErrorCode.Provider, $"Unknown provider: {providerId}")`＝pi `packages/ai/src/models.ts:843-848`（`throw new ModelsError("provider", \`Unknown provider: ${model.provider}\`)`）＋pi.net `src/Pi.Ai/Models/Models.cs:254-257`（逐字同构）。测试 `tests/PiUnityAgent.Ai.Tests/Providers/ModelsAuthTests.cs:431-447`（反射锁 `Code == ModelsErrorCode.Provider` + pi 文案）。spec：§2 D3 行 `design.md:79`、§4.1 `:333`、§6 ModelsError 行 `:637`（"RequireProvider/Logout 均按 pi 口径（审计后修复，无例外）"）全部改口径；`grep InvalidOperationException design.md` 仅剩 `:41`（§0.2 历史记述"原 InvalidOperationException"）与 `:764`（版本记录历史行），无现势性主张；`:335` 明确"全部门面路径已用 ModelsError，无例外" | ✅ 已核销 |
| **N2** | LazyStream setup 失败按 pi 顺序 `Push(errorEvent)`→`observer.OnEvent`→`End`→`observer.OnResult`，且有测试 | `src/PiUnityAgent.Ai/Events/LazyStream.cs:68-73`：`outer.Push(errorEvent)` → `observer?.OnEvent(errorEvent)` → `outer.End(message)` → `observer?.OnResult(message)`＝pi `packages/ai/src/api/lazy.ts:79-82`（`outer.push(event)` → `observer?.onEvent?.(event)` → `outer.end(message)` → `observeResult(message)`）逐句同序。测试 `tests/PiUnityAgent.Ai.Tests/Events/LazyStreamTests.cs:190-207` `Observer_Receives_Error_Event_On_Setup_Failure`（恰一个 `AssistantMessageEvent.Error`、`Reason=Error`、`Assert.Same(done, error.Message)`、OnResult 恰一次）；既有 `:212` `Observer_OnResult_Is_Called_Exactly_Once_On_Success` 仍在。spec §4.5 `design.md:485` 补记失败路径顺序 | ✅ 已核销 |
| **N3** | 请求级 Env 进 overrides；未配置文案、空串 env、显式空 ApiKey 按 pi | ① 请求级 Env：`Models.Streaming.cs:74-86` `ResolveAuthForStreamAsync(Model, StreamOptions?)` 传 `Env = options?.Env`（`:79`）＝pi `models.ts:867-872`（`getAuth(model, { apiKey, env, signal })`）；`Stream`/`StreamSimple` 两调用点同步（`:24`/`:50`），`SimpleStreamOptions : StreamOptions`（`Types/SimpleStreamOptions.cs:3`）一个签名覆盖两端。② 文案：`:84` `$"Provider is not configured: {model.Provider}"`＝pi `models.ts:873` 逐字。③ 显式空 ApiKey 走 Resolve：`Models.Auth.cs:187-192` `overrides?.ApiKey is not null && auth.ApiKey is not null`＝pi `resolve.ts:56` `overrides?.apiKey !== undefined`；测试 `ModelsAuthTests.cs:168`（resolver 收到 ""）。④ ApplyAuthOptions 空串胜出：`Models.Streaming.cs:96/:109` `options.ApiKey is null ? auth.Auth.ApiKey : options.ApiKey`＝pi `models.ts:878` `options?.apiKey ?? auth.apiKey`；测试 `:75`。⑤ overlay env 空串回落 base：`Models.Auth.cs:317-324`＝pi `resolve.ts:97` `env[name] || (await base.env(name))`；测试 `:198`。⑥ 默认 context 空白 env=absent：`Providers/Auth/AuthContext.cs:21-25` `string.IsNullOrWhiteSpace(value) ? null : value`＝pi `auth/context.ts:26-28`；测试 `AuthShapeTests.cs:147`。spec §4.6.2 `design.md:501` + §6 `:652-653` 两行登记 | ✅ 已核销 |
| **N8** | Logout 未知 provider no-op；spec 更新 | `Models.Auth.cs:159-175`：未知 provider 校验已删（注释 `:161` 记 pi 语义），只 `DeleteAsync` + 失败包 `ModelsError(Auth, $"Credential store delete failed for {providerId}", error)`＝pi `models.ts:832-841`（无校验、消息逐字同）。测试 `ModelsAuthTests.cs:417-428` `Logout_Unknown_Provider_Is_No_Op`（未注册 provider 的 stored 凭据删净）。spec §4.1 `:333`"未知 provider 是 no-op（不校验 provider 是否注册）"+ §6 `:657` 类别 **—（回归 pi）**，不登记 D | ✅ 已核销 |
| **N4** | D14 台账行 + spec §6 行登记 helpers/withClientIdentity/头生成 | 台账 `docs/待办-后置能力.md:24` **D14 行**：参考（pi `auth/helpers.ts`/`utils/identity.ts`、pi.net `Auth/Helpers.cs`——实核 pi.net `src/Pi.Ai/Auth/Helpers.cs:12` `EnvApiKeyAuth`、`:67` `LazyOAuthAuth` 均在）、形状锚点、测试锚点（S4 新增）、S3 状态=未移植、目标 S4、验收口径、状态=未开工、备注（"D8 只含 Telemetry，头生成归 D14"）全列。spec §6 `:654`（`envApiKeyAuth`/`lazyOAuth`，B，S4/D14）+ `:655`（`withClientIdentity`/identity 头生成，B，头生成 S4/D14）。`check-deferred.py --gate all` 实跑 OK（registry 14 条，含 D14） | ✅ 已核销（锚点措辞见 R1） |
| **N5** | GetAllModels 必需化登记 | spec §6 `:651`：pi `getAllModels?` 可选（仅 chat 可省）→ 我们必需，类别 **A/Unity**，代价写明（"仅 chat provider 返回 GetModels 同表即可"）。代码 `IProvider.cs:18` `IReadOnlyList<ProviderModel> GetAllModels();` 必需 | ✅ 已核销 |
| **N6** | FetchModels→IRefreshableProvider 装配随 D2 登记 | spec §6 `:656`（C/D2）；台账 D2 形状锚点 `待办-后置能力.md:12` 追加"`ProviderFactory` 产物在给出 `FetchModels` 时实现 `IRefreshableProvider`（S4 装配，审计 N6）"。代码现状与登记一致：`ProviderFactory.cs:36` `CreatedProvider : IProvider, IFilterableProvider, IImagesProvider, IClassifierProvider`（不含 IRefreshableProvider）＝登记的后置态 | ✅ 已核销 |
| **N7** | ModelsPublication "运行期验证二选一"口径订正 | spec §3.6 `design.md:305`："Persist 与 Delete 必须二选一；**验证随 D2 发布路径落地**（S4 发布实现时校验；本 spec 只定形状）"；台账 D2 验收口径 `:12` 追加"ModelsPublication Persist/Delete 二选一在发布路径验证（审计 N7）"。代码 `Auth/ModelsStore.cs`（ModelsPublication 形状）无运行期校验＝与 spec 现口径一致，不再超前 | ✅ 已核销 |
| **§7.1 两处超额声明** | Check 权威性补真测试；串行化声明改实际口径 | ① 新测试 `ModelsAuthTests.cs:363-390` `CheckAuth_Check_Is_Authoritative_Null_Does_Not_Fall_Back_To_Resolve`：stored ApiKey + `Check` 返 null + Resolve 会成功 → `CheckAuthAsync` 返 null 且 `Assert.False(resolveCalled)`；对应代码 `Models.Auth.cs:63-68`（`apiKey.Check is not null` 即权威，不回退）。spec §7.1 `:670` bullet 附测试名；`:693`"审计后补充"清单在列。② `:668` 改为"串行性为测试所证（`Modify_Is_Serialized_Per_Provider`），锁内实现为结构事实（per-provider `SemaphoreSlim`、`await` 回调在信号量内、`lock` 仅护字典读写）"——与 `Credential.cs:79-105`（`:88` `await gate.WaitAsync` 在 try 内、`:93/:97` lock 仅护字典、`:103` `return current`）逐点相符 | ✅ 已核销 |
| **D2 锚点文件名订正** | `AuthShapeTests.InMemoryModelsStore_...` → `Auth/ModelsStoreTests.InMemoryModelsStore_...` | 台账 `:12` 测试锚点已改 `Auth/ModelsStoreTests.InMemoryModelsStore_Reads_Writes_And_Deletes`＝实际 `tests/PiUnityAgent.Ai.Tests/Providers/Auth/ModelsStoreTests.cs:9` | ✅ 已核销 |

**小结**：8/8 核销。N1/N2/N3/N8 为代码修复（均有先红后绿测试，修复报告 TDD 记录与测试实况一致：新增 9 测试＝LazyStream 1 + AuthShape 1 + ModelsAuth 7）；N4–N7 与 §7.1 为登记/口径订正（均落到 spec/台账实际行）。

---

## 1. 修复改动本身的形状复核（未引入新 Shape gap）

修复提交 `d5142d8` 触及 src 4 文件。逐文件与 pi 对照：

### 1.1 `Events/LazyStream.cs`（N2）

- 失败路径顺序＝pi `api/lazy.ts:79-82` 逐句（§0 N2）。
- 正常路径未动：`Push→OnEvent`（`:47-48`）＝pi `lazy.ts:44-46`；`End→OnResult`（`:52-53`）＝pi `lazy.ts:51-52`。
- **已核差异（观察 O1，不计动作）**：pi 的 `observeResult` 带 once-guard（`lazy.ts:67-70` `if (observedResult) return; observedResult = true;`），我们靠 try/catch 单路径结构性保证"恰一次"。观察者契约内（OnEvent/OnResult 不抛）两者等价；仅当 observer.OnResult 自身抛异常时分叉（pi 不二次回调，我们会进 catch 再回调一次）。属容错健壮性细节，非公共形状/正常行为差异；如要逐字对齐可在 catch 前加同款哨兵，S4/D8 顺手即可。

### 1.2 `Providers/Auth/AuthContext.cs`（N3⑥）

- `DefaultAuthContext.EnvAsync` 空白/空 env 返 null（`:21-25`）＝pi `auth/context.ts:24-28`。全仓唯一消费点 `Models.cs:33`（`options?.AuthContext ?? new DefaultAuthContext()`），无其他调用方依赖原始值，无回归面。

### 1.3 `Providers/Models.Auth.cs`（N3③⑤/N8）

- `ResolveAuthAsync` 解析顺序未被改动破坏（§2.1）；override 分支空串语义（`:188`）与 pi `resolve.ts:56` 一致。
- stored ApiKey 分支（`:215-221`）：`MergeEnv(apiKey.Env, overrides?.Env)`（request 胜）＝pi `resolve.ts:71-73` `{ ...stored.env, ...overrides.env }`；overlay 经 `ResolveApiKeyAsync(..., overrides?.Env)` 传入＝pi `requestAuthContext`（`resolve.ts:54`）。
- **已核差异（等价，不计）**：`ResolveApiKeyAsync` 建 overlay 的判定是 `overrideEnv is { Count: > 0 }`（`:240`），pi 是 `overrides?.env` 真值（`resolve.ts:54`，空对象 `{}` 在 JS 为真值→建 overlay）。行为等价：空 env 的 overlay 对每个名字都 `env[name]`(undefined)→回落 base＝无 overlay；非空 dict（含空串值）两者都建 overlay 且空串值都回落 base。无形状/语义缺口。
- `LogoutAsync`（`:159-175`）＝pi `models.ts:832-841`（§0 N8）；OCE 透传、其余包 `ModelsError(Auth)` 与 pi 一致。

### 1.4 `Providers/Models.Streaming.cs`（N1/N3①②④）

- `RequireProvider`（`:66-68`）＝pi `models.ts:843-848` / pi.net `Models.cs:254-257`（§0 N1）。
- `ResolveAuthForStreamAsync`（`:74-86`）＝pi `applyAuth` 的 getAuth 调用 + 未配置抛错（`models.ts:866-874`）：ApiKey/Env/Signal 三件齐，文案逐字。
- `ApplyAuthOptions` 两重载（`:88-113`）：`ApiKey = options.ApiKey is null ? auth.Auth.ApiKey : options.ApiKey`＝pi `models.ts:878`；`MergeHeaders(auth 头, request 头)` request 胜＝pi `models.ts:882-884`（auth.headers 先、options.headers 后；pi 的 `getOpenCodeSessionHeaders` 会话头注入未实现＝已登记 D14/N4，见 §3）；`MergeEnv(auth.Env, options.Env)` request 胜＝pi `models.ts:888`。
- `ApplyIdentity`（`:115-126`）在 TransformHeaders 之前＝pi `mergeClientIdentity`（`models.ts:889`）先于 transform（`:886`）的次序语义一致。
- `ApplyTransformHeaders` 执行后摘除（`:128-151`）+ 调用点 `applied with { TransformHeaders = null }`（`:29`/`:55`）＝pi `models.ts:886,891`（transform 后从 requestOptions 剥除）。
- `requestModel` baseUrl 覆盖（`:28`/`:54`）＝pi `models.ts:879`。

**结论**：修复 4 文件无新公共形状、无新未登记偏离；全部向 pi 收敛。pi.net 语义（`Models.cs:254-257`）交叉印证 N1。

---

## 2. 上一轮已 PASS 关键形状抽查（未被本轮改动破坏）

| 形状 | 当前证据 | 判定 |
|---|---|---|
| Auth 解析顺序（override→stored OAuth→stored ApiKey→stored 无匹配 null→ambient） | `Models.Auth.cs:186-228`：override `:188-192`→stored OAuth `:196-213`（ToAuth 失败包 `ModelsError(OAuth)` `:210`）→stored ApiKey `:215-221`→`stored is not null return null` `:224`→ambient `:228`。与 pi `resolve.ts:56-88` 分支一一对应；fix diff 仅改 `:188` 判定式 | ✅ 未破坏 |
| CredentialStore.Modify null=不变/返回写后值/per-provider 信号量/await 不持 lock | `Auth/Credential.cs:79-105`：`:88` `await gate.WaitAsync`（信号量）、`:93`/`:97` `lock` 仅护字典、`:97-103` `next is not null → 写+返回 next，否则 return current`＝pi `auth/credential-store.ts:50-52`（`if (next !== undefined) set; return next ?? current`）；测试 `AuthShapeTests.cs:38`（null 不变返 current）+`:51`（同 provider 串行） | ✅ 未破坏 |
| `GetAuth(model)` 头合并 | `Models.Auth.cs:23-42`：`model.Headers`(string) 升 `string?` 后 `MergeHeaders(result.Auth.Headers, modelHeaders)`——auth 头先、模型头胜；`GetAuthAsync(providerId)` `:13-21` 不合并。fix 未触及 | ✅ 未破坏 |
| FilterAllModels fallback 保序 | `Models.Auth.cs:122-144`：`FilterAllModels` 非 null 胜出（`:134-135`），null→按 chat id 集合过滤、其他类别原样保留（`:137-143`）＝pi `models.ts` filter fallback；测试 `ModelsTests.cs`（保序 + 非 null 胜出）仍在 | ✅ 未破坏 |
| ClientIdentity 形状 + 逐字段合并 | `Types/ClientIdentity.cs:6-61`：`UserAgentMode` 三态（internal 标记）、`ClientAttribution`、`AttributionSet` 哨兵（`:26-33` init 时置位）、`ClientIdentityMerger.Merge`（`:40-61`，Attribution 看 `AttributionSet` 不是 `??`）＝pi `utils/identity.ts`；`Models.Streaming.ApplyIdentity` 请求级覆盖门面级（`:115-126`） | ✅ 未破坏 |
| TransformHeaders Models 摘除 | `Models.Streaming.cs:29`/`:55` `applied with { TransformHeaders = null }`；执行在 provider 前（`:128-151`） | ✅ 未破坏 |
| `CreateProviderOptions.Models` required + 空/全 null ApiByApi 拒绝 | `CreateProvider.cs:17` `required IReadOnlyList<ProviderModel> Models`；`ProviderFactory.cs:23-35`：auth 至少一法（`:15-17`）、Api/ApiByApi 互斥（`:18-20`）、**数非 null 实现**（`:27-31` `Values.Any(entry => entry is not null)`）、全空抛 `ModelsError(Provider)`（`:32-34`） | ✅ 未破坏 |
| Faux keyless auth | `Faux/FauxProvider.cs:50-62`：`ApiKeyAuth { Name="Faux", Resolve → AuthResult(new ModelAuth(), null, "Faux") }` | ✅ 未破坏 |
| 边界（Ai 不引游戏/Unity/传输） | `grep -rn "UnityEngine\|UnityLlmTransport\|GameData\|Harmony\|HttpClient" src/PiUnityAgent.Ai --include=*.cs`＝**0 命中**（本会话实跑） | ✅ 未破坏 |

---

## 3. spec §6/§7.1 与代码一致性 · Dn 台账 14 条

### 3.1 spec §6 抽查（重点核对审计后新增/改口径行）

| §6 行 | 代码实况 | 判定 |
|---|---|---|
| `:637` ModelsError"RequireProvider/Logout 均按 pi 口径（无例外）" | `Models.Streaming.cs:66-68` + `Models.Auth.cs:159-175` | ✅ |
| `:651` GetAllModels 必需（A/Unity） | `IProvider.cs:18` | ✅ |
| `:652` 请求级 Env 参与解析（含空串语义四条） | `Models.Streaming.cs:79` + `Models.Auth.cs:188,317-324` + `AuthContext.cs:21-25` | ✅ |
| `:653` 未配置文案 pi 逐字 | `Models.Streaming.cs:84` | ✅ |
| `:654` envApiKeyAuth/lazyOAuth 未移植（B，S4/D14） | 无 `AuthHelpers.cs`（实核）；pi.net `Auth/Helpers.cs:12,67` 已示范 | ✅（登记属实） |
| `:655` withClientIdentity/头生成：形状+合并已落、头生成未移植 | `Types/ClientIdentity.cs` 在；头生成族无 | ✅（登记属实） |
| `:656` FetchModels→IRefreshableProvider 装配随 D2/S4 | `CreateProvider.cs:21` 形状在；`ProviderFactory.cs:36` 产物未实现 IRefreshableProvider | ✅ |
| `:657` Logout 未知 provider no-op（—，回归 pi） | `Models.Auth.cs:159-175` | ✅ |

### 3.2 spec §7.1 与测试实况

- `:668` 串行化声明＝`Credential.cs:79-105` 结构事实 + `AuthShapeTests.cs:51` 测试所证（口径已收窄，无超额）。
- `:670` Check 权威 bullet 附真测试名（`ModelsAuthTests.cs:363`）。
- `:691` "实现核对"声明保留 D12 例外（S4 承接）——与台账 D12 `:22`（"CT 传递/单次解析验收测试 S4 开工承接"）一致。
- `:693` 审计后补充清单 9 测试名与测试文件逐一实存（§0 表）。**无现势性超额声明残留。**

### 3.3 Dn 台账 14 条锚点核对

`check-deferred.py --gate all` 本会话实跑：`code markers: D1=1, D2=1, D3=3, D4=2, D5=2, D8=1; registry entries: 14; OK`。

| Dn | 形状锚点实存性（本会话 grep/ls 实核） | 判定 |
|---|---|---|
| D1 | `OAuthAuth`(ProviderAuth.cs)、`CredentialStore.ModifyAsync/ListAsync`(Credential.cs:26-37)、`AuthInteraction` | ✅ |
| D2 | `IModelsStore/ModelsStoreEntry/ModelsPublication`(ModelsStore.cs)、`IRefreshableProvider`(ProviderCapabilities.cs)、`Models.RefreshAsync`(Models.Auth.cs:146-150)；N6 半边/N7 验证已补记 | ✅ |
| D3 | `IDeferredProvider`(ProviderCapabilities.cs)、`ProviderStreams.FetchDeferred/CancelDeferred`(ProviderStreams.cs:14-15)、`Models.StreamDeferred/FetchDeferredAsync/CancelDeferredAsync`(Models.Capabilities.cs:10-24)、Faux `DeferredFetchCount/CancelledDeferred`(Faux.cs:94,99) | ✅ |
| D4/D5 | `IImagesProvider/ProviderImages/ImagesOptions`、`IClassifierProvider/ProviderClassifier/ClassifierOptions`、`Models.GenerateImagesAsync/ClassifyAsync`(Models.Capabilities.cs:31,38) | ✅ |
| D6 | `Model.PromptCache`(Model.cs:60)、`StreamOptions.CacheRetention`(StreamOptions.cs:31) | ✅ |
| D7 | `CredentialStore/InMemoryCredentialStore`(Credential.cs) | ✅ |
| D8 | `ILazyStreamObserver` + `TelemetrySpan`(StreamOptionTypes.cs:56) + `LazyStreamObserver.cs:15` TODO | ✅ |
| D9 | 纯数据（SOP 允许 `—`）；`ModelShapeTests.ProviderModel_Defaults_Are_Stable` 在 | ✅ |
| D10 | `IFilterableProvider.FilterModels/FilterAllModels`、`Models.GetAvailable*`(Models.Auth.cs:75-144) | ✅ |
| D11 | `TranscriptNormalization` + 测试在 | ✅ |
| D12 | `ResolveAuthAsync`(Signal 贯穿 :181)、`CheckAuthAsync`(:44)、`GetAvailable*` 单次 Check(:82,126)；验收测试 S4 承接（台账明记） | ✅ |
| D13 | `FetchOptions.FirstByteTimeout/IdleTimeout`(Types/Fetch.cs:40-41) | ✅ |
| **D14** | **`Auth/AuthHelpers.cs`、`Types/ClientIdentityHelpers.cs` 均不存在**（`ls` 实证；行内标注"S4 新建"、S3 状态"未移植"） | ⚠️ 见 R1 |

目标阶段合理性：D2/D9/D10/D12/D13/D14→S4、D1/D7→S5、D3→S8、D4/D5→2B、D6→S4/S5、D8→后续扩展、D11→S9——与 spec §1.2 非目标及各 Dn 验收口径自洽，无矛盾。

---

## 4. 本轮新发现（全部 ≤ Minor；无未登记 Shape gap）

| # | 严重度 | 发现 | 证据 | 为什么不是 gap | 建议 |
|---|---|---|---|---|---|
| **R1** | Minor（登记质量） | D14 的形状锚点写的是 `Auth/AuthHelpers.cs`/`Types/ClientIdentityHelpers.cs`（标注"S4 新建"），两文件当前不存在；治理 SOP `docs/SOP/后置能力交接治理-SOP-2026-11-07.md:42` 要求"形状锚点必须能定位到 `src/` 或 `tests/` 的具体符号"。D14 属"形状也未移植"能力（非台账标准的"形状已补、行为后置"），锚点定位的是计划文件而非现存符号 | 台账 `待办-后置能力.md:24`；`ls src/PiUnityAgent.Ai/Auth/`→不存在 | 偏离本体已登记（spec §6 `:654-655` 类别 B、S4/D14 承接＝上一轮 N4 的建议解法），check-deferred 可解析（registry 14 OK）；缺的只是锚点措辞与 SOP 字面的一致性，非形状删除/简化 | 一行台账订正：D14 形状锚点改写为"pi `auth/helpers.ts` `envApiKeyAuth`/`lazyOAuth` + `utils/identity.ts` 头生成函数清单（pi.net `Auth/Helpers.cs:12,67` 已示范；C# 文件 S4 落地后回填实际符号）"。控制器可直接改，无需再审计 |
| **O1** | 观察（不计动作） | LazyStream OnResult once-guard：pi 有 `observedResult` 哨兵（`api/lazy.ts:67-70`），我们靠 try/catch 单路径保证；仅当 observer.OnResult 自身抛异常时分叉（§1.1） | `LazyStream.cs:52-53,72-73` | 观察者契约内行为等价；非公共形状差异 | S4/D8 顺手加哨兵（可选） |
| **O2** | 观察（不计动作） | `ClampThinkingLevel` 在"可用档位为空"的病态模型上 `available[0]` 越界（`ModelOperations.cs:88,98`）；pi 返 `availableLevels[0] ?? "off"`（`models.ts:1280,1290`）——上一轮观察项，本轮未动（按上轮口径） | 同左 | 仅 off..high 全被 map 为 null 才触发；上一轮已定性"不计动作" | S4 顺手 `?? Off` 兜底 |

**计数 nit（不计动作）**：spec §0.2 `:45` 与修复报告称"spec §6 新增 5 行"，实际新增/改口径行为 7 行（`:651-657`，其中 `:652/:653` 为 N3 拆出的两行）。 prose 计数误差，无实质影响。

---

## 5. 必需动作

- [ ] **R1**（合并前，控制器一行台账订正即可）：D14 形状锚点措辞改为"pi/pi.net 符号清单（S4 落地后回填实际符号）"，与治理 SOP `:42` 字面一致。**无需新一轮审计。**
- O1/O2 为观察项，随 S4/D8 承接，不构成本次合并前置。
- 除此之外无必需动作：N1–N8 全部核销，spec §6/§7.1 与代码一致，Dn 台账 14 条 check-deferred OK，边界 grep 零命中。

## 6. 审计者附注

- **无法验证项**：① UnityLlmTransport `87db637` 未独立核验（本分支零通讯层代码，与上轮同判：风险为零）；② 未重跑构建/全量单测（控制器已实跑 Release 0/0、238/238、check-deferred OK；本审计独立实跑了 `check-deferred --gate all`＝OK 与边界 grep＝0 命中，均为只读操作）。
- **参考版本核验**：pi `f9a263655`＝tag v1.5.0（`git log -1` 实核）；pi.net `2d6757b`＝main（实核）。diff 包 `s3v3-audit-recheck-269f48a..d5142d8.diff` 与 `git diff 269f48a..d5142d8` 文件集一致（52 文件），包头含 11 commit 清单。审计报告本体 `git show d5142d8:docs/audits/...` 与工作区逐字节一致（`diff` 实核，未被修复提交改动内容）。
- **上一轮行号引用漂移订正（不影响判定）**：上一轮审计/修复报告引用 pi `models.ts:924-928`（requireProvider）、`:1014-1022`（logout）、`resolve.ts:53`（override 判定）、`resolve.ts:98-99`（overlay）、`context.ts:41-43`（blank env）——在 f9a263655 实际为 `models.ts:843-848`、`:832-841`、`resolve.ts:56`、`:96-99`、`context.ts:24-28`。语义主张全部正确，仅行号过时；本报告所有引用均已按 f9a263655 重新定位。
- **修复报告与实况的一致性**：TDD 记录（先红 9 → 绿 38/38）、9 个新测试名与断言、spec/台账改动逐项与当前 HEAD 实况相符；"审计报告入库零改动"属实（git diff 实核）。
- **误报排除**：`ResolveApiKeyAsync` overlay 判定 `Count > 0` vs pi 真值（§1.3，行为等价）；`Task.Run` 调度、Async 化、record 化等沿用上轮 A/B 合规清单；spec §0.2 "5 行"计数 nit 不构成虚报（行内容真实存在）。
- 本复核未发现 wire 字符串错误、未发现越界引用、未发现无主 TODO；未发现任何"为通过而登记"的注水——8 项核销均有对应代码/测试/文档三位证据。
