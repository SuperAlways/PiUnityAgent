# S5 Anthropic + Google 协议适配 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 `anthropic-messages` + `google-generative-ai` 两协议、7 个常用 profile，以及 D1 Anthropic/OpenRouter OAuth、D7 文件凭据 store、D6 Anthropic cache、D10 可用性，最后独立审计非 FAIL。

**Architecture:** 复用 S4 的 Fetch/SSE/retry/auth/identity/ModelCatalog 基础设施；`Api/AnthropicMessages.cs` 与 `Api/GoogleGenerativeAi.cs` 只认识 wire；profile 只做 baseUrl/auth/models/api 装配；OAuth token exchange 通过 host 注入的可选 `FetchFunction`，Ai 不引用 HttpClient/UnityLlmTransport。

**Tech Stack:** C# netstandard2.1 + System.Text.Json 8.0.5 + xUnit(net10.0)；pi v1.5.0 / pi.net 2d6757b / UnityLlmTransport 4c0cc4b。

## Global Constraints

- 代码只在 `.worktrees/s5-anthropic-google`（分支 `feature/s5-anthropic-google`）改；main 只留文档。
- Ai 仍 netstandard2.1；零 UnityEngine / UnityLlmTransport / GameData / Harmony / HttpClient。
- S5 协议断言以 pi `anthropic-messages.ts` / `google-generative-ai.ts` 为准；pi.net 仅 C# 参考。
- 首批 profile：Anthropic、MiniMax、MiniMax CN、Google、Fireworks、Vercel AI Gateway、OpenRouter；Fireworks/OpenRouter 用 ApiByApi 双协议。
- D1 必须行为落地（Anthropic/OpenRouter OAuth login/refresh/logout）；D7 FileCredentialStore；D6 Anthropic cache；D10 可用性。
- OAuth token exchange 使用 host 注入的 `FetchFunction`（profile 工厂可选参数）；浏览器 loopback 回调后置，S5 用 copy-code/manual。
- 所有错误编码为流终态；流开始后不重试。
- 每任务 TDD；显式 git add；不 push/不 merge；最后新的独立 subagent 审计。
- Dn 收尾必须过 `python scripts/check-deferred.py --root . --current-stage S5 --gate exit`。

---

## File Structure

```text
src/PiUnityAgent.Ai/
├─ Api/AnthropicOptions.cs
├─ Api/AnthropicMessages.cs
├─ Api/GoogleOptions.cs
├─ Api/GoogleGenerativeAi.cs
├─ Providers/AnthropicProvider.cs
├─ Providers/MinimaxProvider.cs
├─ Providers/GoogleProvider.cs
├─ Providers/FireworksProvider.cs
├─ Providers/VercelAiGatewayProvider.cs
├─ Providers/OpenRouterProvider.cs
├─ Providers/ModelData/{anthropic,minimax,minimax-cn,google,fireworks,vercel-ai-gateway,openrouter}.json
└─ Providers/Auth/
   ├─ FileCredentialStore.cs
   └─ OAuth/{AnthropicOAuth,OpenRouterOAuth,OAuthHttp,Pkce,OAuthInput}.cs
tests/PiUnityAgent.Ai.Tests/
├─ Api/{S5OptionsTests,AnthropicMessagesTests,GoogleGenerativeAiTests}.cs
├─ Providers/Auth/{FileCredentialStoreTests,OAuthTests,S5AuthIntegrationTests}.cs
└─ Providers/S5ProviderProfileTests.cs
```

---

## Task 0: 文档提交、worktree 与基线

**Files:** 无代码；提交 S5 spec/plan/docs，创建 worktree。

**Interfaces:**
- Consumes: main `fedd15b`。
- Produces: `feature/s5-anthropic-google` worktree；基线 Ai 427/427。

- [ ] **Step 1: 提交 S5 spec/plan/docs**

```bash
cd D:/shuoshu/taiwuask/PiUnityAgent
git add docs/superpowers/specs/2026-11-08-S5-Anthropic与Google协议适配-design.md docs/superpowers/plans/2026-11-08-S5-Anthropic与Google协议适配.md docs/05-教学阶段路线.md docs/README.md
git commit -m "docs: S5 Anthropic/Google spec 与实施计划"
```

- [ ] **Step 2: worktree**

```bash
git worktree add .worktrees/s5-anthropic-google -b feature/s5-anthropic-google
cd .worktrees/s5-anthropic-google
```

- [ ] **Step 3: 基线**

