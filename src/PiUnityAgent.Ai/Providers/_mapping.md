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

## S4 新增映射（OpenAI 家族：ModelCatalog + 8 家 profile + D14 helpers）

| 上游 | 我们 | 说明 |
|---|---|---|
| pi.net `Models/ModelCatalog.cs` | `ModelCatalog.cs` | `ModelCatalog.GetChatModels(providerId)` 读嵌入资源 `Providers/ModelData/{providerId}.json`；顶层按 api 分组、只枚举 `chat:` 前缀键 + 显式 `type != "chat"` 双保险；缺资源/空 id 返回空列表不抛；条目 JSON 内 `provider` 字段优先于目录 key（plan 钉死）、缺 `type` 缺省 chat |
| pi.net `ModelData/*.json` | `ModelData/`（12 份嵌入资源） | openai / xai / deepseek / moonshotai / moonshotai-cn / zai / zai-coding-cn / qwen-token-plan / qwen-token-plan-cn / qwen-token-plan-individual / xiaomi / groq，自 pi.net 原样拷贝；csproj `EmbeddedResource` |
| pi `providers/openai.ts` | `OpenAiProvider.cs` | `Create()`：openai / `https://api.openai.com/v1` / OPENAI_API_KEY / `OpenAiResponses.Streams()` |
| pi `providers/xai.ts` | `XaiProvider.cs` | `Create()`：xai / `https://api.x.ai/v1` / XAI_API_KEY / Responses |
| pi `providers/deepseek.ts` | `DeepseekProvider.cs` | `Create()`：deepseek / `https://api.deepseek.com` / DEEPSEEK_API_KEY / `OpenAiCompletions.Streams()` |
| pi `providers/moonshotai.ts`（intl + cn） | `MoonshotProvider.cs` | `Create()`/`CreateCn()`：moonshotai / moonshotai-cn；api.moonshot.ai|api.moonshot.cn `/v1`；共用 MOONSHOT_API_KEY |
| pi `providers/zai.ts`（zai + coding-cn） | `ZaiProvider.cs` | `Create()`/`CreateCodingCn()`：zai（api.z.ai/api/coding/paas/v4，ZAI_API_KEY）/ zai-coding-cn（open.bigmodel.cn 同路径，ZAI_CODING_CN_API_KEY） |
| pi `providers/qwen-token-plan.ts` | `QwenTokenPlanProvider.cs` | `Create()`/`CreateCn()`/`CreateIndividual()`：ap-southeast-1 / cn-beijing / ap-southeast-1（`.maas.aliyuncs.com/compatible-mode/v1`）；CN 用 QWEN_TOKEN_PLAN_CN_API_KEY，individual 与国际共用 QWEN_TOKEN_PLAN_API_KEY |
| pi `providers/xiaomi.ts` | `XiaomiProvider.cs` | `Create()`：xiaomi / `https://api.xiaomimimo.com/v1` / XIAOMI_API_KEY |
| pi `providers/groq.ts` | `GroqProvider.cs` | `Create()`：groq / `https://api.groq.com/openai/v1` / GROQ_API_KEY |
| pi `createProvider` refreshModels 包装（models.ts:1129-1160） | `ProviderFactory.cs` `RefreshableCreatedProvider` | `FetchModels` 给出时产物实现 `IRefreshableProvider`（D2）；`GetAllModels`=静态基线∪动态覆盖表（同 type+id 就地替换、否则追加） |
| pi `auth/helpers.ts` | `Auth/AuthHelpers.cs` | `EnvApiKeyAuth(name, envVars)`（stored key 胜→按序第一个非空 env）/ `LazyOAuth(name, load, ...)`（首次 login/refresh/toAuth 懒加载且仅一次）；真实 OAuth 行为 D1/S5 |
| pi `utils/identity.ts` | `Types/ClientIdentityHelpers.cs` | `FormatUserAgent`/`GetClientVersion`/`GetAttributionHeaders`/`GetIdentityHeaders`/`ClientIdentityTarget`；UA 版本用 Ai 程序集版本、runtime 段恒 `(browser)`（偏离登记 S4 spec §10） |

> 8 家 family 的 id/baseUrl/env 逐一核对 pi `packages/ai/src/providers/*.ts`（Task 7）；profile 只封装 baseUrl/auth/models，不实现 SSE/JSON 解析（spec §3.1 边界）。

## S5 新增映射（Anthropic/Google 协议 + 7 家 profile）

| 上游 | 我们 | 说明 |
|---|---|---|
| pi `providers/anthropic.ts` | `AnthropicProvider.cs` | `Create()`：anthropic / `https://api.anthropic.com` / `AnthropicMessages.Streams()`；认证链=stored key → `ANTHROPIC_AUTH_TOKEN`（`Authorization: Bearer` 头，source=env 名）→ `ANTHROPIC_OAUTH_TOKEN` → `ANTHROPIC_API_KEY`（按序、空串跳过）；login 走 Secret prompt |
| pi `providers/anthropic.ts:53-69`（federation） | —— 后置 | 工作负载身份联合（三必需 env + 两可选收进 `AuthResult.Env`）未实现，源码留 `TODO(S5,D1)` |
| pi `auth/oauth/load.ts` + `lazyOAuth`（anthropic） | —— Task 6 | `Auth.OAuth`（Claude Pro/Max，isSubscription）在 D1 任务接 `LazyOAuth`；本任务 profile 钉死无 OAuth |
| pi `providers/minimax.ts` + `minimax-cn.ts` | `MinimaxProvider.cs` | `Create()`/`CreateCn()`：minimax（api.minimax.io/anthropic）/ minimax-cn（api.minimaxi.com/anthropic）；`MINIMAX_API_KEY` / `MINIMAX_CN_API_KEY`；AnthropicMessages |
| pi `providers/google.ts` | `GoogleProvider.cs` | google / `https://generativelanguage.googleapis.com/v1beta` / `GEMINI_API_KEY` / `GoogleGenerativeAi.Streams()` |
| pi `providers/fireworks.ts` | `FireworksProvider.cs` | fireworks / `https://api.fireworks.ai/inference` / `FIREWORKS_API_KEY`；`ApiByApi`：anthropic-messages + openai-completions 按 model.Api 分派（openai 组模型自带 `/v1` 后缀 baseUrl，照搬数据） |
| pi `providers/vercel-ai-gateway.ts` | `VercelAiGatewayProvider.cs` | vercel-ai-gateway / `https://ai-gateway.vercel.sh` / `AI_GATEWAY_API_KEY` / AnthropicMessages；typesafe-system-one 分类后置（D5，spec §10） |
| pi `providers/openrouter.ts` | `OpenRouterProvider.cs` | openrouter / `https://openrouter.ai/api/v1` / `OPENROUTER_API_KEY`；`ApiByApi` 双协议；OAuth（Task 6/D1）、openrouter-images（D4）、typesafe 分类（D5）、`GET /models/user` 目录个性化（C 偏离，Task 7 审计登记）均后置；归属/身份头不设 provider 级——走应用级 `ClientIdentity` |
| pi.net `ModelData/{7 份}.json` | `ModelData/` | anthropic(17)/minimax(4)/minimax-cn(4)/google(23)/fireworks(19+10)/vercel-ai-gateway(302 chat,14 classifier 不进)/openrouter(19+370 chat,63 image+33 classifier 不进)，与 pi.net `src/Pi.Ai/ModelData/` 逐字节一致；`ModelCatalog` 零改动即消费 |
