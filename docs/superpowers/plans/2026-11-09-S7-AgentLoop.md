# S7 Agent Loop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 落 S7 低层 agent 循环（事件/批次/hooks/调度）+ 承接 D16（流式参数 partial 快照 + repairJson），以 Faux 驱动全部测试。

**Architecture:** Ai 先补 D16（StreamingJson/JsonParse internal 移植 + 三适配器接线），Core 再落 `Agent/` 形状、工具批次、`AgentLoop` 双层循环与调度回调；实现参考 pi.net `Pi.Agent`（2d6757b）逐文件移植并按我们的类型/命名适配。

**Tech Stack:** netstandard2.1、System.Text.Json 8.0.5、xUnit（net10.0）、Faux provider、裸 `dotnet`；参考源 `D:/shuoshu/taiwuask/pi-study/{pi,pi.net}`。

## Global Constraints

- **工作位置**：`.worktrees/s7-agent-loop`（分支 `feature/s7-agent-loop`，从当前 main HEAD 开出）。
- **禁止推送/合并**：全部任务 + 独立审计通过后，由用户明确批准才可 merge/push。
- **依赖约束**：Core 只引用 Ai；Ai 不反向依赖 Core；无 UnityEngine/UnityLlmTransport/HttpClient/GameData；Ai/Core 不新增 NuGet。
- **公共面**：Ai D16 工具仅 `internal`（`StreamingJsonParser`/`ToolCallArgumentsStream`/`JsonParse`）；Core 新增 §3 形状 + `AgentLoop`/`ToolExecution.RunToolCallAsync`/`DefaultStreamFn`；批次执行保持 internal（Core 加 `InternalsVisibleTo("PiUnityAgent.Core.Tests")` 供测试）。
- **文案/语义逐字**：截断工具错误文案、continue 守卫文案、hook 默认文案、默认流函数缺失文案必须与 pi 一致；D16 partial 快照规则以 pi `streaming-json.ts` 为准。
- **命令**（worktree 根目录）：
  - `dotnet build PiUnityAgent.slnx -c Release --nologo`（0/0）
  - `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo -c Release`
  - `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --nologo -c Release`
  - `python scripts/check-deferred.py --root . --current-stage S7 --gate exit`
- **git**：显式 `git add <paths>`，严禁 `git add -A`；每任务一提交，中文信息 + `Co-Authored-By: Claude <noreply@anthropic.com>`。
- **TDD**：先测试（或先复制测试确认 RED），再实现、再绿；不得削弱/删除既有测试（D16 现势钉替换除外，替换后必须在报告写明）。

---

### Task 0: 开工准备（控制器执行）

**Files:** 无代码改动

- [ ] **Step 1: 开 worktree**

```bash
cd D:/shuoshu/taiwuask/PiUnityAgent
git status --short
git worktree add .worktrees/s7-agent-loop -b feature/s7-agent-loop
cd .worktrees/s7-agent-loop
```

Expected: 分支 `feature/s7-agent-loop`，HEAD=当前 main（含 S7 spec/plan 提交）。

- [ ] **Step 2: 基线门禁**

```bash
dotnet build PiUnityAgent.slnx -c Release --nologo
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo -c Release
dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --nologo -c Release
python scripts/check-deferred.py --root . --current-stage S6 --gate exit
```

Expected: 0/0；Ai 1050 全绿；Core 7 全绿；gate OK。

---

### Task 1: D16 引擎移植（Ai internal：StreamingJson + JsonParse）

**Files:**
- Create（移植源=`D:/shuoshu/taiwuask/pi-study/pi.net/src/Pi.Ai/Utils/`）:
  - `src/PiUnityAgent.Ai/Utils/StreamingJson.cs`（源 735 行）
  - `src/PiUnityAgent.Ai/Utils/JsonParse.cs`（源 249 行）
- Create（测试源=pi.net 同名/相关测试）:
  - `tests/PiUnityAgent.Ai.Tests/Utils/StreamingJsonTests.cs`（源 pi.net `tests/Pi.Ai.Tests/StreamingJsonTests.cs` 387 行）
  - `tests/PiUnityAgent.Ai.Tests/Utils/JsonParseTests.cs`（从 pi.net `tests/Pi.Ai.Tests/Api/TranscriptAndUtilsTests.cs:199-248` 抽出 ParseToolCallArguments 五例）

**Interfaces:**
- Consumes: 无
- Produces（全部 internal）：
  - `StreamingJsonParser`：`Append(string)`、`Snapshot() : object?`
  - `ToolCallArgumentsStream`：`Text`、`Append(string) : IReadOnlyDictionary<string,object?>`、`Replace(string) : (string? Delta, IReadOnlyDictionary<string,object?> Arguments)`、`Finish() : (JsonObject Arguments, string? ArgumentsError)`
  - `JsonParse.RepairJson(string)`、`JsonParse.ParseJsonWithRepair<T>(string)`、`JsonParse.ParseToolCallArguments(string?) : (JsonObject, string?)`

- [ ] **Step 1: 先落测试（RED）**

```bash
mkdir -p tests/PiUnityAgent.Ai.Tests/Utils src/PiUnityAgent.Ai/Utils
cp D:/shuoshu/taiwuask/pi-study/pi.net/tests/Pi.Ai.Tests/StreamingJsonTests.cs tests/PiUnityAgent.Ai.Tests/Utils/StreamingJsonTests.cs
```

把复制的测试改为：

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Utils;
using Xunit;

namespace PiUnityAgent.Ai.Tests.Utils;
```

测试体保持不变（内部类型经 `InternalsVisibleTo("PiUnityAgent.Ai.Tests")` 可见）。

新建 `JsonParseTests.cs`：

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Utils;

namespace PiUnityAgent.Ai.Tests.Utils;

public class JsonParseTests
{
    [Fact]
    public void ParseToolCallArguments_Returns_Object_And_No_Error()
    {
        var (arguments, error) = JsonParse.ParseToolCallArguments("{\"path\":\"/x\",\"n\":2}");
        Assert.Equal(2, arguments["n"]!.GetValue<int>());
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseToolCallArguments_Empty_Input_Is_Empty_Object(string? input)
    {
        var (arguments, error) = JsonParse.ParseToolCallArguments(input);
        Assert.Empty(arguments);
        Assert.Null(error);
    }

    [Fact]
    public void ParseToolCallArguments_Repairs_Recoverable_Text()
    {
        var (arguments, error) = JsonParse.ParseToolCallArguments("{\"text\":\"line1\nline2\"}");
        Assert.Equal("line1\nline2", arguments["text"]!.GetValue<string>());
        Assert.Null(error);
    }

    [Fact]
    public void ParseToolCallArguments_Unrecoverable_Keeps_Received_Arguments()
    {
        var raw = "{\"path\":\"/x\",\"oops\"";
        var (arguments, error) = JsonParse.ParseToolCallArguments(raw);
        Assert.Empty(arguments);
        Assert.NotNull(error);
        Assert.Contains("Received arguments:", error, StringComparison.Ordinal);
        Assert.Contains(raw, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseToolCallArguments_Rejects_NonObject_Json()
    {
        var (arguments, error) = JsonParse.ParseToolCallArguments("[1,2,3]");
        Assert.Empty(arguments);
        Assert.NotNull(error);
        Assert.Contains("Received arguments:", error, StringComparison.Ordinal);
    }
}
```

```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo -c Release --filter "FullyQualifiedName~StreamingJsonTests|FullyQualifiedName~JsonParseTests"
```

Expected: **编译失败**（`PiUnityAgent.Ai.Utils` 下无类型）——RED。

- [ ] **Step 2: 移植实现**

```bash
cp D:/shuoshu/taiwuask/pi-study/pi.net/src/Pi.Ai/Utils/StreamingJson.cs src/PiUnityAgent.Ai/Utils/
cp D:/shuoshu/taiwuask/pi-study/pi.net/src/Pi.Ai/Utils/JsonParse.cs src/PiUnityAgent.Ai/Utils/
```

只做这些变换（其余逐字保留）：

1. `namespace Pi.Ai.Utils;` → `namespace PiUnityAgent.Ai.Utils;`（两文件）。
2. `StreamingJson.cs` 的 public 类型改 internal：`StreamingJsonParser`、`ToolCallArgumentsStream`（及其嵌套类型保持原可见性）；`JsonParse` 若为 public static class 改 internal static。
3. 删除对 `Pi.Ai.Types` 的 using（若存在）。
4. 不改任何行为/注释语义；netstandard2.1 API 不适配时按最小替代并报告。

- [ ] **Step 3: 跑测试确认通过**