```bash
dotnet build PiUnityAgent.slnx -c Release --nologo
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo
python scripts/check-deferred.py --root . --gate all
```

Expected: Release 0/0；Ai 427/427；gate OK。

---

## Task 1: S5 options + model data + D7 FileCredentialStore

**Files:**
- Create `src/PiUnityAgent.Ai/Api/AnthropicOptions.cs`
- Create `src/PiUnityAgent.Ai/Api/GoogleOptions.cs`
- Create `src/PiUnityAgent.Ai/Providers/Auth/FileCredentialStore.cs`
- Create `src/PiUnityAgent.Ai/Providers/ModelData/{anthropic,minimax,minimax-cn,google,fireworks,vercel-ai-gateway,openrouter}.json`
- Test `tests/PiUnityAgent.Ai.Tests/Api/S5OptionsTests.cs`
- Test `tests/PiUnityAgent.Ai.Tests/Providers/Auth/FileCredentialStoreTests.cs`

**Interfaces:**
- Produces `AnthropicOptions`/`GoogleOptions`; `FileCredentialStore(string path)` implements `CredentialStore`.
- Consumes existing `CredentialStore`/`Credential`/`AuthOperationOptions`.

- [ ] **Step 1: 复制模型数据（字节保真）**

```bash
cd D:/shuoshu/taiwuask/PiUnityAgent/.worktrees/s5-anthropic-google
mkdir -p src/PiUnityAgent.Ai/Providers/ModelData
for f in anthropic minimax minimax-cn google fireworks vercel-ai-gateway openrouter; do
  cp D:/shuoshu/taiwuask/pi-study/pi.net/src/Pi.Ai/ModelData/$f.json src/PiUnityAgent.Ai/Providers/ModelData/
done
```

`PiUnityAgent.Ai.csproj` 已有 `<EmbeddedResource Include="Providers/ModelData/*.json" />`（S4 加入），确认存在。

- [ ] **Step 2: 写 options 失败测试**

```csharp
[Fact]
public void Anthropic_Options_Expose_Pi_Fields()
{
    var o = new AnthropicOptions
    {
        ThinkingEnabled = true,
        ThinkingBudgetTokens = 1024,
        Effort = ThinkingLevel.High,
        ThinkingDisplay = AnthropicThinkingDisplay.Summarized,
        InterleavedThinking = true,
    };
    Assert.True(o.ThinkingEnabled);
    Assert.Equal(1024, o.ThinkingBudgetTokens);
    Assert.Equal(ThinkingLevel.High, o.Effort);
}
```

```csharp
[Fact]
public void Google_Options_Expose_Thinking()
{
    var o = new GoogleOptions
    {
        Thinking = new GoogleThinkingOptions { Enabled = true, BudgetTokens = 2048 },
    };
    Assert.True(o.Thinking!.Enabled);
}
```

- [ ] **Step 3: 写 FileCredentialStore 失败测试**

必须覆盖：
1. `Write_Then_Read_RoundTrips_ApiKey_And_OAuth`（临时目录；构造 store → Modify/Write → 重新构造 → Read）。
2. `Modify_Null_Leaves_Entry_Unchanged`。
3. `Modify_Is_Serialized_Per_Provider`（TaskCompletionSource 门闩，复用 S3 AuthShape 模式）。
4. `Missing_File_Reads_Null`；`Corrupt_Json_Throws_ModelsError`。
5. `Write_Is_Atomic_And_Leaves_No_Temp_File`（写后 `path.tmp` 不存在；文件内容完整）。
6. `OAuth_Credential_Serializes_Refresh_Access_Expires`。

- [ ] **Step 4: 实现 options**

```csharp
public enum AnthropicThinkingDisplay { Summarized, Omitted }

public sealed record AnthropicOptions : StreamOptions
{
    public bool? ThinkingEnabled { get; init; }
    public int? ThinkingBudgetTokens { get; init; }
    public ThinkingLevel? Effort { get; init; }
    public AnthropicThinkingDisplay? ThinkingDisplay { get; init; }
    public bool? InterleavedThinking { get; init; }
    public JsonNode? ToolChoice { get; init; }
}
```

```csharp
public sealed record GoogleThinkingOptions
{
    public bool Enabled { get; init; }
    public int? BudgetTokens { get; init; }
    public ThinkingLevel? Level { get; init; }
}

public sealed record GoogleOptions : StreamOptions
{
    public JsonNode? ToolChoice { get; init; }
    public GoogleThinkingOptions? Thinking { get; init; }
}
```

- [ ] **Step 5: 实现 FileCredentialStore**

