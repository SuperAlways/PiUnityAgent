# S5 Anthropic + Google 协议适配 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 `anthropic-messages` + `google-generative-ai` 两协议、7 个常用 profile，以及 D1 Anthropic/OpenRouter OAuth、D7 文件凭据 store、D6 Anthropic cache、D10 可用性，最后独立审计 PASS。

**Architecture:** 复用 S4 的 Fetch/SSE/retry/auth/identity/ModelCatalog 基础设施；新增 `Api/AnthropicMessages.cs` 与 `Api/GoogleGenerativeAi.cs`，profile 只做 baseUrl/auth/models/api 装配；OAuth 的 token exchange 通过 host 注入的可选 `FetchFunction`，不把 HttpClient/UnityLlmTransport 引入 Ai。

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
- Dn 阶段收尾必须过 `python scripts/check-deferred.py --root . --current-stage S5 --gate exit`。

---

## Task 0: 文档提交、worktree 与基线

**Files:** 无代码；提交 S5 spec/plan/docs，创建 worktree。

- [ ] **Step 1: 提交 S5 spec/plan/docs**

```bash
cd D:/shuoshu/taiwuask/PiUnityAgent
git add docs/superpowers/specs/2026-11-08-S5-Anthropic与Google协议适配-design.md \
  docs/superpowers/plans/2026-11-08-S5-Anthropic与Google协议适配.md \
  docs/05-教学阶段路线.md docs/README.md
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
- Modify `src/PiUnityAgent.Ai/PiUnityAgent.Ai.csproj`（EmbeddedResource 已含 `Providers/ModelData/*.json`，只需确认）
- Test `tests/PiUnityAgent.Ai.Tests/Api/S5OptionsTests.cs`
- Test `tests/PiUnityAgent.Ai.Tests/Providers/Auth/FileCredentialStoreTests.cs`

**Interfaces:** `AnthropicOptions`/`GoogleOptions`；`FileCredentialStore(string path)` implements `CredentialStore`；model data embedded.

- [ ] **Step 1: 模型数据复制**

从 `D:/shuoshu/taiwuask/pi-study/pi.net/src/Pi.Ai/ModelData/` 复制：
`anthropic.json`、`minimax.json`、`minimax-cn.json`、`google.json`、`fireworks.json`、`vercel-ai-gateway.json`、`openrouter.json` 到 `src/PiUnityAgent.Ai/Providers/ModelData/`。保持原字节；`ModelCatalog` 已验证过滤 chat。

- [ ] **Step 2: options 测试（失败）**

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

- [ ] **Step 3: FileCredentialStore 测试（失败）**

- 临时目录 path；读写 ApiKey/OAuth；`ModifyAsync` null=不变、per-provider 串行；重新构造 store 后仍能读（重启持久化）；文件不存在返回 null；非法 JSON 抛 storage error；写入原子（临时文件 rename）。测试清 temp dir。

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

- JSON 文件每 provider 一条，结构 `{ "version":1, "credentials": { "provider": {"type":"api_key","key":"...","env":{...}} | {"type":"oauth","refresh":"...","access":"...","expires":...} } }`。
- 构造时加载，目录不存在创建；原子写：写 `path.tmp` 后 `File.Replace/Move`；per-provider SemaphoreSlim；读/列/改/删。
- 文件损坏抛 `ModelsError(Auth, ...)`；不记录 secret 到日志。
- 路径 `~` 展开；测试用 temp。

- [ ] **Step 6: 门禁 + commit**

```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo --filter "FullyQualifiedName~S5OptionsTests|FullyQualifiedName~FileCredentialStoreTests"
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo
git add src/PiUnityAgent.Ai/Api/AnthropicOptions.cs src/PiUnityAgent.Ai/Api/GoogleOptions.cs src/PiUnityAgent.Ai/Providers/Auth/FileCredentialStore.cs src/PiUnityAgent.Ai/Providers/ModelData tests/PiUnityAgent.Ai.Tests/Api/S5OptionsTests.cs tests/PiUnityAgent.Ai.Tests/Providers/Auth/FileCredentialStoreTests.cs
git commit -m "feat(ai): S5 options, model data, file credential store"
```

---

## Task 2: Anthropic Messages 协议适配器

**Files:** `src/PiUnityAgent.Ai/Api/AnthropicMessages.cs`；`tests/PiUnityAgent.Ai.Tests/Api/AnthropicMessagesTests.cs`

**Source port map：** pi `anthropic-messages.ts`：
- `stream` 577–933；`streamSimple` 954–1003；`createClient` 1008–1108；`buildParams` 1153–1328；`convertMessages` 1348–1556；`convertTools` 1619–1659；`mapStopReason` 1660–1685；SSE `iterateSseMessages`/`iterateAnthropicEvents` 477–576。
- pi.net `Api/AnthropicMessages.cs` C# 参考。

**Public:** `AnthropicMessages.Streams()/Stream/StreamSimple`; `AnthropicOptions`.

- [ ] **Step 1: MockFetch tests**
  - text stream (message_start→content_block_start→text_delta→content_block_stop→message_delta→done)
  - thinking + signature
  - tool_use + input_json_delta
  - usage/cacheRead/cacheWrite (含 1h)
  - stop_reason mapping / missing stop
  - HTTP error terminal / cancel → Aborted / missing fetch terminal
  - retry before stream only (429 with MaxRetries=1)
  - OnPayload/OnResponse/OnProviderStreamEvent + StreamSimple hooks
  - headers: x-api-key / anthropic-version / beta / OAuth Bearer (if ApiKey looks OAuth) / identity
- [ ] **Step 2: implement adapter** using Fetch/SSE/retry; errors terminal; timeout mapping; cache_control D6.
- [ ] **Step 3: focused/full/Release + commit** message `feat(ai): add anthropic-messages protocol adapter`.

---

## Task 3: Google Generative AI 协议适配器

**Files:** `src/PiUnityAgent.Ai/Api/GoogleGenerativeAi.cs`；`tests/PiUnityAgent.Ai.Tests/Api/GoogleGenerativeAiTests.cs`

**Source port map：** pi `google-generative-ai.ts`（stream 59–298; streamSimple 299–341; createClient 342–363; buildParams 364–425; getGoogleBudget 426–466）；pi.net `Api/GoogleGenerativeAi.cs`.

- [ ] **Step 1: tests** text/thought switch, functionCall, usage, finishReason, HTTP error, cancel, missing fetch, retry-only-before, hooks.
- [ ] **Step 2: implement** contents/systemInstruction/tools/thinkingConfig; use Fetch (do not copy pi's custom-fetch rejection); errors terminal.
- [ ] **Step 3: gates + commit** `feat(ai): add google-generative-ai protocol adapter`.

---

## Task 4: 7 家 profile + catalog

**Files:** providers `AnthropicProvider.cs`, `MinimaxProvider.cs`, `GoogleProvider.cs`, `FireworksProvider.cs`, `VercelAiGatewayProvider.cs`, `OpenRouterProvider.cs`; tests `S5ProviderProfileTests.cs`; mapping doc.

- [ ] Anthropic id/baseUrl/key+OAuth placeholder; MiniMax/Cn; Google; Fireworks (ApiByApi anthropic+completions); Vercel; OpenRouter (ApiByApi both + identity headers).
- [ ] ModelCatalog reads 7 JSON; tests id/baseUrl/models; Fireworks/OpenRouter ApiByApi dispatch by model.Api.
- [ ] Gates + commit `feat(ai): add S5 provider profiles and catalogs`.

---

## Task 5: D1 OAuth + Models login/logout + D7 wiring

**Files:** `Providers/Auth/OAuth/{AnthropicOAuth,OpenRouterOAuth,OAuthHttp,Pkce,OAuthInput}.cs`; modify `Models.Auth.cs` LoginAsync; profile factories optional `FetchFunction? oauthFetch`; tests `OAuthTests.cs`, `S5AuthIntegrationTests.cs`.

- [ ] **AnthropicOAuth**：copy-code/manual PKCE login; token exchange via injected Fetch; Refresh; ToAuth; no browser callback (C).
- [ ] **OpenRouterOAuth**：manual code PKCE; exchange for API key; Refresh no-op; ToAuth key.
- [ ] Profile factories `Create(FetchFunction? oauthFetch = null)`; default null → OAuth login terminal/ModelsError? API key path unaffected.
- [ ] `Models.LoginAsync` selects ApiKey/OAuth; store credential; OAuth refresh; Logout deletes; errors ModelsError.
- [ ] Tests MockFetch token exchange; store round-trip; login/logout; refresh; no real network.
- [ ] Gates + commit `feat(ai): implement D1 OAuth and login/logout`.

---

## Task 6: D6/D10 + docs + S5 audit

- [ ] D6 Anthropic cache_control/usage tests; D10 OAuth/API key availability tests.
- [ ] Ledger D1/D6/D7/D10 statuses/commits/anchors; spec §10 update; mapping/docs; S5 gate.
- [ ] Final Release/Ai/Hosts/gates; new independent audit `docs/audits/2026-11-08-S5-deviation-audit.md`; conclusion non-FAIL.
- [ ] No push/merge until user approval.

---

## Self-Review

- [ ] Spec §3 profiles all covered; OpenRouter/Fireworks双协议.
- [ ] D1/D7/D6/D10 tasks present.
- [ ] Ai zero transport/HttpClient.
- [ ] OAuth fetch seam registered B/D.
- [ ] MockFetch only; no network.
- [ ] No `git add -A`.

<!-- END PLAN -->
</content>