```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo -c Release --filter "FullyQualifiedName~StreamingJsonTests|FullyQualifiedName~JsonParseTests"
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo -c Release
```

Expected: 新增测试全绿；全量 Ai 全绿。

- [ ] **Step 4: 提交**

```bash
git add src/PiUnityAgent.Ai/Utils/StreamingJson.cs src/PiUnityAgent.Ai/Utils/JsonParse.cs tests/PiUnityAgent.Ai.Tests/Utils/StreamingJsonTests.cs tests/PiUnityAgent.Ai.Tests/Utils/JsonParseTests.cs
git commit -m "feat(ai): 移植流式 JSON 解析与 repair（D16 引擎，internal）

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: D16 接线（ToolCallArguments 升级 + 三适配器 partial 快照）

**Files:**
- Modify: `src/PiUnityAgent.Ai/Api/ToolCallArguments.cs`（整体重写为 `ToolCallArgumentsStream` 的包装）
- Modify: `src/PiUnityAgent.Ai/Api/AnthropicMessages.cs`（input_json_delta :668-675、content_block_stop :701-720）
- Modify: `src/PiUnityAgent.Ai/Api/OpenAiCompletions.cs`（delta :596-607、FinishBlock :443-456）
- Modify: `src/PiUnityAgent.Ai/Api/OpenAiResponses.cs`（delta :1596-1622、done :1687-1710、`ReplaceArguments` :1818-1846）
- Test: `tests/PiUnityAgent.Ai.Tests/Api/ToolCallArgumentsTests.cs`（现势钉替换）
- Test: `tests/PiUnityAgent.Ai.Tests/Api/{OpenAiCompletionsTests,OpenAiResponsesTests,AnthropicMessagesTests}.cs`（各 +1 partial 快照用例）

**Interfaces:**
- Consumes: Task 1 `ToolCallArgumentsStream`、`JsonParse`
- Produces: `ToolCallArguments`（internal，API）：
  - `Append(string delta) : JsonObject`（partial 参数）
  - `Replace(string text) : (string? Delta, JsonObject Arguments)`
  - `Finish() : (JsonObject Arguments, string? ArgumentsError)`
  - `Text : string`

- [ ] **Step 1: 改现势钉为失败测试**

把 `ToolCallArgumentsTests.ToolCallArguments_Does_Not_Expose_Partial_Json` 替换为：

```csharp
    [Fact]
    public void ToolCallArguments_Exposes_Growing_Partial_Json()
    {
        var arguments = new ToolCallArguments();
        var first = arguments.Append("{\"city\"");
        Assert.Equal("{}", first.ToJsonString());
        var second = arguments.Append(":\"SF\"}");
        Assert.Equal("{\"city\":\"SF\"}", second.ToJsonString());
    }
```

并新增 repair 用例：

```csharp
    [Fact]
    public void ToolCallArguments_Finish_Repairs_Recoverable_Json()
    {
        var arguments = new ToolCallArguments();
        arguments.Append("{\"text\":\"line1\nline2\"}");
        var (parsed, error) = arguments.Finish();
        Assert.Equal("line1\nline2", parsed["text"]!.GetValue<string>());
        Assert.Null(error);
    }

    [Fact]
    public void ToolCallArguments_Finish_Reports_Unrecoverable_Json()
    {
        var arguments = new ToolCallArguments();
        arguments.Append("{\"path\":\"/x\",\"oops\"");
        var (parsed, error) = arguments.Finish();
        Assert.Empty(parsed);
        Assert.Contains("Received arguments:", error!, StringComparison.Ordinal);
    }
```

在 `OpenAiCompletionsTests` 的 `Tool_Call_Arguments_Are_Accumulated` 之后新增：

```csharp
    [Fact]
    public async Task Tool_Call_Delta_Partial_Snapshot_Grows()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\","
                + "\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"\"}}]}}]}",
                "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":"
                + "{\"arguments\":\"{\\\"city\\\"\"}}]}}]}",
                "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":"
                + "{\"arguments\":\":\\\"SF\\\"}\"}}]}}]}",
                ChoiceChunk("{}", finishReason: "tool_calls")),
            headers: SseHeaders());

        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        var firstDelta = Assert.IsType<AssistantMessageEvent.ToolCallDelta>(events[3]);
        Assert.Equal("{}", Assert.IsType<ToolCall>(firstDelta.Partial.Content[^1]).Arguments.ToJsonString());
        var secondDelta = Assert.IsType<AssistantMessageEvent.ToolCallDelta>(events[4]);
        Assert.Equal("{\"city\":\"SF\"}",
            Assert.IsType<ToolCall>(secondDelta.Partial.Content[^1]).Arguments.ToJsonString());
    }
```

Anthropic/Responses 各新增同构用例（用各自现有 SSE helper；至少断言“mid-delta 的 partial 块 Arguments 已是部分 JSON”与“终值完整”）。

```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo -c Release --filter "FullyQualifiedName~ToolCallArgumentsTests"
```

Expected: 编译失败/断言失败（`ToolCallArguments.Append` 仍 void）——RED。

- [ ] **Step 2: 重写 ToolCallArguments**

```csharp
using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Api;

/// <summary>
/// 一条流式 tool call 的参数累积器（pi <c>ToolCallArgumentsStream</c>）：
/// delta 期间暴露可解析的部分 JSON 快照；终点用一次 repair 解析给出终值/argumentsError。
/// 原始文本不进入 partial 消息或 transcript。
/// </summary>
internal sealed class ToolCallArguments
{
    private readonly ToolCallArgumentsStream _stream = new();

    public string Text => _stream.Text;

    /// <summary>追加一段参数文本；返回 partial 参数（对象形态；非对象快照显示为 {}）。</summary>
    public JsonObject Append(string delta) => ToObject(_stream.Append(delta));

    /// <summary>接受 provider 的完整参数文本；返回缺失片段与 partial 参数。</summary>
    public (string? Delta, JsonObject Arguments) Replace(string text)
    {
        var (delta, arguments) = _stream.Replace(text);
        return (delta, ToObject(arguments));
    }

    /// <summary>终点：parseJsonWithRepair 语义，返回参数或 argumentsError。</summary>
    public (JsonObject Arguments, string? ArgumentsError) Finish() => _stream.Finish();

    private static JsonObject ToObject(IReadOnlyDictionary<string, object?> partial)
        => JsonSerializer.SerializeToNode(partial) as JsonObject ?? new JsonObject();
}
```

（`JsonSerializer.SerializeToNode` 需 `using System.Text.Json;`；若对象图含 Pi 类型不可序列化，改为递归 ToJsonNode；实现后以测试钉住。）

- [ ] **Step 3: 三适配器接线**

1. **Anthropic** `input_json_delta`：
```csharp
                            var jsonDelta = JsonStr(delta, "partial_json") ?? "";
                            slot.ToolCall.Arguments = slot.JsonArguments.Append(jsonDelta); // slot.ToolCall 为适配器内部可变块
                            stream.Push(new AssistantMessageEvent.ToolCallDelta(
                                slot.ContentIndex, jsonDelta, output.Snapshot(blocks)));
```
`content_block_stop` 去掉 try/catch：
```csharp
                        var block = slot.ToolCall;
                        var (arguments, argumentsError) = slot.JsonArguments.Finish();
                        block.Arguments = arguments;
                        block.ArgumentsError = argumentsError;
```
2. **OpenAI Completions** delta：`input.Arguments!.Append(argumentsDelta)` 返回 partial，赋给 `block.Arguments`；FinishBlock 用 `var (args, err)=input.Arguments!.Finish(); toolCall.Arguments=args; toolCall.ArgumentsError=err;`。
3. **OpenAI Responses**：`function_call_arguments.delta` append/replace 后把 partial 赋给 `slot.ToolCall.Arguments`；`ReplaceArguments` 保持 `Text` 接续逻辑但返回 partial；`done` 用 tuple 赋值。
- Google 无流式参数，不改。

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo -c Release --filter "FullyQualifiedName~ToolCallArgumentsTests|FullyQualifiedName~OpenAiCompletionsTests|FullyQualifiedName~OpenAiResponsesTests|FullyQualifiedName~AnthropicMessagesTests"
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo -c Release
```

Expected: 新增/替换用例绿；全量 Ai 绿（原有非法 JSON 用例的 argumentsError 断言保持——repair 仍失败时文案同形）。

- [ ] **Step 5: 提交**