文件 JSON 形状（固定）：

```json
{
  "version": 1,
  "credentials": {
    "anthropic": { "type": "api_key", "key": "sk-...", "env": { "REGION": "us" } },
    "openrouter": { "type": "oauth", "refresh": "...", "access": "...", "expires": 1760000000000 }
  }
}
```

实现要求：
- 构造 `FileCredentialStore(string path)`：展开 `~`；确保目录存在；启动时读取；非法 JSON/结构 → `ModelsError(ModelsErrorCode.Auth, ...)`。
- `ReadAsync` 不存在返回 null；`ListAsync` 返回 `CredentialInfo`。
- `ModifyAsync` 复用 S3 InMemory 语义：per-provider `SemaphoreSlim`；`null`=不变；回调在锁外 await；成功后原子写文件。
- 原子写：写 `path + ".tmp"`，flush，`File.Replace`（目标存在）或 `File.Move`，最后清理 temp；失败清理 temp 并抛 storage error。
- `DeleteAsync` 删除后原子写。
- 不记录 secret；不把明文写日志。
- 线程安全：字典读写 `lock`；per-provider 串行用 SemaphoreSlim。

- [ ] **Step 6: 门禁 + commit**

```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo --filter "FullyQualifiedName~S5OptionsTests|FullyQualifiedName~FileCredentialStoreTests"
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo
dotnet build PiUnityAgent.slnx -c Release --nologo
git add src/PiUnityAgent.Ai/Api/AnthropicOptions.cs src/PiUnityAgent.Ai/Api/GoogleOptions.cs src/PiUnityAgent.Ai/Providers/Auth/FileCredentialStore.cs src/PiUnityAgent.Ai/Providers/ModelData tests/PiUnityAgent.Ai.Tests/Api/S5OptionsTests.cs tests/PiUnityAgent.Ai.Tests/Providers/Auth/FileCredentialStoreTests.cs
git commit -m "feat(ai): S5 options, model data, file credential store"
```

---

## Task 2: Anthropic 请求/消息/工具转换

**Files:**
- Create `src/PiUnityAgent.Ai/Api/AnthropicMessages.cs`
- Test `tests/PiUnityAgent.Ai.Tests/Api/AnthropicMessagesTests.cs`

**Interfaces:**
- Produces `AnthropicMessages.Streams() : ProviderStreams`; `Stream(Model, TranscriptContext, AnthropicOptions?)`; `StreamSimple(...)`.
- Consumes `AnthropicOptions`, S4 SSE/retry/Fetch.

**Source port map（逐函数照 pi，不发明语义）：**
- `pi/packages/ai/src/api/anthropic-messages.ts`：`createClient` 1008–1108；`buildParams` 1153–1328；`convertMessages` 1348–1556；`convertTools` 1619–1659；`mapStopReason` 1660–1685；`normalizeToolCallId` 1330–1333；`getAnthropicCompat` 225–240。
- pi.net `src/Pi.Ai/Api/AnthropicMessages.cs` 作为 C# 表达参考。

- [ ] **Step 1: 先写请求形状测试**

测试至少断言：
- URL `baseUrl.TrimEnd('/') + "/v1/messages"`；
- headers `x-api-key`/`anthropic-version`/`anthropic-beta`；OAuth token 走 `Authorization: Bearer`；
- body `model`/`max_tokens`/`messages`/`system`/`tools`/`thinking`；
- system/user/assistant/toolResult 转换；
- thinking 回放 signature；
- tool_use id 归一化；
- `max_tokens` 与 thinking budget 关系。

```csharp
[Fact]
public async Task Request_Uses_Anthropic_Headers_And_Tool_Blocks()
{
    var model = ProviderTestSupport.CreateModel("claude-test", "anthropic") with { Api = "anthropic-messages" };
    var fetch = new MockFetch();
    fetch.EnqueueText(SseFixtures.AnthropicDone, headers: new Dictionary<string, string?>
    {
        ["content-type"] = "text/event-stream",
    });
    var context = ContextWithSystemToolAndUser();
    _ = await ProviderTestSupport.CollectAsync(AnthropicMessages.Stream(
        model, ProviderTestSupport.Normalize(context),
        new AnthropicOptions { MaxTokens = 64, ThinkingEnabled = true, ThinkingBudgetTokens = 1024 }));
    var request = fetch.Requests[0];
    Assert.EndsWith("/v1/messages", request.Url);
    Assert.True(request.Headers.ContainsKey("x-api-key"));
    var body = JsonNode.Parse(request.Body.ToArray())!;
    Assert.Equal("claude-test", body["model"]!.GetValue<string>());
    Assert.Equal(64, body["max_tokens"]!.GetValue<int>());
    Assert.NotNull(body["tools"]);
    Assert.NotNull(body["thinking"]);
}
```