```bash
git add src/PiUnityAgent.Ai/Api/ToolCallArguments.cs src/PiUnityAgent.Ai/Api/AnthropicMessages.cs src/PiUnityAgent.Ai/Api/OpenAiCompletions.cs src/PiUnityAgent.Ai/Api/OpenAiResponses.cs tests/PiUnityAgent.Ai.Tests/Api/ToolCallArgumentsTests.cs tests/PiUnityAgent.Ai.Tests/Api/OpenAiCompletionsTests.cs tests/PiUnityAgent.Ai.Tests/Api/OpenAiResponsesTests.cs tests/PiUnityAgent.Ai.Tests/Api/AnthropicMessagesTests.cs
git commit -m "feat(ai): D16 流式参数 partial 快照 + repair 接线（三适配器）

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: Core 循环形状（AgentContext/Config/Events/Hooks/Outcome + InternalsVisibleTo）

**Files:**
- Create: `src/PiUnityAgent.Core/Agent/AgentEvent.cs`
- Create: `src/PiUnityAgent.Core/Agent/AgentContext.cs`
- Create: `src/PiUnityAgent.Core/Agent/AgentLoopConfig.cs`
- Create: `src/PiUnityAgent.Core/Agent/AgentLoopTypes.cs`
- Create: `src/PiUnityAgent.Core/Agent/ToolCallHooks.cs`
- Create: `src/PiUnityAgent.Core/Agent/AgentToolCallOutcome.cs`
- Create: `src/PiUnityAgent.Core/Agent/AgentLoop.Identity.cs`（`DefaultAgentIdentity`；Task 5 以 partial 扩展）
- Create: `src/PiUnityAgent.Core/Properties/AssemblyInfo.cs`
- Test: `tests/PiUnityAgent.Core.Tests/Agent/AgentShapeTests.cs`

**Interfaces:**
- Consumes: S6 `AgentTool`/`AgentToolResult`/`ToolExecutionMode`/`AgentToolUpdateCallback`（`PiUnityAgent.Core.Tools`）；Ai `Message`/`AssistantMessage`/`ToolResultMessage`/`ToolCall`/`AssistantMessageEvent`/`Model`/`SimpleStreamOptions`/`ClientIdentity`/`ClientIdentityHelpers`/`Usage`/`ContentBlock`
- Produces: 全部 `PiUnityAgent.Core.Agent` 公共形状（见 spec §3.1–§3.4），供 Task 4/5 消费

- [ ] **Step 1: 写形状冒烟测试**

新建 `tests/PiUnityAgent.Core.Tests/Agent/AgentShapeTests.cs`：

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

public class AgentShapeTests
{
    private static AgentTool Echo()
        => new()
        {
            Name = "echo",
            Description = "Echo",
            Label = "Echo",
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = (_, args, _, _) => Task.FromResult(new AgentToolResult
            {
                Content = new ContentBlock[] { new TextContent { Text = args?.ToJsonString() ?? "{}" } },
            }),
        };

    [Fact]
    public void Event_Variants_Carry_The_Shape()
    {
        var events = new AgentEvent[]
        {
            new AgentEvent.AgentStart(),
            new AgentEvent.TurnStart(),
            new AgentEvent.ToolExecutionStart("call-1", "echo", new JsonObject()),
            new AgentEvent.AgentEnd(Array.Empty<Message>()),
        };
        Assert.Equal(4, events.Length);
        Assert.IsType<AgentEvent.AgentEnd>(events[^1]);
    }

    [Fact]
    public void Config_Defaults_Are_Parallel_And_Nullable()
    {
        var config = new AgentLoopConfig
        {
            Model = new Model { Id = "m", Name = "m", Api = "faux", Provider = "faux", BaseUrl = "http://localhost" },
            ConvertToLlm = messages => Task.FromResult<IReadOnlyList<Message>>(messages),
        };
        Assert.Equal(ToolExecutionMode.Parallel, config.ToolExecution);
        Assert.Null(config.StreamOptions);
        Assert.Null(config.BeforeToolCall);
        Assert.Null(config.AfterToolCall);
    }

    [Fact]
    public void Hook_And_Outcome_Records_RoundTrip()
    {
        var toolCall = new ToolCall { Id = "c1", Name = "echo" };
        var outcome = new AgentToolCallOutcome(
            toolCall, new AgentToolResult { IsError = true }, IsError: true) { DurationMs = 12 };
        Assert.Equal(12, outcome.DurationMs);
        Assert.True(outcome.IsError);

        var before = new BeforeToolCallResult(Block: true, Reason: "no", Terminate: true);
        Assert.True(before.Block);
        var after = new AfterToolCallResult(IsError: false, Terminate: true);
        Assert.True(after.Terminate);
    }

    [Fact]
    public void Default_Identity_Is_Pi_Attribution()
    {
        Assert.NotNull(AgentLoop.DefaultAgentIdentity.Attribution);
        Assert.Equal("pi", AgentLoop.DefaultAgentIdentity.Attribution!.Name);
    }

    [Fact]
    public void Context_Keeps_Mutable_Message_List()
    {
        var messages = new List<Message> { new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") } };
        var context = new AgentContext { Messages = messages, Tools = new[] { Echo() } };
        messages.Add(new UserMessage { Timestamp = 2, Content = MessageContent.From("again") });
        Assert.Equal(2, context.Messages.Count);
        Assert.Single(context.Tools!);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --nologo -c Release
```

Expected: 编译失败（`PiUnityAgent.Core.Agent` 不存在）。

- [ ] **Step 3: 实现形状文件**

按 spec §3 逐类型落地。文件内容如下（`AgentEvent` 10 变体、`AgentEventSink`；`AgentContext`；`AgentLoopConfig`；`AgentLoopTypes` 的 turn/decision/updates/prepare-request；`ToolCallHooks`；`AgentToolCallOutcome`）。

`AgentEvent.cs`：

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public abstract record AgentEvent
{
    private AgentEvent() { }

    public sealed record AgentStart : AgentEvent;
    public sealed record AgentEnd(IReadOnlyList<Message> Messages) : AgentEvent;
    public sealed record TurnStart : AgentEvent;
    public sealed record TurnEnd(AssistantMessage Message, IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent;
    public sealed record MessageStart(Message Message) : AgentEvent;
    public sealed record MessageUpdate(AssistantMessageEvent AssistantMessageEvent, AssistantMessage Message) : AgentEvent;
    public sealed record MessageEnd(Message Message) : AgentEvent;
    public sealed record ToolExecutionStart(string ToolCallId, string ToolName, JsonObject Args) : AgentEvent;
    public sealed record ToolExecutionUpdate(string ToolCallId, string ToolName, JsonObject Args, AgentToolResult PartialResult) : AgentEvent;
    public sealed record ToolExecutionEnd(
        string ToolCallId, string ToolName, AgentToolResult Result, bool IsError, long? DurationMs) : AgentEvent;
}

public delegate Task AgentEventSink(AgentEvent @event);
```

`AgentContext.cs`：

```csharp
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public sealed record AgentContext
{
    public required List<Message> Messages { get; init; }
    public IReadOnlyList<AgentTool>? Tools { get; init; }
}
```

`AgentLoopTypes.cs`：

```csharp
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

public enum AgentTurnDecision { Continue, End }

public sealed record AgentTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    AgentContext Context,
    IReadOnlyList<Message> NewMessages);

public sealed record PrepareRequestContext(AgentContext Context, Model Model, ModelThinkingLevel ThinkingLevel);