- [ ] **Step 2: 实现转换函数**

按 source port map 逐函数落地。`convertMessages` 必须包含：
- system 折叠到 `system`；
- 文本+图片内容块；
- assistant thinking/tool_use；
- toolResult → tool_result block；
- 工具 ID 归一化（Anthropic 要求的 pattern/长度）；
- `cache_control`（D6）只在 compat 允许时。
`convertTools` 必须保留 strict/grammar 分支；`buildParams` 必须按 compat 选择 beta/thinking/cache。

- [ ] **Step 3: 跑请求测试 + commit**

```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo --filter FullyQualifiedName~AnthropicMessagesTests
git add src/PiUnityAgent.Ai/Api/AnthropicMessages.cs tests/PiUnityAgent.Ai.Tests/Api/AnthropicMessagesTests.cs
git commit -m "feat(ai): anthropic request and message conversion"
```

---

## Task 3: Anthropic SSE 状态机 + 终态

**Files:** 同一适配器文件；补测试。

**Source port map：**
- `iterateSseMessages`/`iterateAnthropicEvents` 477–576；
- `stream` 577–933；`streamSimple` 954–1003。

- [ ] **Step 1: SSE 测试**

至少：
- `Text_Stream`：message_start → content_block_start(text) → text_delta×2 → content_block_stop → message_delta(stop) → done；
- `Thinking_And_Signature`：thinking_delta + signature_delta；
- `Tool_Use_Input_Json_Delta`：tool_use + input_json_delta → ToolCallEnd 完整参数；
- `Usage_Cache_Read_Write_And_One_Hour`；
- `Stop_Reason_Map` / `Missing_Stop_Is_Error`；
- `Http_Error_Is_Error_Terminal`；
- `Cancel_Is_Aborted_Keeps_Partial`；
- `Missing_Fetch_Is_Terminal_Error`；
- `429_Retried_Only_Before_Stream`；
- `Hooks_And_Identity_Headers`。

- [ ] **Step 2: 实现状态机**

必须：
- 用 internal `SseEventReader`；命名事件按 `event:` 字段分派；
- `content_block.index` → 统一 `contentIndex`；
- thinking `signature_delta` 回填；
- tool `input_json_delta` 用 `ToolCallArguments`；
- `message_start` 的 usage、`message_delta` 的 finish/usage；
- 结束补 end 事件，唯一终态；
- catch 所有异常 → Error；取消 → Aborted；流开始后不重试；
- `OnPayload`/`OnResponse`/`OnProviderStreamEvent`/`StreamSimple` hook 透传；
- `TimeoutMs` → FetchOptions 三超时。

- [ ] **Step 3: 聚焦 + 全量 + Release + commit**

```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo --filter FullyQualifiedName~AnthropicMessagesTests
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo
dotnet build PiUnityAgent.slnx -c Release --nologo
git add src/PiUnityAgent.Ai/Api/AnthropicMessages.cs tests/PiUnityAgent.Ai.Tests/Api/AnthropicMessagesTests.cs
git commit -m "feat(ai): anthropic SSE state machine"
```

---

## Task 4: Google Generative AI 适配器

**Files:** `src/PiUnityAgent.Ai/Api/GoogleGenerativeAi.cs`；tests。

**Source port map：** pi `google-generative-ai.ts` `stream` 59–298、`streamSimple` 299–341、`createClient` 342–363、`buildParams` 364–425、`getGoogleBudget` 426–466；pi.net `Api/GoogleGenerativeAi.cs`。

- [ ] **Step 1: 测试** text/thought parts 切换、functionCall、usage、finishReason、HTTP error、cancel、missing fetch、retry、hooks。
- [ ] **Step 2: 实现** `contents`/`systemInstruction`/`generationConfig`/`tools`/`thinkingConfig`；用 Fetch，不照抄 pi 的 custom-fetch 拒绝；错误终态。
- [ ] **Step 3: gates + commit** `feat(ai): google-generative-ai adapter`.

---

## Task 5: 7 家 profile + ModelCatalog

**Files:** `AnthropicProvider.cs`、`MinimaxProvider.cs`、`GoogleProvider.cs`、`FireworksProvider.cs`、`VercelAiGatewayProvider.cs`、`OpenRouterProvider.cs`；tests `S5ProviderProfileTests.cs`；mapping doc。