public sealed record AgentLoopTurnUpdate
{
    public AgentContext? Context { get; init; }
    public IReadOnlyList<Message>? Messages { get; init; }
    public Model? Model { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
}

public sealed record AgentRequestUpdate
{
    public AgentContext? Context { get; init; }
    public Model? Model { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
}

public delegate Task<AgentTurnDecision?> FinishTurn(AgentTurnContext turn, CancellationToken signal);
public delegate Task<AgentRequestUpdate?> PrepareRequest(PrepareRequestContext request, CancellationToken signal);
public delegate Task<AgentLoopTurnUpdate?> PrepareNextTurn(AgentTurnContext turn);
```

`AgentLoopConfig.cs`：

```csharp
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public sealed record AgentLoopConfig
{
    public required Model Model { get; init; }
    public required Func<IReadOnlyList<Message>, Task<IReadOnlyList<Message>>> ConvertToLlm { get; init; }
    public Func<IReadOnlyList<Message>, CancellationToken, Task<IReadOnlyList<Message>>>? TransformContext { get; init; }
    public Func<string, CancellationToken, Task<string?>>? GetApiKey { get; init; }
    public FinishTurn? FinishTurn { get; init; }
    public PrepareRequest? PrepareRequest { get; init; }
    public PrepareNextTurn? PrepareNextTurn { get; init; }
    public Func<Task<IReadOnlyList<Message>>>? GetSteeringMessages { get; init; }
    public Func<Task<IReadOnlyList<Message>>>? GetFollowUpMessages { get; init; }
    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;
    public BeforeToolCall? BeforeToolCall { get; init; }
    public AfterToolCall? AfterToolCall { get; init; }
    public SimpleStreamOptions? StreamOptions { get; init; }
    public ClientIdentity? Identity { get; init; }
}
```

`ToolCallHooks.cs`：

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public sealed record BeforeToolCallContext(
    AssistantMessage AssistantMessage, ToolCall ToolCall, JsonNode? Args, AgentContext Context);

public sealed record BeforeToolCallResult(bool? Block = null, string? Reason = null, bool? Terminate = null);

public sealed record AfterToolCallContext(
    AssistantMessage AssistantMessage, ToolCall ToolCall, JsonNode? Args,
    AgentToolResult Result, bool IsError, AgentContext Context);

public sealed record AfterToolCallResult(
    IReadOnlyList<ContentBlock>? Content = null, JsonNode? Details = null, JsonNode? StructuredContent = null,
    bool? IsError = null, Usage? Usage = null, bool? Terminate = null);

public delegate Task<BeforeToolCallResult?> BeforeToolCall(BeforeToolCallContext context, CancellationToken signal);
public delegate Task<AfterToolCallResult?> AfterToolCall(AfterToolCallContext context, CancellationToken signal);
```

`AgentToolCallOutcome.cs`：

```csharp
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public sealed record AgentToolCallOutcome(ToolCall ToolCall, AgentToolResult Result, bool IsError)
{
    public long? DurationMs { get; init; }
}
```

`AgentLoop.Identity.cs`：

```csharp
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

public static partial class AgentLoop
{
    /// <summary>请求身份缺省：UA 归属 pi。对应 pi DEFAULT_AGENT_IDENTITY。</summary>
    public static ClientIdentity DefaultAgentIdentity { get; } =
        new() { Attribution = ClientIdentityHelpers.PiAttribution };
}
```

`Properties/AssemblyInfo.cs`：

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PiUnityAgent.Core.Tests")]
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet build PiUnityAgent.slnx -c Release --nologo
dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --nologo -c Release
```

Expected: 0/0；Core 7 + 5 = 12 通过。

- [ ] **Step 5: 提交**

```bash
git add src/PiUnityAgent.Core/Agent src/PiUnityAgent.Core/Properties/AssemblyInfo.cs tests/PiUnityAgent.Core.Tests/Agent/AgentShapeTests.cs
git commit -m "feat(core): Agent 循环公共形状（事件/上下文/配置/钩子/outcome）

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: 工具执行管线 + 装载公告（ToolExecution / ToolLoadout / RunToolCallAsync）

**Files:**
- Create: `src/PiUnityAgent.Core/Agent/ToolExecution.cs`（参考移植 pi.net `src/Pi.Agent/ToolExecution.cs` 417 行）
- Create: `src/PiUnityAgent.Core/Agent/ToolLoadout.cs`（按 pi TS `agent-loop.ts:330-410` 实现；pi.net 版本为简化版，不采用其 shortcut）
- Test: `tests/PiUnityAgent.Core.Tests/Agent/ToolExecutionTests.cs`
- Test: `tests/PiUnityAgent.Core.Tests/Agent/ToolLoadoutTests.cs`

**Interfaces:**
- Consumes: Task 3 形状；S6 `AgentTool.Definition`、`ToolValidation.ValidateToolArguments`、`AgentToolResult.FromError`；Ai `ToolCall.Arguments/ArgumentsError`
- Produces:
  - `public static class ToolExecution { Task<AgentToolCallOutcome> RunToolCallAsync(ToolCall, AssistantMessage, AgentContext, AgentLoopConfig, CancellationToken, AgentToolUpdateCallback? = null) }`
  - internal `ToolExecution.ExecuteToolCalls(...) : Task<ExecutedToolCallBatch>`（供 Task 5 与 Core.Tests）
  - internal `ToolExecution.FailToolCallsFromTruncatedMessage(...)`
  - internal `ToolLoadout.DeclareToolChanges(AgentContext, IReadOnlyList<Message>, long timestamp) : IReadOnlyList<Message>`

- [ ] **Step 1: 写失败测试**

`ToolExecutionTests.cs` 核心用例：

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

public class ToolExecutionTests
{
    private static AgentContext Context(params AgentTool[] tools)
        => new() { Messages = new List<Message>(), Tools = tools };

    private static AgentLoopConfig Config() => new()
    {
        Model = new Model { Id = "m", Name = "m", Api = "faux", Provider = "faux", BaseUrl = "http://localhost" },
        ConvertToLlm = messages => Task.FromResult<IReadOnlyList<Message>>(messages),
    };

    private static AgentTool Echo(AgentToolResult? result = null)
        => new()
        {
            Name = "echo",
            Description = "Echo",
            Label = "Echo",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("value"),
            },
            Execute = (_, args, _, _) => Task.FromResult(result ?? new AgentToolResult
            {
                Content = new ContentBlock[] { new TextContent { Text = args!["value"]!.GetValue<string>() } },
                Details = new JsonObject(),
            }),
        };

    private static AssistantMessage Assistant(params ContentBlock[] content)
        => new()
        {
            Content = content, Api = "faux", Provider = "faux", Model = "m", Usage = new Usage(),
            StopReason = StopReason.ToolUse, Timestamp = 1,
        };

    private static ToolCall Call(string name = "echo", JsonObject? args = null, string? error = null)
        => new() { Id = "call-1", Name = name, Arguments = args ?? new JsonObject { ["value"] = "hi" }, ArgumentsError = error };

    [Fact]
    public async Task Unknown_Tool_Is_An_Error_Outcome()
    {
        var outcome = await ToolExecution.RunToolCallAsync(
            Call("nope"), Assistant(), Context(), Config(), CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("Tool nope not found", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task Validation_Error_Is_An_Error_Outcome()
    {
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(args: new JsonObject()), Assistant(), Context(Echo()), Config(), CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Contains("Validation failed for tool \"echo\"",
            Assert.IsType<TextContent>(outcome.Result.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Before_Hook_Block_Uses_Reason_And_Terminate()
    {
        var config = Config() with
        {
            BeforeToolCall = (_, _) => Task.FromResult<BeforeToolCallResult?>(
                new BeforeToolCallResult(Block: true, Reason: "denied", Terminate: true)),
        };
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), Assistant(), Context(Echo()), config, CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.True(outcome.Result.Terminate);
        Assert.Equal("denied", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task After_Hook_Replaces_Fields_And_Drops_Structured_Content_When_Content_Replaced()
    {
        var config = Config() with
        {
            AfterToolCall = (context, _) => Task.FromResult<AfterToolCallResult?>(
                new AfterToolCallResult(
                    Content: new ContentBlock[] { new TextContent { Text = "override" } },
                    IsError: true)),
        };
        var tool = Echo(new AgentToolResult
        {
            Content = new ContentBlock[] { new TextContent { Text = "original" } },
            StructuredContent = new JsonObject { ["x"] = 1 },
        });
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), Assistant(), Context(tool), config, CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("override", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
        Assert.Null(outcome.Result.StructuredContent);
    }

    [Fact]
    public async Task Execute_Throw_Becomes_Error_With_Duration()
    {
        var tool = Echo() with
        {
            Execute = (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), Assistant(), Context(tool), Config(), CancellationToken.None);
        Assert.True(outcome.IsError);
        Assert.Equal("boom", Assert.IsType<TextContent>(outcome.Result.Content[0]).Text);
        Assert.NotNull(outcome.DurationMs);
    }

    [Fact]
    public async Task Update_After_Settle_Is_Ignored()
    {
        var updates = new List<string>();
        var tool = Echo() with
        {
            Execute = async (_, _, _, var update) =>
            {
                if (update is not null) await update(new AgentToolResult());
                return new AgentToolResult { Content = new ContentBlock[] { new TextContent { Text = "done" } } };
            },
        };
        // 直接调用 runToolCall 时 onUpdate 由调用方提供（不产生事件）；此处只断言正常返回。
        var outcome = await ToolExecution.RunToolCallAsync(
            Call(), Assistant(), Context(tool), Config(), CancellationToken.None,
            partial => { updates.Add("update"); return Task.CompletedTask; });
        Assert.True(outcome.IsError == false);
        Assert.Single(updates);
    }
}
```

`ToolLoadoutTests.cs` 核心用例：

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

public class ToolLoadoutTests
{
    private static AgentTool Tool(string name, string description = "d")
        => new()
        {
            Name = name, Description = description, Label = name,
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = (_, _, _, _) => Task.FromResult(new AgentToolResult()),
        };

    private static AgentContext Context(IReadOnlyList<AgentTool>? tools, params Message[] messages)
        => new() { Messages = messages.ToList(), Tools = tools };

    [Fact]
    public void Tool_Changes_Insert_A_System_Message_Before_The_First_NonSystem_Prompt()
    {
        var user = new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") };
        var result = ToolLoadout.DeclareToolChanges(Context(new[] { Tool("echo") }), new Message[] { user }, 2);
        var system = Assert.IsType<SystemMessage>(result[0]);
        Assert.NotEmpty(system.ToolsAdded!);
        Assert.Equal("echo", system.ToolsAdded![0].Name);
        Assert.Equal("", system.Content.Text);
        Assert.Equal(user, result[1]);
    }

    [Fact]
    public void Changed_Definition_Is_Removal_Plus_Addition()
    {
        var previous = new SystemMessage
        {
            Timestamp = 1, Content = MessageContent.From(""),
            ToolsAdded = new[] { new Tool { Name = "echo", Description = "old", Parameters = new JsonObject { ["type"] = "object" } } },
        };
        var result = ToolLoadout.DeclareToolChanges(
            Context(new[] { Tool("echo", "new") }), Array.Empty<Message>(), 2,
            new[] { previous });
        var system = Assert.IsType<SystemMessage>(result[0]);
        Assert.Contains(system.ToolsAdded!, t => t.Name == "echo");
        Assert.Contains(system.ToolsRemoved!, r => r.Name == "echo");
    }

    [Fact]
    public void No_Changes_Do_Not_Insert_A_Message()
    {
        var user = new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") };
        var result = ToolLoadout.DeclareToolChanges(Context(null), new Message[] { user }, 2);
        Assert.Single(result);
        Assert.IsType<UserMessage>(result[0]);
    }
}
```

（`DeclareToolChanges` 签名以 §Interfaces 为准：`(AgentContext context, IReadOnlyList<Message> pending, long timestamp, IReadOnlyList<Message>? committed = null)`；测试第三例传 committed 以模拟 transcript。）

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --nologo -c Release
```

Expected: 编译失败（ToolExecution/ToolLoadout 不存在）。

- [ ] **Step 3: 实现 ToolExecution（移植 + 适配）**

复制 pi.net `src/Pi.Agent/ToolExecution.cs` 到 `src/PiUnityAgent.Core/Agent/ToolExecution.cs`，namespace 改 `PiUnityAgent.Core.Agent`，按以下规则适配：

1. `Pi.Agent.Types`/`Pi.Ai.Utils` using → Core Agent/Tools + `PiUnityAgent.Ai.Utils`（`ToolValidation`）。
2. `ChatMessage`→`Message`；`ToolCallContent`→`ToolCall`；`AssistantMessage.ToolCalls` 扩展→`message.Content.OfType<ToolCall>()`；`ToolSchema`→`JsonObject`。
3. `AgentTool` 构造/属性名按 S6；`AgentToolResult` 是 record 属性（`Content/Details/...`）而非位置构造。
4. `ToolValidation.ValidateToolArguments(tool.Definition, preparedCall)`（不是 pi.net 的 Validation.ValidateToolArguments(tool.Definition,...) 名称差异）。
5. 事件 args 用 `JsonObject`；`ToolExecutionEnd.Result` 用 `AgentToolResult`；`AgentToolResult` 的 Details `JsonNode?`，`ToolResultMessage.Details` 是 `object?`（赋值即可）。
6. `DurationMs` 用 `Stopwatch`；写入 `ToolResultMessage.DurationMs` 时 `(int)Math.Min(int.MaxValue, duration)`。
7. 截断错误文案逐字：
```
Tool call "<name>" was not executed: the response hit the output token limit, so its arguments may be truncated. Re-issue the tool call with complete arguments.
```
8. 批次函数 `ExecuteToolCalls`/`ExecuteSequential`/`ExecuteParallel`/`FailToolCallsFromTruncatedMessage` 保持 internal（Core.Tests 经 IVT 可见）。
9. `RunToolCallAsync` 公开；签名按 §Interfaces；onUpdate settle 后忽略（`acceptingUpdates` 标志）；after hook 的 structuredContent 丢弃规则逐字按 pi。

- [ ] **Step 4: 实现 ToolLoadout（pi TS 语义）**

`ToolLoadout.cs` 完整实现：

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// 工具装载变更公告（pi declareToolChanges）。context.Tools 是运行时可执行集；
/// transcript 的 system 消息声明模型可调用集；每次请求前把差异写入系统消息。
/// S9/D11 的公开 transcript 重放辅助不在本阶段。
/// </summary>
internal static class ToolLoadout
{
    public static IReadOnlyList<Message> DeclareToolChanges(
        AgentContext context,
        IReadOnlyList<Message> pendingMessages,
        long timestamp,
        IReadOnlyList<Message>? committed = null)
    {
        var pending = pendingMessages.ToList();
        var systemIndex = -1;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            if (pending[i] is SystemMessage) { systemIndex = i; break; }
        }

        var baseline = pending.Select((message, index) =>
            index == systemIndex && message is SystemMessage system
                ? WithToolChanges(system, Array.Empty<Tool>(), Array.Empty<ToolReference>())
                : message).ToList();

        var currentTools = GetCurrentTools(committed ?? context.Messages, baseline);
        var desired = (context.Tools ?? Array.Empty<AgentTool>()).Select(ToToolDeclaration).ToList();
        var changes = GetToolStateChanges(currentTools, desired);
        var unchanged = changes.ToolsAdded.Count == 0 && changes.ToolsRemoved.Count == 0;

        if (systemIndex >= 0 && pending[systemIndex] is SystemMessage pendingSystem)
        {
            if (unchanged && pendingSystem.ToolsAdded is null && pendingSystem.ToolsRemoved is null)
                return pendingMessages;
            pending[systemIndex] = WithToolChanges(pendingSystem, changes.ToolsAdded, changes.ToolsRemoved);
            return pending;
        }

        if (unchanged) return pendingMessages;

        var update = new SystemMessage
        {
            Content = MessageContent.From(""),
            Timestamp = timestamp,
            ToolsAdded = changes.ToolsAdded.Count > 0 ? changes.ToolsAdded : null,
            ToolsRemoved = changes.ToolsRemoved.Count > 0 ? changes.ToolsRemoved : null,
        };
        var insertIndex = pending.FindIndex(message => message is not SystemMessage);
        pending.Insert(insertIndex < 0 ? pending.Count : insertIndex, update);
        return pending;
    }

    internal static SystemMessage WithToolChanges(
        SystemMessage message, IReadOnlyList<Tool> added, IReadOnlyList<ToolReference> removed)
        => message with
        {
            ToolsAdded = added.Count > 0 ? added : null,
            ToolsRemoved = removed.Count > 0 ? removed : null,
        };

    internal static IReadOnlyList<Tool> GetCurrentTools(params IEnumerable<Message>[] messageSets)
    {
        var tools = new Dictionary<string, Tool>(StringComparer.Ordinal);
        foreach (var set in messageSets)
        {
            foreach (var message in set)
            {
                if (message is not SystemMessage system) continue;
                foreach (var reference in system.ToolsRemoved ?? Array.Empty<ToolReference>())
                    tools.Remove(reference.Name);
                foreach (var tool in system.ToolsAdded ?? Array.Empty<Tool>())
                    tools[tool.Name] = tool;
            }
        }
        return tools.Values.ToList();
    }

    internal static (IReadOnlyList<Tool> ToolsAdded, IReadOnlyList<ToolReference> ToolsRemoved)
        GetToolStateChanges(IReadOnlyList<Tool> previous, IReadOnlyList<Tool> current)
    {
        var previousTools = previous.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var currentTools = current.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var added = current.Where(tool =>
            !previousTools.TryGetValue(tool.Name, out var before) || !DeclarationsEqual(before, tool))
            .Select(ToToolDeclaration).ToList();
        var removed = previous.Where(tool =>
            !currentTools.TryGetValue(tool.Name, out var after) || !DeclarationsEqual(tool, after))
            .Select(tool => new ToolReference { Name = tool.Name }).ToList();
        return (added, removed);
    }

    internal static Tool ToToolDeclaration(AgentTool tool) => tool.Definition;

    internal static Tool ToToolDeclaration(Tool tool) => tool with
    {
        Parameters = (JsonObject)tool.Parameters.DeepClone(),
    };

    internal static bool DeclarationsEqual(Tool left, Tool right)
        => Canonical(left).ToJsonString() == Canonical(right).ToJsonString();

    private static JsonNode Canonical(Tool tool)
        => JsonSerializer.SerializeToNode(new { tool.Name, tool.Description, tool.Parameters, tool.ConstrainedSampling })
           ?? new JsonObject();
}
```

（`Tool` 有 `required` 字段与 JsonObject Parameters；`with` 可用。Canonical 键序由匿名对象固定；ConstrainedSampling 序列化以 spec §4.3 为准。）

- [ ] **Step 5: 跑测试确认通过**

```bash
dotnet build PiUnityAgent.slnx -c Release --nologo
dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --nologo -c Release
```

Expected: 0/0；Core 12 + 新增用例全绿。

- [ ] **Step 6: 提交**

```bash
git add src/PiUnityAgent.Core/Agent/ToolExecution.cs src/PiUnityAgent.Core/Agent/ToolLoadout.cs tests/PiUnityAgent.Core.Tests/Agent/ToolExecutionTests.cs tests/PiUnityAgent.Core.Tests/Agent/ToolLoadoutTests.cs
git commit -m "feat(core): 工具批次执行/单调用管线/装载公告

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: `AgentLoop` 入口 + 双层循环 + 流式响应 + 调度回调

**Files:**
- Create: `src/PiUnityAgent.Core/Agent/AgentLoop.cs`（公共入口；partial，与 AgentLoop.Identity.cs 合并）
- Create: `src/PiUnityAgent.Core/Agent/AgentLoop.Stream.cs`（`streamAssistantResponse`）
- Create: `src/PiUnityAgent.Core/Agent/AgentLoop.Run.cs`（`runLoop` 双层循环）
- Create: `src/PiUnityAgent.Core/Agent/DefaultStreamFn.cs`
- Test: `tests/PiUnityAgent.Core.Tests/Agent/AgentLoopTests.cs`
- Test: `tests/PiUnityAgent.Core.Tests/Agent/AgentLoopSchedulingTests.cs`

**Interfaces:**
- Consumes: Task 3 形状；Task 4 `ToolExecution.ExecuteToolCalls/FailToolCallsFromTruncatedMessage`、`ToolLoadout.DeclareToolChanges`；Ai `StreamFn`/`AssistantMessageEventStream`/`TranscriptNormalization`/`ClientIdentityMerger`/`EventStream`
- Produces: spec §3.5 全部公共入口；`AgentLoop.DefaultAgentIdentity`

- [ ] **Step 1: 写失败测试（事件顺序 + 工具循环）**

新建 `AgentLoopTests.cs`（helper + 两条核心用例；其余按 Step 4 列表补齐）：

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

public class AgentLoopTests
{
    private static FauxProvider FauxProviderWith(params FauxResponseStep[] steps)
    {
        var faux = new FauxProvider();
        faux.SetResponses(steps);
        return faux;
    }

    private static AgentLoopConfig Config(Model model, ToolExecutionMode mode = ToolExecutionMode.Parallel)
        => new()
        {
            Model = model,
            ToolExecution = mode,
            ConvertToLlm = messages => Task.FromResult<IReadOnlyList<Message>>(messages),
            StreamOptions = new SimpleStreamOptions(),
        };

    private static AgentTool EchoTool(string name = "echo")
        => new()
        {
            Name = name,
            Description = "Echo",
            Label = "Echo",
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = (_, args, _, _) => Task.FromResult(new AgentToolResult
            {
                Content = new ContentBlock[] { new TextContent { Text = "echo:" + (args?.ToJsonString() ?? "{}") } },
            }),
        };

    private static async Task<List<AgentEvent>> CollectAsync(EventStream<AgentEvent, IReadOnlyList<Message>> stream)
    {
        var events = new List<AgentEvent>();
        await foreach (var @event in stream) events.Add(@event);
        return events;
    }

    [Fact]
    public async Task Simple_Text_Turn_Emits_Full_Event_Sequence()
    {
        var faux = FauxProviderWith(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("你好"))));
        var stream = AgentLoop.Loop(
            new Message[] { new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") } },
            new AgentContext { Messages = new List<Message>() },
            Config(faux.GetModel()),
            CancellationToken.None,
            faux.Provider.Stream);
        var events = await CollectAsync(stream);

        Assert.IsType<AgentEvent.AgentStart>(events[0]);
        Assert.IsType<AgentEvent.TurnStart>(events[1]);
        Assert.IsType<AgentEvent.MessageStart>(events[2]);
        Assert.Contains(events, e => e is AgentEvent.MessageUpdate);
        Assert.IsType<AgentEvent.MessageEnd>(events[^3]);
        Assert.IsType<AgentEvent.TurnEnd>(events[^2]);
        Assert.IsType<AgentEvent.AgentEnd>(events[^1]);

        var result = await stream.WaitForResultAsync();
        Assert.Equal(2, result.Count);
        Assert.IsType<UserMessage>(result[0]);
        Assert.Equal("你好", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[1]).Content[0]).Text);
    }

    [Fact]
    public async Task Tool_Call_Turn_Executes_Tool_And_Continues()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { Faux.ToolCall("echo", new JsonObject { ["value"] = "hi" }) },
                StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("done"))));
        var stream = AgentLoop.Loop(
            new Message[] { new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") } },
            new AgentContext { Messages = new List<Message>(), Tools = new[] { EchoTool() } },
            Config(faux.GetModel()),
            CancellationToken.None,
            faux.Provider.Stream);
        var events = await CollectAsync(stream);
        var result = await stream.WaitForResultAsync();

        Assert.Contains(events, e => e is AgentEvent.ToolExecutionStart);
        Assert.Contains(events, e => e is AgentEvent.ToolExecutionEnd { IsError: false });
        var toolResult = Assert.IsType<ToolResultMessage>(result[2]);
        Assert.False(toolResult.IsError);
        Assert.Equal("done", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(result[^1]).Content[0]).Text);
        Assert.Equal(2, faux.State.CallCount);
    }
}
```

新建 `AgentLoopSchedulingTests.cs`（三条核心）：

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

public class AgentLoopSchedulingTests
{
    private static FauxProvider FauxProviderWith(params FauxResponseStep[] steps)
    {
        var faux = new FauxProvider();
        faux.SetResponses(steps);
        return faux;
    }

    private static AgentLoopConfig Config(Model model)
        => new()
        {
            Model = model,
            ConvertToLlm = messages => Task.FromResult<IReadOnlyList<Message>>(messages),
            StreamOptions = new SimpleStreamOptions(),
        };

    [Fact]
    public async Task FinishTurn_End_Stops_Requests()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))));
        var config = Config(faux.GetModel()) with
        {
            FinishTurn = (_, _) => Task.FromResult<AgentTurnDecision?>(AgentTurnDecision.End),
        };
        await foreach (var _ in AgentLoop.Loop(
            new Message[] { new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") } },
            new AgentContext { Messages = new List<Message>() }, config, CancellationToken.None, faux.Provider.Stream)) { }
        Assert.Equal(1, faux.State.CallCount);
    }

    [Fact]
    public void Continue_Rejects_Assistant_Last_Message()
    {
        var faux = FauxProviderWith();
        var context = new AgentContext
        {
            Messages = new List<Message> { Faux.AssistantMessage(Faux.Text("x")) },
        };
        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentLoop.Continue(context, Config(faux.GetModel()), CancellationToken.None, null));
        Assert.Equal("Cannot continue from message role: assistant", error.Message);
    }

    // 注：pi 在 runLoop 入口先轮询一次 steering；本用例让第一次轮询返回空、第二次（工具批后）返回 steering。
    [Fact]
    public async Task Steering_Messages_Are_Injected_After_Tool_Batch()
    {
        var faux = FauxProviderWith(
            new FauxResponseStep.Response(Faux.AssistantMessage(
                new ContentBlock[] { new ToolCall { Id = "c1", Name = "echo" } }, StopReason.ToolUse)),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("final"))));
        var tool = new AgentTool
        {
            Name = "echo", Description = "Echo", Label = "Echo",
            Parameters = new JsonObject { ["type"] = "object" },
            Execute = (_, _, _, _) => Task.FromResult(new AgentToolResult()),
        };
        var steering = new Message[] { new UserMessage { Timestamp = 3, Content = MessageContent.From("steer") } };
        var poll = 0;
        IReadOnlyList<Message> NextSteering()
        {
            poll++;
            return poll == 2 ? steering : Array.Empty<Message>();
        }
        var config = Config(faux.GetModel()) with
        {
            GetSteeringMessages = () => Task.FromResult(NextSteering()),
        };
        var context = new AgentContext { Messages = new List<Message>(), Tools = new[] { tool } };
        await foreach (var _ in AgentLoop.Loop(
            new Message[] { new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") } },
            context, config, CancellationToken.None, faux.Provider.Stream)) { }

        Assert.Contains(context.Messages, m =>
            m is UserMessage { Content: MessageContent.Text { Value: "steer" } });
    }
}
```

（第三条的 `Content.Text` 访问方式按 `MessageContent` 形状调整：可用 `((MessageContent)content).Text` 或 `MessageContent.From` 的等值比较；实现者按实际属性补全。）

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --nologo -c Release
```

Expected: 编译失败（`AgentLoop` 无入口）。

- [ ] **Step 3: 实现（移植 + 适配）**

参考源：`D:/shuoshu/taiwuask/pi-study/pi.net/src/Pi.Agent/AgentLoop.cs`（131 行）、`AgentLoopRunner.cs`（387 行）、`StreamFn.cs`（24 行）。复制/移植后按下列规则适配：

1. namespace → `PiUnityAgent.Core.Agent`；`ChatMessage`→`Message`；`ToolCallContent`→`ToolCall`；`ToolSchema`→`JsonObject`；`object? Args`→`JsonNode?`；`object? Result/Partial`→`AgentToolResult`。
2. 公共命名：`Run`→`Loop`、`RunContinue`→`Continue`、`RunWithPrompts`→`RunAsync`、`RunContinueCore`→`RunContinueAsync`；`streamFn` 参数可空（null→`DefaultStreamFn.Get()`）。
3. `CreateAgentStream`：
```csharp
private static EventStream<AgentEvent, IReadOnlyList<Message>> CreateAgentStream()
    => new(
        @event => @event is AgentEvent.AgentEnd,
        @event => ((AgentEvent.AgentEnd)@event).Messages);
```
`PumpAsync`：完成→`stream.End(result)`；异常→`stream.Fail(error)`。
4. `DeclareToolChanges` 调用：
```csharp
ToolLoadout.DeclareToolChanges(context, pendingMessages, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
```
5. `streamAssistantResponse`：
   - `transformContext` → `convertToLlm` → `TranscriptNormalization.NormalizeContext(llmMessages)`；
   - resolvedApiKey = `GetApiKey?.Invoke(provider, ct)` ?? `StreamOptions?.ApiKey`；
   - options = (`StreamOptions` ?? new `SimpleStreamOptions`()) with `{ ApiKey = resolved, Identity = ClientIdentityMerger.Merge(DefaultAgentIdentity, config.Identity), CancellationToken = signal }`；
   - 事件 `start`→push partial + `message_start`；delta/end→替换 context 末条 + `message_update`；`done/error`→取 result、设置 `ThinkingLevel`、必要时替换/追加、确保 `message_start`、发 `message_end`；
   - `ThinkingLevel` 映射：`config.StreamOptions?.Reasoning is { } r ? MapToModelThinkingLevel(r) : ModelThinkingLevel.Off`；
   - 事件 message 快照直接使用事件内 partial/final（record 不可变）。
6. `runLoop` 按 spec §4.1 逐条；工具批用 Task 4；`turn_end`/`agent_end` 顺序；error/aborted 硬退出；explicitContinuation/follow-up 逐句照 pi。
7. `DefaultStreamFn.cs`：
```csharp
using PiUnityAgent.Ai.Types;
namespace PiUnityAgent.Core.Agent;

public static class DefaultStreamFn
{
    private static StreamFn? _default;
    public static void Set(StreamFn? streamFn) => _default = streamFn;
    public static StreamFn Get() => _default
        ?? throw new InvalidOperationException(
            "No default stream function configured. Pass streamFn explicitly or call DefaultStreamFn.Set().");
    public static bool IsConfigured => _default is not null;
}
```
8. `AgentLoop.Identity.cs`（Task 3 已建）内容：
```csharp
using PiUnityAgent.Ai.Types;
namespace PiUnityAgent.Core.Agent;

public static partial class AgentLoop
{
    public static ClientIdentity DefaultAgentIdentity { get; } =
        new() { Attribution = ClientIdentityHelpers.PiAttribution };
}
```

- [ ] **Step 4: 补齐其余循环行为测试**

从 pi.net `tests/Pi.Agent.Tests/AgentLoopTests.cs`（375 行）移植并适配：

1. `ToolCallTurnRecordsExecutionDuration`
2. `BlockedToolCallProducesErrorResult`
3. `MalformedToolArgumentsAreReportedWithoutExecuting`
4. `SchemaViolationIsReportedWithoutExecuting`
5. `ToolReceivesCoercedArguments`
6. `ErrorStopReasonIsHardExit`
7. `TruncatedToolCallsAllFail`（错误文案逐字）
8. `SteeringMessagesInjectMidRun`
9. `ContinueRejectsAssistantLastMessage`（已写）
10. `Parallel_End_Completion_Order_But_Result_Message_Source_Order`（TaskCompletionSource 控制完成序）
11. `Sequential_Mode_Stops_After_Abort`
12. `PrepareRequest_And_PrepareNextTurn_Can_Replace_State`
13. `FollowUp_Messages_Continue_After_Inner_Loop_Exits`
14. `DefaultStreamFn_Missing_Throws_Pi_Message`

适配对照：`Faux.CreateStreamFn([...])`→`faux.Provider.Stream`；`AgentLoop.Run`→`AgentLoop.Loop`；`AgentContext.Messages=[]`→`new List<Message>()`；`ChatMessage`→`Message`；`AgentToolResult([...], IsError:…)`→属性构造；`ToolSchema`→`JsonObject`；`ToolCallContent`→`ToolCall`。

- [ ] **Step 5: 跑测试确认通过**

```bash
dotnet build PiUnityAgent.slnx -c Release --nologo
dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --nologo -c Release
```

Expected: 0/0；Core 全绿（12 + Task 4 用例 + 本任务 ≥17）。

- [ ] **Step 6: 提交**

```bash
git add src/PiUnityAgent.Core/Agent/AgentLoop.cs src/PiUnityAgent.Core/Agent/AgentLoop.Stream.cs src/PiUnityAgent.Core/Agent/AgentLoop.Run.cs src/PiUnityAgent.Core/Agent/DefaultStreamFn.cs tests/PiUnityAgent.Core.Tests/Agent/AgentLoopTests.cs tests/PiUnityAgent.Core.Tests/Agent/AgentLoopSchedulingTests.cs
git commit -m "feat(core): AgentLoop 双层循环/流式响应/调度回调（Faux 全覆盖）

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 6: D16 结账 + D22 登记 + 架构/映射文档

**Files:**
- Modify: `docs/待办-后置能力.md`（D16 转已完成；D22 新增；最后更新）
- Modify: `src/PiUnityAgent.Core/Agent/DefaultStreamFn.cs`（D22 锚注释）
- Modify: `src/PiUnityAgent.Ai/Api/ToolCallArguments.cs`（删除 `TODO(S7, D16)`）
- Modify: `docs/05-教学阶段路线.md`（§2.1 后续扩展 append D22）
- Modify: `docs/06-工程架构与包依赖.md`（Core Agent 内容、Core.Tests、§七）
- Modify: `PORTING.md`（S7 映射）

**Interfaces:**
- Consumes: Task 1–5 提交
- Produces: D16 `已完成`（含完成提交/验收锚点）、D22 行、文档现势

- [ ] **Step 1: D16 台账转已完成**

把 D16 行的状态改 `已完成`、完成提交填 Task 2 提交 SHA，备注追加：

```markdown
S7 承接完成（`<Task 2 commit>`）：StreamingJsonParser/JsonParse/partial 快照 + 三适配器接线；`ToolCallArgumentsTests` 现势钉改为部分快照/repair 用例。
```

测试锚点列改为：

```markdown
`ToolCallArgumentsTests.ToolCallArguments_Exposes_Growing_Partial_Json` / `ToolCallArguments_Finish_Repairs_Recoverable_Json`；`StreamingJsonTests`；`JsonParseTests`；三适配器 partial 快照用例
```

并删除 `ToolCallArguments.cs` 的 `TODO(S7, D16)` 注释行。

- [ ] **Step 2: D22 行与锚**

台账 D21 之后加：

```markdown
| D22 | proxy.ts 服务端代理流函数 | pi `packages/agent/src/proxy.ts`（395 行：ProxyAssistantMessageEvent 去 partial、流重组、tool 参数流） | `Core/Agent/DefaultStreamFn.cs`（源码标注 TODO）；pi.net `Pi.Agent/Proxy.cs` 可作 C# 参考 | 待补：Proxy 协议流测试（实现时新增） | 未实现 | 后续扩展 | 宿主经服务端代理 LLM 流；事件/工具参数按 proxy 协议重组 | 未开工 | — | S7 spec §6 登记；server RPC 路线不在 Agent Loop 阶段 |
```

`DefaultStreamFn.cs` 顶部加：

```csharp
// TODO(后续扩展, D22): proxy.ts 服务端代理流函数未移植（server RPC 路线）。
```

- [ ] **Step 3: 文档现势**

1. `docs/05` §2.1 后续扩展行追加 `D22 proxy 服务端代理流`。
2. `docs/06` §3.2 Core 内容追加：
```markdown
Agent/  AgentLoop*.cs / ToolExecution.cs / ToolLoadout.cs / DefaultStreamFn.cs / 事件与配置类型
```
§3.4 Core.Tests 说明改为“S6 契约 + S7 循环用例（实际以 dotnet test 为准）”。
§七追加：
```markdown
- **S7** Agent Loop（`feature/s7-agent-loop`）：事件/批次/hooks/调度 + D16 承接；审计待跑。
```
3. `PORTING.md` 追加：
```markdown
| `packages/agent/src/agent-loop.ts` | `src/PiUnityAgent.Core/Agent/*` | ✅ S7 |
| `packages/agent/src/stream-fn.ts` | `src/PiUnityAgent.Core/Agent/DefaultStreamFn.cs` | ✅ S7 |
| `packages/agent/src/proxy.ts` | —（D22） | 🟡 后续扩展 |
| `packages/ai/src/utils/streaming-json.ts`、`json-parse.ts` | `src/PiUnityAgent.Ai/Utils/{StreamingJson,JsonParse}.cs` | ✅ S7/D16 |
```

- [ ] **Step 4: 门禁**

```bash
dotnet build PiUnityAgent.slnx -c Release --nologo
python scripts/check-deferred.py --root . --current-stage S7 --gate exit
```

Expected: 0/0；D16 无 stub；D22=1；registry 22；gate OK。

- [ ] **Step 5: 提交**

```bash
git add docs/待办-后置能力.md docs/05-教学阶段路线.md docs/06-工程架构与包依赖.md PORTING.md src/PiUnityAgent.Core/Agent/DefaultStreamFn.cs src/PiUnityAgent.Ai/Api/ToolCallArguments.cs
git commit -m "docs(deferred): S7 收尾——D16 完成、D22 登记、架构/映射现势

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 7: 全量门禁

**Files:** 无改动

- [ ] **Step 1: 构建 + 三套测试 + 台账**

```bash
cd D:/shuoshu/taiwuask/PiUnityAgent/.worktrees/s7-agent-loop
dotnet build PiUnityAgent.slnx -c Release --nologo
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo -c Release
dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --nologo -c Release
python scripts/check-deferred.py --root . --current-stage S7 --gate exit
```

Expected: 0/0；Ai 全绿（含 D16 新用例）；Core 全绿（≥40）；D16 已完成、D22 行，gate OK。

- [ ] **Step 2: 边界抽查**

```bash
grep -rn "using UnityEngine\|using UnityLlmTransport\|using GameData\|using HarmonyLib" src/PiUnityAgent.Ai src/PiUnityAgent.Core --include="*.cs" || true
grep -rn "namespace Pi.Ai\." src/PiUnityAgent.Ai src/PiUnityAgent.Core || true
grep -rn "public.*StreamingJsonParser\|public.*ToolCallArgumentsStream\|public.*class JsonParse" src/PiUnityAgent.Ai/Utils || true
grep -n "HttpClient\|UnityLlmTransport\|UnityEngine\|GameData\|Harmony" src/PiUnityAgent.Ai/PiUnityAgent.Ai.csproj src/PiUnityAgent.Core/PiUnityAgent.Core.csproj || true
```

Expected: 四条均无输出。

---

### Task 8: 独立偏离审计（控制器派新 subagent）

**Files:**
- Create: `docs/audits/2026-11-09-S7-deviation-audit.md`
- Modify: `docs/audits/README.md`

- [ ] **Step 1: 派独立审计 subagent（haiku）**

Prompt 要求：参考顺序 pi（v1.5.0/f9a263655）→ pi.net（2d6757b，仅 C# 表达）→ Unity/2.1（A 类）；只读；范围=分支起点（Task 0 基线）..HEAD；按 SOP §3 必审：
- 形状：AgentContext/Config/AgentEvent/hooks/outcome/AgentLoop 公共入口 vs pi `agent/types.ts`+`agent-loop.ts`；D16 工具 vs pi streaming-json/json-parse。
- 行为：双层循环、事件顺序/快照、工具批次串并行/end 与消息顺序/terminate/截断、runToolCall 管线与 hook 合并、调度回调、continue 守卫、装载公告、D16 partial/repair 文案。
- wire/生态：无新 wire；边界/TFM/internal 公共面；文案逐字。
- 台账：D16 完成证据（提交/测试锚）、D22 三件套、check-deferred。
- spec §6 逐行核对；找未登记偏离（含 S7/S8 钩子边界的 scope 说明是否成立）。
- 报告 `docs/audits/2026-11-09-S7-deviation-audit.md`，SOP §6 模板，结论三选一。

- [ ] **Step 2: 按结论处理**

- PASS / PASS_WITH_REGISTERED_DEVIATIONS：README 登记，提交。
- FAIL：findings 回派一个 fix subagent；修后 Task 7 门禁 + 派**新**审计者复核（报告 `-recheck.md`）。

- [ ] **Step 3: 提交**

```bash
git add docs/audits/2026-11-09-S7-deviation-audit.md docs/audits/README.md
git commit -m "docs(audits): S7 独立偏离审计报告与索引登记

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 9: 阶段收尾文档 + 最终 whole-branch review

**Files:**
- Modify: `docs/superpowers/specs/2026-11-09-S7-AgentLoop-design.md`（状态头）
- Modify: `docs/README.md`（S7 spec/plan 两行 + 最后同步）
- Modify: `docs/05-教学阶段路线.md`（S7 行状态）
- Modify: `docs/06-工程架构与包依赖.md`（审计结论/状态）

- [ ] **Step 1: 更新状态**

1. spec 状态头：
```markdown
> **状态**：v1 已实现（Tasks 1–7 落于 `feature/s7-agent-loop`）；独立审计结论见 `docs/audits/2026-11-09-S7-deviation-audit.md`；待用户验收合 main。
```
2. `docs/README.md` 在 S5 两行之后追加 S7 spec/plan 两行（描述=D16 承接/Agent Loop/事件与批次/审计），并更新“最后同步”。
3. `docs/05` S7 行状态改为 `**实现完成，独立审计 <结论>**（feature/s7-agent-loop，D16 已完成、D22 后续扩展；待用户验收）`。
4. `docs/06` §七 S7 行改为实现完成 + 审计结论。

- [ ] **Step 2: 提交**

```bash
git add docs/README.md docs/05-教学阶段路线.md docs/06-工程架构与包依赖.md docs/superpowers/specs/2026-11-09-S7-AgentLoop-design.md
git commit -m "docs(s7): 阶段收尾状态与审计链接

Co-Authored-By: Claude <noreply@anthropic.com>"
```

- [ ] **Step 3: 最终 whole-branch review（控制器）**

用 `scripts/review-package <branch-base> HEAD` 生成包；派 reviewer（标准模型）按 code-reviewer 模板审：正确性/测试质量/边界/是否越出 S7（Agent 门面/队列/proxy 不得出现）；要求 Ready to merge 或 findings。findings → 一个 fix subagent 全量修 → Task 7 门禁 → 重审。

- [ ] **Step 4: 停在合并门**

汇报审计与终审结论、门禁数字、提交范围；等用户明确批准再 merge/push。

---

## Self-Review（计划自查）

- **spec 覆盖**：§3 形状→Task 3；§4.1/4.2→Task 5；§4.3→Task 4；§4.4/4.5→Task 4；§4.6 D16→Task 1/2；§5 测试→各任务；§6 偏离→Task 8；§8 台账→Task 6；§10 完成→Task 7–9。无遗漏。
- **占位符**：无 TBD；移植任务以源文件路径 + 逐字适配规则给出；测试代码提供核心用例，其余以“具体 pi.net 测试名 + 适配对照”列出，可执行。
- **类型一致**：`AgentLoopConfig.StreamOptions` 与 S3 `SimpleStreamOptions` 一致；`AgentToolResult`/`AgentTool` 来自 S6；`ToolValidation` 来自 S6；`ToolLoadout.DeclareToolChanges` 签名在 Task 4/5 一致；`AgentEvent` 变体在事件代码与测试中用类型断言。
- **风险**：Task 5 移植面最大，若命名适配困难用 sonnet 实现者；D16 适配器若破坏既有断言，逐条记录并保留 `argumentsError` 兼容断言。