- [ ] **Step 1: profile 工厂**
- Anthropic: `https://api.anthropic.com`, `ANTHROPIC_API_KEY`, Anthropic API + optional OAuth; `Create(FetchFunction? oauthFetch = null)`.
- MiniMax: `https://api.minimax.io/anthropic`, `MINIMAX_API_KEY`, Anthropic.
- MiniMax CN: `https://api.minimaxi.com/anthropic`, `MINIMAX_CN_API_KEY`.
- Google: `https://generativelanguage.googleapis.com/v1beta`, `GEMINI_API_KEY`, Google.
- Fireworks: `https://api.fireworks.ai/inference`, `FIREWORKS_API_KEY`, ApiByApi `anthropic-messages` + `openai-completions`.
- Vercel: `https://ai-gateway.vercel.sh`, `AI_GATEWAY_API_KEY`, Anthropic.
- OpenRouter: `https://openrouter.ai/api/v1`, `OPENROUTER_API_KEY` + optional OAuth, ApiByApi `anthropic-messages` + `openai-completions`.
- [ ] **Step 2: model data** 7 JSON via ModelCatalog; tests each profile models non-empty; Fireworks/OpenRouter dual api dispatch.
- [ ] **Step 3: gates + commit** `feat(ai): S5 provider profiles and catalogs`.

---

## Task 6: D1 OAuth + Models login/logout + D7 wiring

**Files:** `Providers/Auth/OAuth/{AnthropicOAuth,OpenRouterOAuth,OAuthHttp,Pkce,OAuthInput}.cs`; modify `Models.Auth.cs`; profile factories optional fetch; tests `OAuthTests.cs`, `S5AuthIntegrationTests.cs`.

**Interfaces:**
- `AnthropicOAuth(FetchFunction? fetch = null)` implements OAuthAuth.
- `OpenRouterOAuth(FetchFunction? fetch = null)` implements OAuthAuth.
- `Models.LoginAsync(providerId, AuthType, AuthInteraction, LoginOptions?)`.
- `FileCredentialStore` from Task 1.

- [ ] **Step 1: OAuth tests**
- `Pkce.Generate` deterministic shape;
- `OAuthHttp` POST token exchange via MockFetch; status/body errors;
- Anthropic copy-code/manual prompt → credential; refresh via MockFetch; ToAuth;
- OpenRouter manual code → API key; refresh no-op; ToAuth;
- `Models.LoginAsync` stores credential; `LogoutAsync` deletes; refresh writes back; errors ModelsError.
- No network; no browser callback.

- [ ] **Step 2: implement**
  - Port pi.net `AnthropicOAuth.cs`/`OpenRouterOAuth.cs` **manual/copy-code branches only**; replace HttpClient with injected `FetchFunction`; browser loopback/CallbackServer 不实现（C 类偏离，spec §10 登记）；copy-code/manual 已满足 D1 登录验收。
  - `OAuthHttp` maps `FetchRequest`/`FetchOptions`; token request JSON; parse errors.
  - `Pkce` generate verifier/challenge (SHA256 base64url).
  - Profile factories take optional `FetchFunction`; `lazyOAuth` only if fetch supplied; AuthHelpers.LazyOAuth.
  - Models.LoginAsync: choose ApiKey/OAuth by type; prompt/credential; store Modify; OAuth refresh/ToAuth; Logout delete (OAuth refresh D1 no browser).
  - FileCredentialStore injection via `CreateModelsOptions.Credentials`.
- [ ] **Step 3: gates + commit** `feat(ai): implement D1 OAuth and login/logout`.

---

## Task 7: D6/D10 + docs + S5 audit

- [ ] D6 Anthropic cache_control/usage tests; D10 OAuth availability tests.
- [ ] Ledger D1/D6/D7/D10 statuses/commits/anchors; spec §10 update; mapping/docs; S5 gate.
- [ ] Final Release/Ai/Hosts/gates; new independent audit `docs/audits/2026-11-08-S5-deviation-audit.md`; conclusion non-FAIL.
- [ ] No push/merge until user approval.

---

## Self-Review

- [ ] Spec §3 profiles covered; Fireworks/OpenRouter dual api.
- [ ] D1/D7/D6/D10 tasks present.
- [ ] Ai zero transport/HttpClient.
- [ ] OAuth fetch seam registered B/D.
- [ ] MockFetch only; no network.
- [ ] No `git add -A`.

<!-- END PLAN -->
</content>
