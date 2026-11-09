# S8 Agent 门面 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 pi `agent.ts` 的有状态 `Agent` 门面落到 `PiUnityAgent.Core`，并完成台账 D3（Deferred）、D17（助手轮次重试模块）、D23（QueueMode），使 M1（S0–S8）收口。

**Architecture:** Core 新增 `Agent`/`AgentState`/`AgentOptions`/`QueueMode` 与 internal `AgentTranscript`；门面把 S7 低层循环包装成单飞运行、双队列、abort、状态订阅与钩子装配。Ai 侧实现 `Models` deferred 管线、工厂/静态 provider 分派、Faux deferred 行为、`Retry` 模块与 Window 校验。

**Tech Stack:** C# 12 · netstandard2.1（Ai/Core）· net10.0（tests）· xUnit · System.Text.Json.Nodes · Faux provider（无网）。

## Global Constraints

- **工作位置**：代码改动必须在 `.worktrees/s8-agent-facade`（分支 `feature/s8-agent-facade`）；主 checkout 留 master（项目 SOP）。纯文档可留 master。
- **编译目标**：`PiUnityAgent.Ai` / `PiUnityAgent.Core` 必须 netstandard2.1 可编；测试工程 net10.0。禁止 net9/net10 专有 API（`IReadOnlySet`、`System.Threading.Lock`、`Task.WaitAsync`、`Task.WhenEach`）。
- **分层**：Core 只引用 Ai；Ai 不引用 Core/UnityEngine/transport/HTTP/GameData；不新增 NuGet。
- **S7 形状零回改**：`AgentLoop`、`AgentLoopConfig`、`AgentEvent`、S7 钩子 delegate、`DefaultStreamFn` 的公共签名不改。`AgentOptions.PrepareNextTurn` 是门面层 `Func<CancellationToken, Task<AgentLoopTurnUpdate?>>`，与 S7 loop 层 `PrepareNextTurn(AgentTurnContext)` 是两个不同形状。
- **行为真相源**：pi `v1.5.0` / `f9a263655`；pi.net 只作 C# 写法参考，形状不照抄。
- **测试纪律**：全部 Core/Ai 新测试走 Faux/Mock，无网络、无真实 key；Faux 的 id 必须确定性（`deferred:{n}`），不用 `DateTime.Now`/`Random`。
- **门禁命令**（在 worktree 根执行；裸 `dotnet`）：
  - `dotnet build PiUnityAgent.slnx -c Release` → 0 warnings / 0 errors
  - `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj`
  - `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj`
  - `python scripts/check-deferred.py --root . --current-stage S8 --gate exit`
- **提交纪律**：显式 `git add <paths>`；**禁止** `git add -A`。每任务结束提交一次。
- **C# 风格**：`if` 必须带花括号（项目 IDE0011 在部分工程为 error）；注释解释“为什么”，与现有代码中文注释风格一致。

---

## File Structure

```
src/PiUnityAgent.Core/Agent/
├─ Agent.cs                 门面字段/构造/State/Subscribe/生命周期/processEvents/Reset
├─ Agent.Run.cs             Prompt/RunPromptMessages/RunContinuation/context 快照/config 组装
├─ Agent.Queues.cs          队列公共 API + Continue
├─ AgentState.cs            AgentState + AgentInitialState
├─ AgentOptions.cs          AgentOptions + PrepareNextTurnContext/WithContext
├─ QueueMode.cs             QueueMode 枚举 + internal PendingMessageQueue
└─ AgentTranscript.cs       internal 初始 system 消息 + 当前 system 重放子集

src/PiUnityAgent.Ai/
├─ Utils/Retry.cs                           D17 公共模块
├─ Providers/Models.Capabilities.cs         D3 Models 管线（替换 stub）
├─ Providers/ProviderCapabilities.cs        IDeferredProvider 增 Supports*
├─ Providers/ProviderFactory.cs             CreatedProvider deferred 分派
├─ Providers/StaticProvider.cs              可选 deferred 委托
├─ Providers/Faux/Faux.cs                   FauxDeferredOptions + 状态计数
├─ Providers/Faux/FauxProvider.cs           deferred 存储/fetch/cancel/请求侧句柄
└─ Types/StreamOptionTypes.cs               DeferredRequest.Enabled.Window 校验
```

---

### Task 1: QueueMode 与待处理队列

**Files:**
- Create: `src/PiUnityAgent.Core/Agent/QueueMode.cs`
- Test: `tests/PiUnityAgent.Core.Tests/Agent/AgentQueueTests.cs`

**Interfaces:**
- Produces: `public enum QueueMode { All, OneAtATime }`；`internal sealed class PendingMessageQueue`（`Enqueue(Message)`、`HasItems()`、`Peek()`、`Drain()`、`Clear()`、`Mode`）。
- Consumed by: Task 3/4 的 `Agent`。

- [ ] **Step 1: Write the failing test**

`tests/PiUnityAgent.Core.Tests/Agent/AgentQueueTests.cs`：

```csharp
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;

namespace PiUnityAgent.Core.Tests.Agent;

public class AgentQueueTests
{
    private static Message M(string text)
        => new UserMessage { Content = MessageContent.From(text), Timestamp = 1 };

    [Fact]
    public void OneAtATime_Drains_One_Per_Call()
    {
        var queue = new PendingMessageQueue(QueueMode.OneAtATime);
        queue.Enqueue(M("a"));
        queue.Enqueue(M("b"));

        var first = queue.Drain();
        Assert.Single(first);
        Assert.Equal("a", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(first[0])
            .Content.ToBlocks()[0]).Text);

        var second = queue.Drain();
        Assert.Single(second);
        Assert.Equal("b", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(second[0])
            .Content.ToBlocks()[0]).Text);
    }

    [Fact]
    public void All_Drains_Everything_In_Insertion_Order()
    {
        var queue = new PendingMessageQueue(QueueMode.All);
        queue.Enqueue(M("a"));
        queue.Enqueue(M("b"));

        var drained = queue.Drain();
        Assert.Equal(2, drained.Count);
        Assert.Equal("a", Assert.IsType<TextContent>(
            Assert.IsType<UserMessage>(drained[0]).Content.ToBlocks()[0]).Text);
        Assert.Equal("b", Assert.IsType<TextContent>(
            Assert.IsType<UserMessage>(drained[1]).Content.ToBlocks()[0]).Text);
        Assert.False(queue.HasItems());
    }

    [Fact]
    public void Peek_Does_Not_Consume_And_Steering_Priority_Is_Caller_Side()
    {
        var queue = new PendingMessageQueue(QueueMode.OneAtATime);
        queue.Enqueue(M("a"));
        queue.Enqueue(M("b"));

        var peeked = queue.Peek();
        Assert.Single(peeked);
        Assert.True(queue.HasItems());
        Assert.Single(queue.Drain()); // 预览未消费；Drain 只取首条
        Assert.True(queue.HasItems());
    }

    [Fact]
    public void Clear_Empties_And_Mode_Can_Change()
    {
        var queue = new PendingMessageQueue(QueueMode.OneAtATime);
        queue.Enqueue(M("a"));
        queue.Enqueue(M("b"));
        queue.Clear();
        Assert.False(queue.HasItems());

        queue.Enqueue(M("a"));
        queue.Enqueue(M("b"));
        queue.Mode = QueueMode.All;
        Assert.Equal(2, queue.Drain().Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --filter FullyQualifiedName~AgentQueueTests`
Expected: compile error: `QueueMode`/`PendingMessageQueue` not found.

- [ ] **Step 3: Write minimal implementation**

`src/PiUnityAgent.Core/Agent/QueueMode.cs`：

```csharp
using System;
using System.Collections.Generic;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

/// <summary>队列排空模式。对应 pi types.ts:55 的 "all" | "one-at-a-time"。</summary>
public enum QueueMode
{
    /// <summary>一次排空全部排队消息。</summary>
    All,

    /// <summary>每次只投递最旧的一条，其余留给下个排空点。</summary>
    OneAtATime,
}

/// <summary>
/// pi agent.ts:149-180 PendingMessageQueue 的 C# 等价物。
/// 队列可被 Unity 主线程与循环续体跨线程访问，故内部加锁；pi 本身单线程无锁。
/// </summary>
internal sealed class PendingMessageQueue
{
    private readonly object _gate = new();
    private readonly List<Message> _messages = new();
    private QueueMode _mode;

    public PendingMessageQueue(QueueMode mode)
        => _mode = mode;

    public QueueMode Mode
    {
        get
        {
            lock (_gate) return _mode;
        }
        set
        {
            lock (_gate) _mode = value;
        }
    }

    public void Enqueue(Message message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        lock (_gate) _messages.Add(message);
    }

    public bool HasItems()
    {
        lock (_gate) return _messages.Count > 0;
    }

    /// <summary>预览将被选取的消息，不消费。</summary>
    public IReadOnlyList<Message> Peek()
    {
        lock (_gate) return Select();
    }

    /// <summary>排空并移除被选取的消息。</summary>
    public IReadOnlyList<Message> Drain()
    {
        lock (_gate)
        {
            var drained = Select();
            if (drained.Count > 0) _messages.RemoveRange(0, drained.Count);
            return drained;
        }
    }

    public void Clear()
    {
        lock (_gate) _messages.Clear();
    }

    private IReadOnlyList<Message> Select()
    {
        if (_mode == QueueMode.All) return _messages.ToArray();
        return _messages.Count > 0 ? new[] { _messages[0] } : Array.Empty<Message>();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --filter FullyQualifiedName~AgentQueueTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PiUnityAgent.Core/Agent/QueueMode.cs tests/PiUnityAgent.Core.Tests/Agent/AgentQueueTests.cs
git commit -m "feat(s8): QueueMode 与待处理消息队列（D23 存储层）"
```

---

### Task 2: AgentState / AgentInitialState / AgentTranscript

**Files:**
- Create: `src/PiUnityAgent.Core/Agent/AgentState.cs`
- Create: `src/PiUnityAgent.Core/Agent/AgentTranscript.cs`
- Test: `tests/PiUnityAgent.Core.Tests/Agent/AgentStateTests.cs`

**Interfaces:**
- Consumes: `ToolLoadout.GetCurrentTools(params IEnumerable<Message>[])`（internal，S7 已有）；`AgentTool.Definition`。
- Produces:
  - `public sealed record AgentInitialState { string? SystemPrompt; Model? Model; ModelThinkingLevel? ThinkingLevel; IReadOnlyList<AgentTool>? Tools; IReadOnlyList<Message>? Messages; }`
  - `public sealed class AgentState`（`SystemPrompt`、`Model`、`ThinkingLevel`、`Tools`、`Messages`、`IsStreaming`、`StreamingMessage`、`PendingToolCalls`、`ErrorMessage` + internal 变更方法）
  - `internal static class AgentTranscript`（`CreateInitialSystemMessage`、`GetCurrentSystemMessage`、`GetCurrentSystemPrompt`）。

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Tests.Agent;

public class AgentStateTests
{
    private static AgentTool Tool(string name) => new()
    {
        Name = name,
        Description = name,
        Label = name,
        Parameters = new JsonObject { ["type"] = "object" },
        Execute = (_, _, _, _) => Task.FromResult(new AgentToolResult()),
    };

    private static SystemMessage System(string text, long timestamp = 1)
        => new() { Content = MessageContent.From(text), Timestamp = timestamp };

    [Fact]
    public void Defaults_Match_Pi_Default_Model_And_Off_Thinking()
    {
        var state = new AgentState(initialState: null);

        Assert.Equal("unknown", state.Model.Id);
        Assert.Equal("unknown", state.Model.Provider);
        Assert.Equal(0, state.Model.ContextWindow);
        Assert.Equal(0, state.Model.MaxTokens);
        Assert.Empty(state.Model.Input);
        Assert.Equal(ModelThinkingLevel.Off, state.ThinkingLevel);
        Assert.Empty(state.Messages);
        Assert.Empty(state.Tools);
        Assert.False(state.IsStreaming);
        Assert.Null(state.StreamingMessage);
        Assert.Empty(state.PendingToolCalls);
        Assert.Null(state.ErrorMessage);
    }

    [Fact]
    public void Initial_SystemPrompt_And_Tools_Fold_Into_Leading_System_Message()
    {
        var state = new AgentState(new AgentInitialState
        {
            SystemPrompt = "you are pi",
            Tools = new[] { Tool("echo") },
        });

        var first = Assert.IsType<SystemMessage>(state.Messages[0]);
        Assert.Equal("you are pi", Assert.IsType<TextContent>(first.Content.ToBlocks()[0]).Text);
        Assert.Single(first.ToolsAdded!);
        Assert.Equal("echo", first.ToolsAdded![0].Name);
        Assert.Equal("you are pi", state.SystemPrompt);
    }

    [Fact]
    public void Existing_Leading_System_Message_Is_Not_Duplicated()
    {
        var existing = System("custom");
        var state = new AgentState(new AgentInitialState
        {
            SystemPrompt = "ignored",
            Messages = new Message[] { existing },
        });

        Assert.Single(state.Messages);
        Assert.Same(existing, state.Messages[0]);
    }

    [Fact]
    public void SystemPrompt_Replays_Content_Sections_And_Tools()
    {
        var messages = new Message[]
        {
            new SystemMessage
            {
                Content = MessageContent.From("base"),
                Sections = new Dictionary<string, string?> { ["tone"] = "calm" },
                ToolsAdded = new[] { Tool("echo").Definition },
                Timestamp = 1,
            },
            new SystemMessage
            {
                Content = MessageContent.From("later"),
                Sections = new Dictionary<string, string?> { ["tone"] = null, ["mode"] = "strict" },
                ToolsRemoved = new[] { new ToolReference { Name = "echo" } },
                Timestamp = 2,
            },
        };

        var prompt = AgentTranscript.GetCurrentSystemPrompt(messages);
        Assert.Contains("base", prompt);
        Assert.Contains("later", prompt);
        Assert.Contains("strict", prompt);
        Assert.DoesNotContain("calm", prompt);
    }

    [Fact]
    public void Messages_And_Tools_Setters_Copy_Top_Level_Array()
    {
        var source = new List<Message> { System("s") };
        var state = new AgentState(null);
        state.Messages = source;
        source.Add(new UserMessage { Content = MessageContent.From("u"), Timestamp = 2 });

        Assert.Single(state.Messages);

        var tools = new List<AgentTool> { Tool("echo") };
        state.Tools = tools;
        tools.Add(Tool("other"));
        Assert.Single(state.Tools);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --filter FullyQualifiedName~AgentStateTests`
Expected: compile error: `AgentState`/`AgentInitialState`/`AgentTranscript` not found.

- [ ] **Step 3: Write minimal implementation**

`src/PiUnityAgent.Core/Agent/AgentState.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>构造期初始状态；对应 pi AgentInitialState（AgentState 的可写子集）。</summary>
public sealed record AgentInitialState
{
    public string? SystemPrompt { get; init; }
    public Model? Model { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
    public IReadOnlyList<AgentTool>? Tools { get; init; }
    public IReadOnlyList<Message>? Messages { get; init; }
}

/// <summary>
/// 公开运行状态；对应 pi AgentState。读写面按 pi：tools/messages 赋值复制顶层数组。
/// 内部状态变更由 Agent 在 _gate 保护下调用；公开 getter 返回快照。
/// </summary>
public sealed class AgentState
{
    private readonly object _gate = new();
    private readonly List<AgentTool> _tools;
    private readonly List<Message> _messages;
    private readonly HashSet<string> _pendingToolCalls = new(StringComparer.Ordinal);
    private Model _model;
    private ModelThinkingLevel _thinkingLevel;
    private bool _isStreaming;
    private Message? _streamingMessage;
    private string? _errorMessage;

    internal AgentState(AgentInitialState? initialState)
    {
        _model = initialState?.Model ?? DefaultModel();
        _thinkingLevel = initialState?.ThinkingLevel ?? ModelThinkingLevel.Off;
        _tools = initialState?.Tools?.ToList() ?? new List<AgentTool>();
        _messages = initialState?.Messages?.ToList() ?? new List<Message>();

        var initial = AgentTranscript.CreateInitialSystemMessage(initialState?.SystemPrompt, _tools);
        if (initial is not null && (_messages.Count == 0 || _messages[0] is not SystemMessage))
        {
            _messages.Insert(0, initial);
        }
    }

    /// <summary>从 transcript 的 system 消息回读的当前 prompt（只读）。</summary>
    public string SystemPrompt => AgentTranscript.GetCurrentSystemPrompt(Messages);

    public Model Model
    {
        get
        {
            lock (_gate) return _model;
        }
        set
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            lock (_gate) _model = value;
        }
    }

    public ModelThinkingLevel ThinkingLevel
    {
        get
        {
            lock (_gate) return _thinkingLevel;
        }
        set
        {
            lock (_gate) _thinkingLevel = value;
        }
    }

    public IReadOnlyList<AgentTool> Tools
    {
        get
        {
            lock (_gate) return _tools.ToArray();
        }
        set
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            lock (_gate)
            {
                _tools.Clear();
                _tools.AddRange(value);
            }
        }
    }

    public IReadOnlyList<Message> Messages
    {
        get
        {
            lock (_gate) return _messages.ToArray();
        }
        set
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            lock (_gate)
            {
                _messages.Clear();
                _messages.AddRange(value);
            }
        }
    }

    public bool IsStreaming
    {
        get
        {
            lock (_gate) return _isStreaming;
        }
    }

    public Message? StreamingMessage
    {
        get
        {
            lock (_gate) return _streamingMessage;
        }
    }

    public IReadOnlyCollection<string> PendingToolCalls
    {
        get
        {
            lock (_gate) return _pendingToolCalls.ToArray();
        }
    }

    public string? ErrorMessage
    {
        get
        {
            lock (_gate) return _errorMessage;
        }
    }

    internal void SetIsStreaming(bool value)
    {
        lock (_gate) _isStreaming = value;
    }

    internal void SetStreamingMessage(Message? message)
    {
        lock (_gate) _streamingMessage = message;
    }

    internal void AddMessage(Message message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        lock (_gate) _messages.Add(message);
    }

    internal void AddPendingToolCall(string toolCallId)
    {
        lock (_gate) _pendingToolCalls.Add(toolCallId);
    }

    internal void RemovePendingToolCall(string toolCallId)
    {
        lock (_gate) _pendingToolCalls.Remove(toolCallId);
    }

    internal void SetErrorMessage(string? message)
    {
        lock (_gate) _errorMessage = message;
    }

    internal void ClearPendingToolCalls()
    {
        lock (_gate) _pendingToolCalls.Clear();
    }

    internal void ClearRuntime()
    {
        lock (_gate)
        {
            _isStreaming = false;
            _streamingMessage = null;
            _pendingToolCalls.Clear();
            _errorMessage = null;
        }
    }

    internal void ReplaceMessages(IReadOnlyList<Message> messages)
    {
        lock (_gate)
        {
            _messages.Clear();
            _messages.AddRange(messages);
        }
    }

    private static Model DefaultModel() => new()
    {
        Id = "unknown",
        Name = "unknown",
        Api = "unknown",
        Provider = "unknown",
        BaseUrl = string.Empty,
        Input = Array.Empty<ModelInputModality>(),
        Cost = new ModelCost(),
        ContextWindow = 0,
        MaxTokens = 0,
    };
}
```

`src/PiUnityAgent.Core/Agent/AgentTranscript.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// Agent 自身所需的 transcript 重放子集（初始 system 消息 + 当前 system 消息）。
/// 公开 getCurrentTools/collapseSystemMessages 仍是 D11/S9；本类 internal，S9 提升。
/// </summary>
internal static class AgentTranscript
{
    public static SystemMessage? CreateInitialSystemMessage(
        string? systemPrompt,
        IReadOnlyList<AgentTool> tools)
    {
        var hasPrompt = !string.IsNullOrEmpty(systemPrompt);
        var hasTools = tools.Count > 0;
        if (!hasPrompt && !hasTools) return null;

        return new SystemMessage
        {
            Content = MessageContent.From(systemPrompt ?? string.Empty),
            Timestamp = 0,
            ToolsAdded = hasTools
                ? tools.Select(ToolLoadout.ToToolDeclaration).ToList()
                : null,
        };
    }

    public static SystemMessage? GetCurrentSystemMessage(IReadOnlyList<Message> messages)
    {
        var content = new List<string>();
        var sections = new Dictionary<string, string?>(StringComparer.Ordinal);
        long? timestamp = null;

        foreach (var message in messages)
        {
            if (message is not SystemMessage system) continue;
            timestamp ??= system.Timestamp;

            var text = ContentText(system.Content);
            if (text.Length > 0) content.Add(text);

            if (system.Sections is not null)
            {
                foreach (var pair in system.Sections)
                {
                    if (pair.Value is null) sections.Remove(pair.Key);
                    else sections[pair.Key] = pair.Value;
                }
            }
        }

        var tools = ToolLoadout.GetCurrentTools(messages);
        if (timestamp is null && tools.Count == 0) return null;

        return new SystemMessage
        {
            Content = MessageContent.From(string.Join("\n\n", content)),
            Timestamp = timestamp ?? 0,
            Sections = sections.Count > 0 ? sections : null,
            ToolsAdded = tools.Count > 0 ? tools : null,
        };
    }

    public static string GetCurrentSystemPrompt(IReadOnlyList<Message> messages)
    {
        var message = GetCurrentSystemMessage(messages);
        if (message is null) return string.Empty;

        var parts = new List<string> { ContentText(message.Content) };
        if (message.Sections is not null)
        {
            foreach (var value in message.Sections.Values)
            {
                if (value is not null) parts.Add(value);
            }
        }

        return string.Join("\n\n", parts.Where(part => part.Length > 0));
    }

    /// <summary>pi contentText：仅 text 块，以 "\n" 连接。</summary>
    private static string ContentText(MessageContent content)
        => string.Join("\n", content.ToBlocks().OfType<TextContent>().Select(text => text.Text));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --filter FullyQualifiedName~AgentStateTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PiUnityAgent.Core/Agent/AgentState.cs src/PiUnityAgent.Core/Agent/AgentTranscript.cs tests/PiUnityAgent.Core.Tests/Agent/AgentStateTests.cs
git commit -m "feat(s8): AgentState/AgentInitialState 与 transcript 重放子集"
```

---

### Task 3: Agent 门面核心（生命周期/状态/订阅/Prompt）

**Files:**
- Create: `src/PiUnityAgent.Core/Agent/AgentOptions.cs`
- Create: `src/PiUnityAgent.Core/Agent/Agent.cs`
- Create: `src/PiUnityAgent.Core/Agent/Agent.Run.cs`
- Test: `tests/PiUnityAgent.Core.Tests/Agent/AgentLifecycleTests.cs`

**Interfaces:**
- Consumes: Task 1 `PendingMessageQueue`；Task 2 `AgentState`/`AgentInitialState`；S7 `AgentLoop.RunAsync/RunContinueAsync`、`AgentEvent`、`BuildAgentLoopConfig` 所需的 S7 类型。
- Produces: `public sealed partial class Agent`（构造、`State`、`Subscribe`、`Signal`、`Abort`、`WaitForIdle`、`Reset`、`Prompt` 三重载）；`public sealed record AgentOptions`；`public delegate Task<AgentLoopTurnUpdate?> PrepareNextTurnWithContext(PrepareNextTurnContext, CancellationToken)`；`public sealed record PrepareNextTurnContext(...)`。

- [ ] **Step 1: Write the failing test**

`tests/PiUnityAgent.Core.Tests/Agent/AgentLifecycleTests.cs`：

```csharp
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;

namespace PiUnityAgent.Core.Tests.Agent;

public class AgentLifecycleTests
{
    private static Agent NewAgent(FauxProvider faux, AgentOptions? options = null)
    {
        var initial = new AgentInitialState
        {
            Model = faux.GetModel(),
            Messages = new Message[]
            {
                new UserMessage { Content = MessageContent.From("hi"), Timestamp = 1 },
            },
        };
        return new Agent(new AgentOptions
        {
            InitialState = initial,
            StreamFn = faux.Provider.Stream,
            ConvertToLlm = options?.ConvertToLlm,
            TransformContext = options?.TransformContext,
            GetApiKey = options?.GetApiKey,
            SessionId = options?.SessionId,
            ThinkingBudgets = options?.ThinkingBudgets,
            Transport = options?.Transport,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            ToolExecution = options?.ToolExecution,
            Identity = options?.Identity,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
            SteeringMode = options?.SteeringMode ?? QueueMode.OneAtATime,
            FollowUpMode = options?.FollowUpMode ?? QueueMode.OneAtATime,
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Prompt_Emits_Full_Event_Sequence_And_Updates_State()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("hello"))));
        var agent = NewAgent(faux);

        var events = new List<AgentEvent>();
        agent.Subscribe((@event, _) =>
        {
            lock (events) events.Add(@event);
            return Task.CompletedTask;
        });

        await agent.Prompt("go");

        lock (events)
        {
            Assert.IsType<AgentEvent.AgentStart>(events[0]);
            Assert.IsType<AgentEvent.TurnStart>(events[1]);
            Assert.IsType<AgentEvent.AgentEnd>(events[^1]);
        }

        Assert.False(agent.State.IsStreaming);
        var assistant = Assert.IsType<AssistantMessage>(agent.State.Messages[^1]);
        Assert.Equal(StopReason.Stop, assistant.StopReason);
        Assert.Equal("hello", Assert.IsType<TextContent>(assistant.Content[0]).Text);
    }

    [Fact]
    public async Task Prompt_While_Running_Throws_Precise_Message()
    {
        var gate = new TaskCompletionSource<AssistantMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Factory((_, _, _, _) => gate.Task));
        var agent = NewAgent(faux);

        var running = agent.Prompt("one");
        await WaitUntilAsync(() => agent.State.IsStreaming);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.Prompt("two"));
        Assert.Equal(
            "Agent is already processing a prompt. Use steer() or followUp() to queue messages, or wait for completion.",
            error.Message);

        gate.SetResult(Faux.AssistantMessage(Faux.Text("done")));
        await running;
        await agent.WaitForIdle();
        Assert.False(agent.State.IsStreaming);
    }

    [Fact]
    public async Task Abort_Cancels_Run_And_Leaves_Idle()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Factory(async (_, options, _, _) =>
        {
            await Task.Delay(Timeout.Infinite, options?.CancellationToken ?? CancellationToken.None);
            throw new InvalidOperationException("unreachable");
        }));
        var agent = NewAgent(faux);

        var running = agent.Prompt("one");
        await WaitUntilAsync(() => agent.State.IsStreaming);
        agent.Abort();

        await running;
        await agent.WaitForIdle();
        Assert.False(agent.State.IsStreaming);

        var assistant = Assert.IsType<AssistantMessage>(agent.State.Messages[^1]);
        Assert.Equal(StopReason.Aborted, assistant.StopReason);
    }

    [Fact]
    public async Task Executor_Failure_Emits_Failure_Message_And_Four_Events()
    {
        var faux = new FauxProvider();
        var agent = new Agent(new AgentOptions
        {
            InitialState = new AgentInitialState { Model = faux.GetModel() },
            StreamFn = (_, _, _) => throw new InvalidOperationException("boom"),
        });

        var events = new List<AgentEvent>();
        agent.Subscribe((@event, _) =>
        {
            lock (events) events.Add(@event);
            return Task.CompletedTask;
        });

        await agent.Prompt("go");

        lock (events)
        {
            Assert.IsType<AgentEvent.MessageStart>(events[^4]);
            Assert.IsType<AgentEvent.MessageEnd>(events[^3]);
            Assert.IsType<AgentEvent.TurnEnd>(events[^2]);
            Assert.IsType<AgentEvent.AgentEnd>(events[^1]);
        }

        var failure = Assert.IsType<AssistantMessage>(agent.State.Messages[^1]);
        Assert.Equal(StopReason.Error, failure.StopReason);
        Assert.Equal("boom", failure.ErrorMessage);
        Assert.Equal("boom", agent.State.ErrorMessage);
    }

    [Fact]
    public async Task Reset_Keeps_Single_System_Baseline_And_Rejects_While_Running()
    {
        var gate = new TaskCompletionSource<AssistantMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faux = new FauxProvider();
        faux.SetResponses(
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Factory((_, _, _, _) => gate.Task));
        var agent = new Agent(new AgentOptions
        {
            InitialState = new AgentInitialState
            {
                SystemPrompt = "base",
                Model = faux.GetModel(),
            },
            StreamFn = faux.Provider.Stream,
        });

        await agent.Prompt("first");
        Assert.True(agent.State.Messages.Count > 1);

        agent.Reset();
        var baseline = Assert.IsType<SystemMessage>(Assert.Single(agent.State.Messages));
        Assert.Equal(0, baseline.Timestamp);
        Assert.False(agent.State.IsStreaming);

        var running = agent.Prompt("second");
        await WaitUntilAsync(() => agent.State.IsStreaming);
        var error = Assert.Throws<InvalidOperationException>(() => agent.Reset());
        Assert.Equal(
            "Agent is already processing. Wait for completion before resetting.",
            error.Message);
        gate.SetResult(Faux.AssistantMessage(Faux.Text("two")));
        await running;
    }

    [Fact]
    public async Task Subscribe_Awaits_In_Order_And_Unsubscribe_Stops_Delivery()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("ok"))));
        var agent = NewAgent(faux);
        var calls = new List<string>();

        var first = agent.Subscribe((_, _) =>
        {
            lock (calls) calls.Add("first");
            return Task.CompletedTask;
        });
        agent.Subscribe((_, _) =>
        {
            lock (calls) calls.Add("second");
            return Task.CompletedTask;
        });
        await agent.Prompt("go");
        Assert.Equal(new[] { "first", "second" }, calls.Take(2));

        first.Dispose();
        calls.Clear();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("again"))));
        await agent.Prompt("again");
        Assert.DoesNotContain("first", calls);
        Assert.Contains("second", calls);
    }

    [Fact]
    public async Task WaitForIdle_Waits_Until_AgentEnd_Listener_Settles()
    {
        var gate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("ok"))));
        var agent = NewAgent(faux);
        agent.Subscribe(async (@event, _) =>
        {
            if (@event is AgentEvent.AgentEnd) await gate.Task;
        });

        var running = agent.Prompt("go");
        await WaitUntilAsync(() => agent.State.IsStreaming);
        var idle = agent.WaitForIdle();
        var completed = await Task.WhenAny(idle, Task.Delay(200));
        Assert.NotSame(idle, completed);

        gate.SetResult(true);
        await running;
        await idle;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --filter FullyQualifiedName~AgentLifecycleTests`
Expected: compile error: `Agent`/`AgentOptions` not found.

- [ ] **Step 3: Write minimal implementation**

`src/PiUnityAgent.Core/Agent/AgentOptions.cs`：

```csharp
using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>门面层 prepareNextTurnWithContext（context + signal）；pi AgentOptions 同名成员。</summary>
public delegate Task<AgentLoopTurnUpdate?> PrepareNextTurnWithContext(
    PrepareNextTurnContext context,
    CancellationToken signal);

/// <summary>pi PrepareNextTurnContext（extends AgentTurnContext，空扩展）；C# 用同字段独立 record。</summary>
public sealed record PrepareNextTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    AgentContext Context,
    IReadOnlyList<Message> NewMessages);

/// <summary>pi AgentOptions 的 C# 形状；全部可空，缺省走 pi 默认值。</summary>
public sealed record AgentOptions
{
    public AgentInitialState? InitialState { get; init; }

    public Func<IReadOnlyList<Message>, Task<IReadOnlyList<Message>>>? ConvertToLlm { get; init; }

    public Func<IReadOnlyList<Message>, CancellationToken, Task<IReadOnlyList<Message>>>? TransformContext { get; init; }

    public StreamFn? StreamFn { get; init; }

    public Func<string, Task<string?>>? GetApiKey { get; init; }

    public BeforeToolCall? BeforeToolCall { get; init; }
    public AfterToolCall? AfterToolCall { get; init; }
    public FinishTurn? FinishTurn { get; init; }
    public PrepareRequest? PrepareRequest { get; init; }

    /// <summary>pi AgentOptions.prepareNextTurn：门面层只收 signal（与 S7 loop 层只收 context 的同名回调不同）。</summary>
    public Func<CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; init; }

    public PrepareNextTurnWithContext? PrepareNextTurnWithContext { get; init; }

    public QueueMode SteeringMode { get; init; } = QueueMode.OneAtATime;
    public QueueMode FollowUpMode { get; init; } = QueueMode.OneAtATime;

    public string? SessionId { get; init; }
    public ThinkingBudgets? ThinkingBudgets { get; init; }
    public TransportKind? Transport { get; init; }
    public int? MaxRetryDelayMs { get; init; }
    public ToolExecutionMode? ToolExecution { get; init; }
    public ClientIdentity? Identity { get; init; }

    public Func<JsonNode?, ProviderModel, Task<JsonNode?>>? OnPayload { get; init; }
    public Func<ProviderResponse, ProviderModel, Task>? OnResponse { get; init; }
    public Func<JsonNode, ProviderModel, Task>? OnProviderStreamEvent { get; init; }
}
```

`src/PiUnityAgent.Core/Agent/Agent.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// 有状态 agent 门面（pi agent.ts）。持有 transcript、双队列、运行生命周期与事件订阅。
/// 公开控制 API 可被 Unity 主线程与循环续体跨线程调用；同步状态段由 _gate 保护。
/// </summary>
public sealed partial class Agent
{
    private readonly object _gate = new();
    private readonly AgentState _state;
    private readonly List<Func<AgentEvent, CancellationToken, Task>> _listeners = new();
    private readonly PendingMessageQueue _steeringQueue;
    private readonly PendingMessageQueue _followUpQueue;
    private ActiveRun? _activeRun;

    public Agent(AgentOptions options)
    {
        options ??= new AgentOptions();
        _state = new AgentState(options.InitialState);
        ConvertToLlm = options.ConvertToLlm ?? DefaultConvertToLlm;
        TransformContext = options.TransformContext;
        StreamFunction = options.StreamFn ?? DefaultStreamFn.Get();
        GetApiKey = options.GetApiKey;
        BeforeToolCall = options.BeforeToolCall;
        AfterToolCall = options.AfterToolCall;
        FinishTurn = options.FinishTurn;
        PrepareRequest = options.PrepareRequest;
        PrepareNextTurn = options.PrepareNextTurn;
        PrepareNextTurnWithContext = options.PrepareNextTurnWithContext;
        SessionId = options.SessionId;
        ThinkingBudgets = options.ThinkingBudgets;
        Transport = options.Transport ?? TransportKind.Auto;
        MaxRetryDelayMs = options.MaxRetryDelayMs;
        ToolExecution = options.ToolExecution ?? ToolExecutionMode.Parallel;
        Identity = options.Identity;
        OnPayload = options.OnPayload;
        OnResponse = options.OnResponse;
        OnProviderStreamEvent = options.OnProviderStreamEvent;
        _steeringQueue = new PendingMessageQueue(options.SteeringMode);
        _followUpQueue = new PendingMessageQueue(options.FollowUpMode);
    }

    /// <summary>当前状态；对应 pi agent.state。</summary>
    public AgentState State => _state;

    public Func<IReadOnlyList<Message>, Task<IReadOnlyList<Message>>> ConvertToLlm { get; set; }
    public Func<IReadOnlyList<Message>, CancellationToken, Task<IReadOnlyList<Message>>>? TransformContext { get; set; }
    public StreamFn StreamFunction { get; set; }
    public Func<string, Task<string?>>? GetApiKey { get; set; }
    public BeforeToolCall? BeforeToolCall { get; set; }
    public AfterToolCall? AfterToolCall { get; set; }
    public FinishTurn? FinishTurn { get; set; }
    public PrepareRequest? PrepareRequest { get; set; }
    public Func<CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; set; }
    public PrepareNextTurnWithContext? PrepareNextTurnWithContext { get; set; }
    public string? SessionId { get; set; }
    public ThinkingBudgets? ThinkingBudgets { get; set; }
    public TransportKind Transport { get; set; }
    public int? MaxRetryDelayMs { get; set; }
    public ToolExecutionMode ToolExecution { get; set; }
    public ClientIdentity? Identity { get; set; }
    public Func<JsonNode?, ProviderModel, Task<JsonNode?>>? OnPayload { get; set; }
    public Func<ProviderResponse, ProviderModel, Task>? OnResponse { get; set; }
    public Func<JsonNode, ProviderModel, Task>? OnProviderStreamEvent { get; set; }

    /// <summary>按订阅顺序 await；返回取消订阅句柄。同一 listener 重复订阅只保留一个。</summary>
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener)
    {
        if (listener is null) throw new ArgumentNullException(nameof(listener));
        lock (_gate)
        {
            if (!_listeners.Contains(listener)) _listeners.Add(listener);
        }
        return new Unsubscriber(this, listener);
    }

    /// <summary>当前运行的中止令牌；无运行时 null。</summary>
    public CancellationToken? Signal
    {
        get
        {
            lock (_gate)
            {
                return _activeRun?.Cancellation.Token;
            }
        }
    }

    /// <summary>中止当前运行（若有）。</summary>
    public void Abort()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _activeRun?.Cancellation;
        }
        cancellation?.Cancel();
    }

    /// <summary>等待当前运行与全部事件监听器结束（agent_end listener 结算后才完成）。</summary>
    public Task WaitForIdle()
    {
        lock (_gate)
        {
            return _activeRun?.Promise.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>清空对话状态与队列，仅保留折叠后的 system 基线。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_activeRun is not null)
            {
                throw new InvalidOperationException(
                    "Agent is already processing. Wait for completion before resetting.");
            }

            var baseline = AgentTranscript.GetCurrentSystemMessage(_state.Messages);
            _state.ReplaceMessages(baseline is null
                ? Array.Empty<Message>()
                : new Message[] { baseline });
            _state.ClearRuntime();
            _steeringQueue.Clear();
            _followUpQueue.Clear();
        }
    }

    private bool HasActiveRun()
    {
        lock (_gate) return _activeRun is not null;
    }

    private static Task<IReadOnlyList<Message>> DefaultConvertToLlm(IReadOnlyList<Message> messages)
        => Task.FromResult<IReadOnlyList<Message>>(messages
            .Where(message => message.Role is "system" or "user" or "assistant" or "toolResult")
            .ToList());

    private async Task RunWithLifecycle(Func<CancellationToken, Task> executor)
    {
        ActiveRun run;
        lock (_gate)
        {
            if (_activeRun is not null)
            {
                throw new InvalidOperationException("Agent is already processing.");
            }

            run = new ActiveRun
            {
                Cancellation = new CancellationTokenSource(),
                Promise = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _activeRun = run;
            _state.SetIsStreaming(true);
            _state.SetStreamingMessage(null);
            _state.SetErrorMessage(null);
        }

        try
        {
            await executor(run.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await HandleRunFailure(error, run.Cancellation.IsCancellationRequested)
                .ConfigureAwait(false);
        }
        finally
        {
            FinishRun(run);
        }
    }

    private async Task HandleRunFailure(Exception error, bool aborted)
    {
        var model = _state.Model;
        var failure = new AssistantMessage
        {
            Content = new ContentBlock[] { new TextContent { Text = string.Empty } },
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = aborted ? StopReason.Aborted : StopReason.Error,
            ErrorMessage = error.Message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await ProcessEvents(new AgentEvent.MessageStart(failure)).ConfigureAwait(false);
        await ProcessEvents(new AgentEvent.MessageEnd(failure)).ConfigureAwait(false);
        await ProcessEvents(new AgentEvent.TurnEnd(failure, Array.Empty<ToolResultMessage>()))
            .ConfigureAwait(false);
        await ProcessEvents(new AgentEvent.AgentEnd(new Message[] { failure }))
            .ConfigureAwait(false);
    }

    private void FinishRun(ActiveRun run)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeRun, run)) return;
            _state.SetIsStreaming(false);
            _state.SetStreamingMessage(null);
            _state.ClearPendingToolCalls();
            _activeRun = null;
        }

        run.Cancellation.Dispose();
        run.Promise.TrySetResult(true);
    }

    private async Task ProcessEvents(AgentEvent @event)
    {
        lock (_gate)
        {
            switch (@event)
            {
                case AgentEvent.MessageStart start:
                    _state.SetStreamingMessage(start.Message);
                    break;
                case AgentEvent.MessageUpdate update:
                    _state.SetStreamingMessage(update.Message);
                    break;
                case AgentEvent.MessageEnd end:
                    _state.SetStreamingMessage(null);
                    _state.AddMessage(end.Message);
                    break;
                case AgentEvent.ToolExecutionStart start:
                    _state.AddPendingToolCall(start.ToolCallId);
                    break;
                case AgentEvent.ToolExecutionEnd end:
                    _state.RemovePendingToolCall(end.ToolCallId);
                    break;
                case AgentEvent.TurnEnd turn:
                    if (turn.Message.ErrorMessage is not null)
                    {
                        _state.SetErrorMessage(turn.Message.ErrorMessage);
                    }
                    break;
                case AgentEvent.AgentEnd:
                    _state.SetStreamingMessage(null);
                    break;
            }
        }

        CancellationToken signal;
        Func<AgentEvent, CancellationToken, Task>[] listeners;
        lock (_gate)
        {
            if (_activeRun is null)
            {
                throw new InvalidOperationException("Agent listener invoked outside active run");
            }
            signal = _activeRun.Cancellation.Token;
            listeners = _listeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            await listener(@event, signal).ConfigureAwait(false);
        }
    }

    private sealed class ActiveRun
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public required TaskCompletionSource<bool> Promise { get; init; }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Agent? _owner;
        private readonly Func<AgentEvent, CancellationToken, Task> _listener;

        public Unsubscriber(Agent owner, Func<AgentEvent, CancellationToken, Task> listener)
        {
            _owner = owner;
            _listener = listener;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._gate) owner._listeners.Remove(_listener);
        }
    }
}
```

`src/PiUnityAgent.Core/Agent/Agent.Run.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

public sealed partial class Agent
{
    /// <summary>以文本（可带图片）启动新 prompt。对应 pi prompt(input, images)。</summary>
    public Task Prompt(string input, IReadOnlyList<ImageContent>? images = null)
    {
        var content = new List<ContentBlock> { new TextContent { Text = input } };
        if (images is not null) content.AddRange(images);
        return Prompt(new UserMessage
        {
            Content = MessageContent.From(content),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>以单条消息启动 prompt。</summary>
    public Task Prompt(Message message)
        => Prompt(new Message[] { message });

    /// <summary>以消息批次启动 prompt。</summary>
    public async Task Prompt(IReadOnlyList<Message> messages)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));
        if (HasActiveRun())
        {
            throw new InvalidOperationException(
                "Agent is already processing a prompt. Use steer() or followUp() to queue messages, or wait for completion.");
        }

        await RunPromptMessages(messages).ConfigureAwait(false);
    }

    private Task RunPromptMessages(
        IReadOnlyList<Message> messages,
        bool skipInitialSteeringPoll = false)
        => RunWithLifecycle(signal => AgentLoop.RunAsync(
            messages,
            CreateContextSnapshot(),
            CreateLoopConfig(skipInitialSteeringPoll),
            ProcessEvents,
            signal,
            StreamFunction));

    private Task RunContinuation()
        => RunWithLifecycle(signal => AgentLoop.RunContinueAsync(
            CreateContextSnapshot(),
            CreateLoopConfig(),
            ProcessEvents,
            signal,
            StreamFunction));

    private AgentContext CreateContextSnapshot() => new()
    {
        Messages = _state.Messages.ToList(),
        Tools = _state.Tools.ToList(),
    };

    private AgentLoopConfig CreateLoopConfig(bool skipInitialSteeringPoll = false)
    {
        var thinkingLevel = _state.ThinkingLevel;
        var streamOptions = new SimpleStreamOptions
        {
            SessionId = SessionId,
            OnPayload = OnPayload,
            OnResponse = OnResponse,
            OnProviderStreamEvent = OnProviderStreamEvent,
            Transport = Transport,
            ThinkingBudgets = ThinkingBudgets,
            MaxRetryDelayMs = MaxRetryDelayMs,
            Reasoning = thinkingLevel == ModelThinkingLevel.Off
                ? null
                : ToThinkingLevel(thinkingLevel),
        };

        return new AgentLoopConfig
        {
            Model = _state.Model,
            ConvertToLlm = ConvertToLlm,
            TransformContext = TransformContext,
            GetApiKey = GetApiKey is null
                ? null
                : (provider, _) => GetApiKey(provider),
            FinishTurn = FinishTurn,
            PrepareRequest = PrepareRequest,
            PrepareNextTurn = null,
            ToolExecution = ToolExecution,
            BeforeToolCall = BeforeToolCall,
            AfterToolCall = AfterToolCall,
            StreamOptions = streamOptions,
            Identity = Identity,
            GetSteeringMessages = null,
            GetFollowUpMessages = null,
        };
    }

    /// <summary>ModelThinkingLevel → 流选项 ThinkingLevel（Off 不映射，由调用点归一为 null）。</summary>
    private static ThinkingLevel ToThinkingLevel(ModelThinkingLevel level) => level switch
    {
        ModelThinkingLevel.Minimal => ThinkingLevel.Minimal,
        ModelThinkingLevel.Low => ThinkingLevel.Low,
        ModelThinkingLevel.Medium => ThinkingLevel.Medium,
        ModelThinkingLevel.High => ThinkingLevel.High,
        ModelThinkingLevel.XHigh => ThinkingLevel.XHigh,
        ModelThinkingLevel.Max => ThinkingLevel.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Off has no provider-level ThinkingLevel."),
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --filter FullyQualifiedName~AgentLifecycleTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PiUnityAgent.Core/Agent/AgentOptions.cs src/PiUnityAgent.Core/Agent/Agent.cs src/PiUnityAgent.Core/Agent/Agent.Run.cs tests/PiUnityAgent.Core.Tests/Agent/AgentLifecycleTests.cs
git commit -m "feat(s8): Agent 门面核心——状态/订阅/单飞/失败收场/Prompt"
```

---

### Task 4: 双队列 API + QueueMode 排空 + Continue

**Files:**
- Create: `src/PiUnityAgent.Core/Agent/Agent.Queues.cs`
- Modify: `src/PiUnityAgent.Core/Agent/Agent.Run.cs`（`CreateLoopConfig` 接队列）
- Modify: `tests/PiUnityAgent.Core.Tests/Agent/AgentQueueTests.cs`（顶部补 `using PiUnityAgent.Ai.Providers;`，追加队列集成 + Continue 测试）

**Interfaces:**
- Consumes: Task 3 的 `Agent` 字段/`RunPromptMessages`/`RunContinuation`/`HasActiveRun`；Task 1 队列。
- Produces: `SteeringMode`/`FollowUpMode`、`Steer`/`FollowUp`/`Clear*`/`HasQueuedMessages`/`PeekQueuedMessages`、`Continue`。

- [ ] **Step 1: Write the failing test**

先在 `tests/PiUnityAgent.Core.Tests/Agent/AgentQueueTests.cs` 顶部补充 `using PiUnityAgent.Ai.Providers;`（现有文件缺少 Faux 类型命名空间），再在文件末尾追加：

```csharp
public class AgentQueueIntegrationTests
{
    private static Agent NewAgent(FauxProvider faux, QueueMode mode)
        => new(new AgentOptions
        {
            InitialState = new AgentInitialState { Model = faux.GetModel() },
            StreamFn = faux.Provider.Stream,
            SteeringMode = mode,
            FollowUpMode = mode,
        });

    private static Message M(string text)
        => new UserMessage { Content = MessageContent.From(text), Timestamp = 1 };

    [Fact]
    public async Task OneAtATime_Injects_One_Steering_Message_Per_Drain()
    {
        var faux = new FauxProvider();
        faux.SetResponses(
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))));
        var agent = NewAgent(faux, QueueMode.OneAtATime);
        agent.Steer(M("s1"));
        agent.Steer(M("s2"));

        await agent.Prompt(M("p"));

        var userTexts = agent.State.Messages.OfType<UserMessage>()
            .Select(message => Assert.IsType<TextContent>(message.Content.ToBlocks()[0]).Text)
            .ToList();
        Assert.Equal(new[] { "p", "s1", "s2" }, userTexts);
        Assert.Equal(2, agent.State.Messages.Count(message => message is AssistantMessage));
        Assert.False(agent.HasQueuedMessages());
    }

    [Fact]
    public async Task All_Injects_Every_Steering_Message_In_One_Drain()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))));
        var agent = NewAgent(faux, QueueMode.All);
        agent.Steer(M("s1"));
        agent.Steer(M("s2"));

        await agent.Prompt(M("p"));

        var userTexts = agent.State.Messages.OfType<UserMessage>()
            .Select(message => Assert.IsType<TextContent>(message.Content.ToBlocks()[0]).Text)
            .ToList();
        Assert.Equal(new[] { "p", "s1", "s2" }, userTexts);
        Assert.Single(agent.State.Messages.OfType<AssistantMessage>());
    }

    [Fact]
    public async Task FollowUp_Runs_Only_After_Agent_Would_Stop()
    {
        var faux = new FauxProvider();
        faux.SetResponses(
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))));
        var agent = NewAgent(faux, QueueMode.OneAtATime);
        agent.FollowUp(M("f1"));

        await agent.Prompt(M("p"));

        var assistantCount = agent.State.Messages.Count(message => message is AssistantMessage);
        Assert.Equal(2, assistantCount);
        Assert.Contains(agent.State.Messages.OfType<UserMessage>(),
            message => Assert.IsType<TextContent>(message.Content.ToBlocks()[0]).Text == "f1");
    }

    [Fact]
    public async Task Continue_From_Assistant_Drains_Steering_First()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))));
        var agent = NewAgent(faux, QueueMode.OneAtATime);
        await agent.Prompt(M("p"));
        agent.Steer(M("s"));

        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))));
        await agent.Continue();

        var userTexts = agent.State.Messages.OfType<UserMessage>()
            .Select(message => Assert.IsType<TextContent>(message.Content.ToBlocks()[0]).Text)
            .ToList();
        Assert.Equal(new[] { "p", "s" }, userTexts);
    }

    [Fact]
    public async Task Continue_From_Assistant_Without_Queues_Throws_Precise_Message()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))));
        var agent = NewAgent(faux, QueueMode.OneAtATime);
        await agent.Prompt(M("p"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Continue());
        Assert.Equal("Cannot continue from message role: assistant", error.Message);
    }

    [Fact]
    public void PeekQueuedMessages_Prefers_Steering()
    {
        var agent = NewAgent(new FauxProvider(), QueueMode.OneAtATime);
        agent.FollowUp(M("f"));
        agent.Steer(M("s"));
        var peeked = Assert.Single(agent.PeekQueuedMessages());
        Assert.Equal("s", Assert.IsType<TextContent>(
            Assert.IsType<UserMessage>(peeked).Content.ToBlocks()[0]).Text);

        agent.ClearAllQueues();
        Assert.False(agent.HasQueuedMessages());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --filter FullyQualifiedName~AgentQueueIntegrationTests`
Expected: compile error: queue APIs/`Continue` not found.

- [ ] **Step 3: Write minimal implementation**

`src/PiUnityAgent.Core/Agent/Agent.Queues.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Agent;

public sealed partial class Agent
{
    /// <summary>steering 队列的排空模式。</summary>
    public QueueMode SteeringMode
    {
        get => _steeringQueue.Mode;
        set => _steeringQueue.Mode = value;
    }

    /// <summary>follow-up 队列的排空模式。</summary>
    public QueueMode FollowUpMode
    {
        get => _followUpQueue.Mode;
        set => _followUpQueue.Mode = value;
    }

    /// <summary>排队一条消息：在当前助手回合结束后注入。</summary>
    public void Steer(Message message) => _steeringQueue.Enqueue(message);

    /// <summary>排队一条消息：仅在 agent 本应停止时运行。</summary>
    public void FollowUp(Message message) => _followUpQueue.Enqueue(message);

    public void ClearSteeringQueue() => _steeringQueue.Clear();

    public void ClearFollowUpQueue() => _followUpQueue.Clear();

    public void ClearAllQueues()
    {
        ClearSteeringQueue();
        ClearFollowUpQueue();
    }

    public bool HasQueuedMessages()
        => _steeringQueue.HasItems() || _followUpQueue.HasItems();

    /// <summary>预览下一回合将选取的消息（steering 优先）。</summary>
    public IReadOnlyList<Message> PeekQueuedMessages()
    {
        var steering = _steeringQueue.Peek();
        return steering.Count > 0 ? steering : _followUpQueue.Peek();
    }

    /// <summary>
    /// 从当前 transcript 续跑。最后一条须为 user 或 toolResult；
    /// 最后一条为 assistant 时先排空 steering，再排空 follow-up。
    /// </summary>
    public async Task Continue()
    {
        if (HasActiveRun())
        {
            throw new InvalidOperationException(
                "Agent is already processing. Wait for completion before continuing.");
        }

        var messages = _state.Messages;
        var lastMessage = messages.Count > 0 ? messages[messages.Count - 1] : null;
        if (lastMessage is null || messages.All(message => message is SystemMessage))
        {
            throw new InvalidOperationException("No messages to continue from");
        }

        if (lastMessage is AssistantMessage)
        {
            var steering = _steeringQueue.Drain();
            if (steering.Count > 0)
            {
                await RunPromptMessages(steering, skipInitialSteeringPoll: true)
                    .ConfigureAwait(false);
                return;
            }

            var followUps = _followUpQueue.Drain();
            if (followUps.Count > 0)
            {
                await RunPromptMessages(followUps).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("Cannot continue from message role: assistant");
        }

        await RunContinuation().ConfigureAwait(false);
    }
}
```

修改 `Agent.Run.cs` 的 `CreateLoopConfig`：把 `skipInitialSteeringPoll` 局部变量与两个队列委托接上。替换该方法中：

```csharp
        return new AgentLoopConfig
        {
            Model = _state.Model,
            ConvertToLlm = ConvertToLlm,
            TransformContext = TransformContext,
            GetApiKey = GetApiKey is null
                ? null
                : (provider, _) => GetApiKey(provider),
            FinishTurn = FinishTurn,
            PrepareRequest = PrepareRequest,
            PrepareNextTurn = null,
            ToolExecution = ToolExecution,
            BeforeToolCall = BeforeToolCall,
            AfterToolCall = AfterToolCall,
            StreamOptions = streamOptions,
            Identity = Identity,
            GetSteeringMessages = null,
            GetFollowUpMessages = null,
        };
```

为：

```csharp
        var skipPoll = skipInitialSteeringPoll;
        return new AgentLoopConfig
        {
            Model = _state.Model,
            ConvertToLlm = ConvertToLlm,
            TransformContext = TransformContext,
            GetApiKey = GetApiKey is null
                ? null
                : (provider, _) => GetApiKey(provider),
            FinishTurn = FinishTurn,
            PrepareRequest = PrepareRequest,
            PrepareNextTurn = null,
            ToolExecution = ToolExecution,
            BeforeToolCall = BeforeToolCall,
            AfterToolCall = AfterToolCall,
            StreamOptions = streamOptions,
            Identity = Identity,
            GetSteeringMessages = () =>
            {
                if (skipPoll)
                {
                    skipPoll = false;
                    return Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
                }
                return Task.FromResult(_steeringQueue.Drain());
            },
            GetFollowUpMessages = () => Task.FromResult(_followUpQueue.Drain()),
        };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --filter FullyQualifiedName~AgentQueue`
Expected: PASS（队列单测 + 集成测试）。

- [ ] **Step 5: Commit**

```bash
git add src/PiUnityAgent.Core/Agent/Agent.Queues.cs src/PiUnityAgent.Core/Agent/Agent.Run.cs tests/PiUnityAgent.Core.Tests/Agent/AgentQueueTests.cs
git commit -m "feat(s8): 双队列 API/QueueMode 排空/Continue（D23）"
```

---

### Task 5: prepareNextTurn 门面包装 / DefaultStreamFn / 跨线程控制

**Files:**
- Modify: `src/PiUnityAgent.Core/Agent/Agent.Run.cs`（`CreateLoopConfig` 接 `CreateLoopPrepareNextTurn`）
- Test: `tests/PiUnityAgent.Core.Tests/Agent/AgentFacadeTests.cs`
- Test: `tests/PiUnityAgent.Core.Tests/Agent/AgentThreadingTests.cs`

**Interfaces:**
- Consumes: Task 3 `Agent`；S7 `AgentLoopTurnUpdate`、`AgentTurnContext`、`DefaultStreamFn`。
- Produces: `Agent` 门面 `prepareNextTurn`/`prepareNextTurnWithContext` 的实际接线；`DefaultStreamFn` 兜底行为。

- [ ] **Step 1: Write the failing test**

`tests/PiUnityAgent.Core.Tests/Agent/AgentFacadeTests.cs`：

```csharp
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;

namespace PiUnityAgent.Core.Tests.Agent;

public class AgentFacadeTests
{
    private static Agent NewAgent(FauxProvider faux, AgentOptions? options = null)
        => new(options is null
            ? new AgentOptions
            {
                InitialState = new AgentInitialState { Model = faux.GetModel() },
                StreamFn = faux.Provider.Stream,
            }
            : options with
            {
                InitialState = options.InitialState ?? new AgentInitialState { Model = faux.GetModel() },
                StreamFn = options.StreamFn ?? faux.Provider.Stream,
            });

    [Fact]
    public async Task PrepareNextTurnWithContext_Receives_Context_And_Can_Update_Thinking()
    {
        var faux = new FauxProvider();
        faux.SetResponses(
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))));
        PrepareNextTurnContext? seen = null;
        CancellationToken seenSignal = default;

        var agent = NewAgent(faux, new AgentOptions
        {
            InitialState = new AgentInitialState { Model = faux.GetModel() },
            StreamFn = faux.Provider.Stream,
            PrepareNextTurnWithContext = (context, signal) =>
            {
                seen = context;
                seenSignal = signal;
                return Task.FromResult<AgentLoopTurnUpdate?>(new AgentLoopTurnUpdate
                {
                    ThinkingLevel = ModelThinkingLevel.Low,
                });
            },
        });
        agent.FollowUp(new UserMessage { Content = MessageContent.From("f"), Timestamp = 1 });

        await agent.Prompt("p");

        Assert.NotNull(seen);
        Assert.Equal(2, seen!.NewMessages.Count);
        Assert.NotEqual(default, seenSignal);
    }

    [Fact]
    public async Task PrepareNextTurn_Signal_Overload_Is_Called_With_Run_Token()
    {
        var faux = new FauxProvider();
        faux.SetResponses(
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("one"))),
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("two"))));
        CancellationToken seen = default;
        var agent = NewAgent(faux, new AgentOptions
        {
            InitialState = new AgentInitialState { Model = faux.GetModel() },
            StreamFn = faux.Provider.Stream,
            PrepareNextTurn = signal =>
            {
                seen = signal;
                return Task.FromResult<AgentLoopTurnUpdate?>(null);
            },
        });
        agent.FollowUp(new UserMessage { Content = MessageContent.From("f"), Timestamp = 1 });

        await agent.Prompt("p");

        Assert.NotEqual(default, seen);
    }

    [Fact]
    public async Task Omitted_StreamFn_Falls_Back_To_DefaultStreamFn()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("ok"))));
        DefaultStreamFn.Set(faux.Provider.Stream);
        try
        {
            var agent = new Agent(new AgentOptions
            {
                InitialState = new AgentInitialState { Model = faux.GetModel() },
            });
            await agent.Prompt("p");
            Assert.Equal(StopReason.Stop,
                Assert.IsType<AssistantMessage>(agent.State.Messages[^1]).StopReason);
        }
        finally
        {
            DefaultStreamFn.Set(null);
        }
    }

    [Fact]
    public void Missing_Default_StreamFn_Throws_Pi_Text()
    {
        DefaultStreamFn.Set(null);
        var error = Assert.Throws<InvalidOperationException>(
            () => new Agent(new AgentOptions()));
        Assert.Equal(
            "No default stream function configured. Pass streamFn explicitly or call setDefaultStreamFn().",
            error.Message);
    }
}
```

`tests/PiUnityAgent.Core.Tests/Agent/AgentThreadingTests.cs`：

```csharp
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Agent;

namespace PiUnityAgent.Core.Tests.Agent;

public class AgentThreadingTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Steer_Abort_From_Other_Threads_Are_Safe_And_Effective()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Factory(async (_, options, _, _) =>
        {
            await Task.Delay(Timeout.Infinite, options?.CancellationToken ?? CancellationToken.None);
            throw new InvalidOperationException("unreachable");
        }));
        var agent = new Agent(new AgentOptions
        {
            InitialState = new AgentInitialState { Model = faux.GetModel() },
            StreamFn = faux.Provider.Stream,
        });

        var running = agent.Prompt(new UserMessage
        {
            Content = MessageContent.From("p"),
            Timestamp = 1,
        });
        await WaitUntilAsync(() => agent.State.IsStreaming);

        await Task.Run(() => agent.Steer(new UserMessage
        {
            Content = MessageContent.From("s"),
            Timestamp = 2,
        }));
        await Task.Run(agent.Abort);

        await running;
        await agent.WaitForIdle();
        Assert.False(agent.State.IsStreaming);
        // 队列内容是否被排空取决于与初始 steering 轮询的竞争；本测试只冒烟锁/取消安全。
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --filter "FullyQualifiedName~AgentFacadeTests|FullyQualifiedName~AgentThreadingTests"`
Expected: `PrepareNextTurnWithContext`/`PrepareNextTurn` 回调未被调用，断言失败；DefaultStreamFn 兜底已存在应通过。

- [ ] **Step 3: Write minimal implementation**

在 `Agent.Run.cs` 中替换 `CreateLoopConfig` 的这一行：

```csharp
            PrepareNextTurn = null,
```

为：

```csharp
            PrepareNextTurn = CreateLoopPrepareNextTurn(),
```

并在 `CreateLoopConfig` 之后追加方法：

```csharp
    private PrepareNextTurn? CreateLoopPrepareNextTurn()
    {
        if (PrepareNextTurnWithContext is null && PrepareNextTurn is null) return null;

        return async turn =>
        {
            var signal = Signal ?? CancellationToken.None;
            if (PrepareNextTurnWithContext is not null)
            {
                return await PrepareNextTurnWithContext(
                    new PrepareNextTurnContext(
                        turn.Message,
                        turn.ToolResults,
                        turn.Context,
                        turn.NewMessages),
                    signal).ConfigureAwait(false);
            }

            return await PrepareNextTurn!(signal).ConfigureAwait(false);
        };
    }
```

> 注意：这里的 `PrepareNextTurn` 是 `Agent` 门面的 `Func<CancellationToken, Task<AgentLoopTurnUpdate?>>`，不是 S7 loop 层的 `PrepareNextTurn` delegate。C# 属性名与类型名处于不同命名空间，不会冲突。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj --filter "FullyQualifiedName~AgentFacadeTests|FullyQualifiedName~AgentThreadingTests"`
Expected: PASS（5 tests）。

- [ ] **Step 5: Commit**

```bash
git add src/PiUnityAgent.Core/Agent/Agent.Run.cs tests/PiUnityAgent.Core.Tests/Agent/AgentFacadeTests.cs tests/PiUnityAgent.Core.Tests/Agent/AgentThreadingTests.cs
git commit -m "feat(s8): prepareNextTurn 门面包装 + DefaultStreamFn 兜底 + 跨线程控制"
```

---

### Task 6: D3 provider 能力面（IDeferredProvider / 工厂 / 静态 provider）

**Files:**
- Modify: `src/PiUnityAgent.Ai/Providers/ProviderCapabilities.cs`
- Modify: `src/PiUnityAgent.Ai/Providers/ProviderFactory.cs`
- Modify: `src/PiUnityAgent.Ai/Providers/StaticProvider.cs`
- Test: `tests/PiUnityAgent.Ai.Tests/Providers/DeferredTests.cs`

**Interfaces:**
- Consumes: S3 `ProviderStreams.FetchDeferred/CancelDeferred`、`DeferredHandle`、`DeferredFetchOptions/DeferredCancelOptions`。
- Produces: `IDeferredProvider.SupportsFetchDeferred/SupportsCancelDeferred`；`CreatedProvider`/`StaticProvider` 的 deferred 分派。

- [ ] **Step 1: Write the failing test**

`tests/PiUnityAgent.Ai.Tests/Providers/DeferredTests.cs`（先写 provider 能力面部分）：

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class DeferredProviderCapabilityTests
{
    private static Model Model()
        => ProviderTestSupport.CreateModel("m1", "p1");

    private static IAssistantMessageEventStream Done(Model model, StopReason reason, DeferredHandle? handle = null)
    {
        var stream = new AssistantMessageEventStream();
        var message = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = reason,
            Deferred = handle,
            Timestamp = 1,
        };
        stream.Push(new AssistantMessageEvent.Done(reason, message));
        stream.End(message);
        return stream;
    }

    private static DeferredHandle Handle(string id = "d1") => new()
    {
        Provider = "p1",
        ModelId = "m1",
        Api = "faux",
        Id = id,
    };

    [Fact]
    public void StaticProvider_Reports_Capability_From_Delegates_And_Throws_When_Missing()
    {
        var model = Model();
        var without = ProviderTestSupport.CreateProvider("p1");
        Assert.False(Assert.IsAssignableFrom<IDeferredProvider>(without).SupportsFetchDeferred);
        Assert.False(Assert.IsAssignableFrom<IDeferredProvider>(without).SupportsCancelDeferred);

        var with = new StaticProvider(
            "p1", "p1", new[] { model }, ProviderTestSupport.DoneStream,
            ProviderTestSupport.KeylessAuth(),
            fetchDeferred: (requestModel, handle, options) => Done(requestModel, StopReason.Deferred, handle),
            cancelDeferred: (requestModel, handle, options, signal) => Task.CompletedTask);

        var deferred = Assert.IsAssignableFrom<IDeferredProvider>(with);
        Assert.True(deferred.SupportsFetchDeferred);
        Assert.True(deferred.SupportsCancelDeferred);
        var message = Assert.IsType<AssistantMessage>(deferred.StreamDeferred(model, Handle(), null)
            .WaitForDoneAsync().Result);
        Assert.Equal(StopReason.Deferred, message.StopReason);
        Assert.Equal("d1", message.Deferred!.Id);
    }

    [Fact]
    public void FactoryProvider_Exposes_Deferred_From_Api_Streams()
    {
        var model = Model();
        var capturedHandle = Handle("d2");
        var options = new CreateProviderOptions
        {
            Id = "p1",
            Auth = ProviderTestSupport.KeylessAuth(),
            Models = new ProviderModel[] { model },
            Api = new ProviderStreams
            {
                Stream = ProviderTestSupport.DoneStream,
                StreamSimple = (requestModel, context, streamOptions) =>
                    ProviderTestSupport.DoneStream(requestModel, context, streamOptions),
                FetchDeferred = (requestModel, handle, fetchOptions) =>
                    Done(requestModel, StopReason.Deferred, handle),
                CancelDeferred = (requestModel, handle, cancelOptions, signal) =>
                {
                    capturedHandle = handle;
                    return Task.CompletedTask;
                },
            },
        };

        var provider = ProviderFactory.Create(options);
        var deferred = Assert.IsAssignableFrom<IDeferredProvider>(provider);
        Assert.True(deferred.SupportsFetchDeferred);
        Assert.True(deferred.SupportsCancelDeferred);

        var message = Assert.IsType<AssistantMessage>(deferred.StreamDeferred(model, Handle("d2"), null)
            .WaitForDoneAsync().Result);
        Assert.Equal(StopReason.Deferred, message.StopReason);

        deferred.CancelDeferredAsync(model, Handle("d3"), null).GetAwaiter().GetResult();
        Assert.Equal("d3", capturedHandle.Id);
    }

    [Fact]
    public void FactoryProvider_Without_Deferred_Reports_Unsupported()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p1",
            Auth = ProviderTestSupport.KeylessAuth(),
            Models = new ProviderModel[] { Model() },
            Api = new ProviderStreams
            {
                Stream = ProviderTestSupport.DoneStream,
                StreamSimple = (requestModel, context, streamOptions) =>
                    ProviderTestSupport.DoneStream(requestModel, context, streamOptions),
            },
        });

        var deferred = Assert.IsAssignableFrom<IDeferredProvider>(provider);
        Assert.False(deferred.SupportsFetchDeferred);
        Assert.False(deferred.SupportsCancelDeferred);
        Assert.Throws<ModelsError>(() => deferred.StreamDeferred(Model(), Handle(), null));
        Assert.ThrowsAsync<ModelsError>(() => deferred.CancelDeferredAsync(Model(), Handle(), null));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~DeferredProviderCapabilityTests`
Expected: compile error: `IDeferredProvider` 无 `Supports*`；`StaticProvider` 构造器无 deferred 参数。

- [ ] **Step 3: Write minimal implementation**

`ProviderCapabilities.cs` 的 `IDeferredProvider` 改为：

```csharp
/// <summary>Deferred 响应能力；行为在 S8/D3 落地。</summary>
public interface IDeferredProvider
{
    /// <summary>任一 api 实现导出 fetchDeferred 时为 true；pi 以方法存在性表达。</summary>
    bool SupportsFetchDeferred { get; }

    /// <summary>任一 api 实现导出 cancelDeferred 时为 true。</summary>
    bool SupportsCancelDeferred { get; }

    IAssistantMessageEventStream StreamDeferred(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options);

    Task<AssistantMessage> FetchDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options);

    Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options);
}
```

`ProviderFactory.cs`：
1. 类声明加接口：

```csharp
    private class CreatedProvider : IProvider, IFilterableProvider, IImagesProvider, IClassifierProvider, IDeferredProvider
```

2. 在 `ApiFor` 之前插入：

```csharp
        public bool SupportsFetchDeferred => _single?.FetchDeferred is not null
            || (_byApi?.Values.Any(streams => streams.FetchDeferred is not null) ?? false);

        public bool SupportsCancelDeferred => _single?.CancelDeferred is not null
            || (_byApi?.Values.Any(streams => streams.CancelDeferred is not null) ?? false);

        public IAssistantMessageEventStream StreamDeferred(
            Model model,
            DeferredHandle handle,
            DeferredFetchOptions? options)
        {
            var streams = ApiFor(model);
            if (streams?.FetchDeferred is null)
            {
                throw new ModelsError(
                    ModelsErrorCode.Provider,
                    $"Provider {_options.Id} does not support deferred responses");
            }

            return streams.FetchDeferred(model, handle, options);
        }

        public Task<AssistantMessage> FetchDeferredAsync(
            Model model,
            DeferredHandle handle,
            DeferredFetchOptions? options)
            => StreamDeferred(model, handle, options)
                .WaitForDoneAsync(options?.CancellationToken ?? default);

        public Task CancelDeferredAsync(
            Model model,
            DeferredHandle handle,
            DeferredCancelOptions? options)
        {
            var streams = ApiFor(model);
            if (streams?.CancelDeferred is null)
            {
                throw new ModelsError(
                    ModelsErrorCode.Provider,
                    $"Provider {_options.Id} does not support deferred responses");
            }

            return streams.CancelDeferred(
                model,
                handle,
                options,
                options?.CancellationToken ?? default);
        }
```

`StaticProvider.cs`：
1. 类声明 `: IProvider, IDeferredProvider`；
2. 字段与构造器追加：

```csharp
    private readonly Func<Model, DeferredHandle, DeferredFetchOptions?, IAssistantMessageEventStream>? _fetchDeferred;
    private readonly Func<Model, DeferredHandle, DeferredCancelOptions?, CancellationToken, Task>? _cancelDeferred;
```

构造器参数末尾追加：

```csharp
        Func<Model, DeferredHandle, DeferredFetchOptions?, IAssistantMessageEventStream>? fetchDeferred = null,
        Func<Model, DeferredHandle, DeferredCancelOptions?, CancellationToken, Task>? cancelDeferred = null
```

构造器体追加：

```csharp
        _fetchDeferred = fetchDeferred;
        _cancelDeferred = cancelDeferred;
```

构造函数末尾追加接口实现：

```csharp
    public bool SupportsFetchDeferred => _fetchDeferred is not null;
    public bool SupportsCancelDeferred => _cancelDeferred is not null;

    public IAssistantMessageEventStream StreamDeferred(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options)
        => _fetchDeferred?.Invoke(model, handle, options)
           ?? throw new ModelsError(
               ModelsErrorCode.Provider,
               $"Provider {Id} does not support deferred responses");

    public Task<AssistantMessage> FetchDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options)
        => StreamDeferred(model, handle, options)
            .WaitForDoneAsync(options?.CancellationToken ?? default);

    public Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options)
        => _cancelDeferred?.Invoke(model, handle, options, options?.CancellationToken ?? default)
           ?? throw new ModelsError(
               ModelsErrorCode.Provider,
               $"Provider {Id} does not support deferred responses");
```

需要 `using System.Threading;` / `using System.Threading.Tasks;`。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~DeferredProviderCapabilityTests`
Expected: PASS（3 tests）。

- [ ] **Step 5: Commit**

```bash
git add src/PiUnityAgent.Ai/Providers/ProviderCapabilities.cs src/PiUnityAgent.Ai/Providers/ProviderFactory.cs src/PiUnityAgent.Ai/Providers/StaticProvider.cs tests/PiUnityAgent.Ai.Tests/Providers/DeferredTests.cs
git commit -m "feat(s8): IDeferredProvider 能力面与工厂/静态 provider 分派（D3）"
```

---

### Task 7: D3 Models deferred 管线与 auth 接线

**Files:**
- Modify: `src/PiUnityAgent.Ai/Providers/Models.Capabilities.cs`
- Modify: `src/PiUnityAgent.Ai/Providers/Models.Streaming.cs`（DeferredFetchOptions/DeferredCancelOptions 的 auth/identity/transformHeaders 重载）
- Test: `tests/PiUnityAgent.Ai.Tests/Providers/DeferredTests.cs`（追加 Models 测试）

**Interfaces:**
- Consumes: Task 6 `IDeferredProvider` 能力面；S3 `ResolveAuthForStreamAsync`/`ApplyAuthOptions`/`ApplyIdentity`/`ApplyTransformHeaders`。
- Produces: `Models.StreamDeferred/FetchDeferredAsync/CancelDeferredAsync` 真实现；不支持时 `ModelsError(Provider)`。

- [ ] **Step 1: Write the failing test（追加到 `DeferredTests.cs`）**

```csharp
public class DeferredModelsTests
{
    private static Model Model() => ProviderTestSupport.CreateModel("m1", "p1");

    private static DeferredHandle Handle(string id = "d1") => new()
    {
        Provider = "p1",
        ModelId = "m1",
        Api = "faux",
        Id = id,
    };

    private static IAssistantMessageEventStream Done(Model model, StopReason reason)
    {
        var stream = new AssistantMessageEventStream();
        var message = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = reason,
            Timestamp = 1,
        };
        stream.Push(new AssistantMessageEvent.Done(reason, message));
        stream.End(message);
        return stream;
    }

    [Fact]
    public async Task Unsupported_Provider_Becomes_Error_Terminal_And_Cancel_Throws()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider("p1"));
        var handle = Handle();

        var final = await models.StreamDeferred(Model(), handle).WaitForDoneAsync();
        Assert.Equal(StopReason.Error, final.StopReason);
        Assert.Equal("Provider p1 does not support deferred responses", final.ErrorMessage);

        var fetched = await models.FetchDeferredAsync(Model(), handle);
        Assert.Equal(StopReason.Error, fetched.StopReason);
        Assert.Equal("Provider p1 does not support deferred responses", fetched.ErrorMessage);

        var error = await Assert.ThrowsAsync<ModelsError>(
            () => models.CancelDeferredAsync(Model(), handle));
        Assert.Equal(ModelsErrorCode.Provider, error.Code);
        Assert.Equal("Provider p1 does not support deferred responses", error.Message);
    }

    [Fact]
    public async Task Auth_BaseUrl_Overlays_Before_Provider_Fetch()
    {
        Model? seen = null;
        var auth = new PiUnityAgent.Ai.Providers.Auth.ProviderAuth
        {
            ApiKey = new PiUnityAgent.Ai.Providers.Auth.ApiKeyAuth
            {
                Name = "keyless",
                Resolve = (_, _) => Task.FromResult<PiUnityAgent.Ai.Providers.Auth.AuthResult?>(
                    new PiUnityAgent.Ai.Providers.Auth.AuthResult(
                        new PiUnityAgent.Ai.Providers.Auth.ModelAuth
                        {
                            BaseUrl = "https://proxy.example/v1",
                        },
                        null,
                        "test")),
            },
        };

        var model = Model();
        var provider = new StaticProvider(
            "p1", "p1", new[] { model }, ProviderTestSupport.DoneStream, auth,
            fetchDeferred: (requestModel, handle, options) =>
            {
                seen = requestModel;
                return Done(requestModel, StopReason.Deferred);
            });

        var models = new Models();
        models.SetProvider(provider);
        await models.StreamDeferred(model, Handle()).WaitForDoneAsync();

        Assert.NotNull(seen);
        Assert.Equal("https://proxy.example/v1", seen!.BaseUrl);
    }

    [Fact]
    public async Task FetchDeferredAsync_Returns_Stream_Result()
    {
        var model = Model();
        var provider = new StaticProvider(
            "p1", "p1", new[] { model }, ProviderTestSupport.DoneStream,
            ProviderTestSupport.KeylessAuth(),
            fetchDeferred: (requestModel, handle, options) => Done(requestModel, StopReason.Stop));

        var models = new Models();
        models.SetProvider(provider);
        var message = await models.FetchDeferredAsync(model, Handle());
        Assert.Equal(StopReason.Stop, message.StopReason);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~DeferredModelsTests`
Expected: FAIL with `NotSupportedException: Deferred to S8: deferred responses (D3)`（现有 stub）。

- [ ] **Step 3: Write minimal implementation**

`Models.Capabilities.cs` 改为：

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed partial class Models
{
    public IAssistantMessageEventStream StreamDeferred(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options = null)
    {
        return LazyStream.Run(model, async () =>
        {
            var provider = RequireProvider(model.Provider);
            if (provider is not IDeferredProvider deferred)
            {
                throw new ModelsError(
                    ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support deferred responses");
            }
            if (!deferred.SupportsFetchDeferred)
            {
                throw new ModelsError(
                    ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support deferred responses");
            }

            var cancellationToken = options?.CancellationToken ?? default;
            var auth = await ResolveAuthForStreamAsync(model, options).ConfigureAwait(false);
            var requestModel = auth.Auth.BaseUrl is null
                ? model
                : model with { BaseUrl = auth.Auth.BaseUrl };
            var applied = ApplyAuthOptions(requestModel, options, auth);
            applied = ApplyIdentity(applied);
            applied = await ApplyTransformHeaders(applied, cancellationToken)
                .ConfigureAwait(false);
            return deferred.StreamDeferred(requestModel, handle, applied);
        });
    }

    public Task<AssistantMessage> FetchDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options = null)
        => StreamDeferred(model, handle, options)
            .WaitForDoneAsync(options?.CancellationToken ?? default);

    public async Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options = null)
    {
        var provider = RequireProvider(model.Provider);
        if (provider is not IDeferredProvider deferred)
        {
            throw new ModelsError(
                ModelsErrorCode.Provider,
                $"Provider {model.Provider} does not support deferred responses");
        }
        if (!deferred.SupportsCancelDeferred)
        {
            throw new ModelsError(
                ModelsErrorCode.Provider,
                $"Provider {model.Provider} does not support deferred responses");
        }

        var auth = await ResolveAuthForStreamAsync(model, options).ConfigureAwait(false);
        var requestModel = auth.Auth.BaseUrl is null
            ? model
            : model with { BaseUrl = auth.Auth.BaseUrl };
        var applied = ApplyAuthOptions(requestModel, options, auth);
        await deferred.CancelDeferredAsync(requestModel, handle, applied)
            .ConfigureAwait(false);
    }

    public Task<AssistantImages> GenerateImagesAsync(
        ImageModel model,
        ImagesContext context,
        ImagesOptions? options = null)
        => throw new NotSupportedException("Deferred to 2B: image generation (D4)");

    public Task<ClassifierResult> ClassifyAsync(
        ClassifierModel model,
        ClassifierContext context,
        ClassifierOptions? options = null)
        => throw new NotSupportedException("Deferred to 2B: classification (D5)");
}
```

> 保留原文件中的 `GenerateImagesAsync`/`ClassifyAsync`（D4/D5 stub）不动，只替换三个 deferred 方法。

`Models.Streaming.cs` 末尾（`MergeEnv` 之后、类结束前）追加：

```csharp
    private static DeferredFetchOptions ApplyAuthOptions(
        Model model,
        DeferredFetchOptions? options,
        AuthResult auth)
    {
        options ??= new DeferredFetchOptions();
        var sessionHeaders = ClientIdentityHelpers.GetOpenCodeSessionHeaders(
            new ClientIdentityTarget(model.Provider, model.BaseUrl), options.SessionId) is { } session
            ? session.ToDictionary(entry => entry.Key, entry => (string?)entry.Value)
            : null;
        var headers = MergeHeaders(sessionHeaders, MergeHeaders(auth.Auth.Headers, options.Headers));
        var env = MergeEnv(auth.Env, options.Env);
        return options with
        {
            ApiKey = options.ApiKey is null ? auth.Auth.ApiKey : options.ApiKey,
            Headers = headers,
            Env = env,
        };
    }

    private static DeferredCancelOptions ApplyAuthOptions(
        Model model,
        DeferredCancelOptions? options,
        AuthResult auth)
    {
        options ??= new DeferredCancelOptions();
        var sessionHeaders = ClientIdentityHelpers.GetOpenCodeSessionHeaders(
            new ClientIdentityTarget(model.Provider, model.BaseUrl), options.SessionId) is { } session
            ? session.ToDictionary(entry => entry.Key, entry => (string?)entry.Value)
            : null;
        var headers = MergeHeaders(sessionHeaders, MergeHeaders(auth.Auth.Headers, options.Headers));
        var env = MergeEnv(auth.Env, options.Env);
        return options with
        {
            ApiKey = options.ApiKey is null ? auth.Auth.ApiKey : options.ApiKey,
            Headers = headers,
            Env = env,
        };
    }

    private DeferredFetchOptions ApplyIdentity(DeferredFetchOptions options)
    {
        var identity = ClientIdentityMerger.Merge(_identity, options.Identity);
        return identity is null ? options : options with { Identity = identity };
    }

    private static async Task<DeferredFetchOptions> ApplyTransformHeaders(
        DeferredFetchOptions options,
        CancellationToken cancellationToken)
    {
        var transform = options.TransformHeaders;
        if (transform is null) return options;
        cancellationToken.ThrowIfCancellationRequested();
        var headers = await transform(options.Headers ?? new Dictionary<string, string?>())
            .ConfigureAwait(false);
        return options with { Headers = headers, TransformHeaders = null };
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~DeferredModelsTests`
Expected: PASS（3 tests）。

- [ ] **Step 5: 删除现势钉**

删除 `tests/PiUnityAgent.Ai.Tests/Providers/ModelsCapabilityTests.cs` 中 `Deferred_Methods_Are_Deferred_To_S8` 方法（保留 `Images_And_Classify_Are_Deferred_To_2B`）。

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~ModelsCapabilityTests`
Expected: PASS（仅剩 2B 测试）。

- [ ] **Step 6: Commit**

```bash
git add src/PiUnityAgent.Ai/Providers/Models.Capabilities.cs src/PiUnityAgent.Ai/Providers/Models.Streaming.cs tests/PiUnityAgent.Ai.Tests/Providers/DeferredTests.cs tests/PiUnityAgent.Ai.Tests/Providers/ModelsCapabilityTests.cs
git commit -m "feat(s8): Models deferred 管线/auth 接线；删除 D3 现势钉"
```

---

### Task 8: D3 Faux deferred 全行为

**Files:**
- Modify: `src/PiUnityAgent.Ai/Providers/Faux/Faux.cs`
- Modify: `src/PiUnityAgent.Ai/Providers/Faux/FauxProvider.cs`
- Test: `tests/PiUnityAgent.Ai.Tests/Providers/DeferredTests.cs`（追加 Faux 测试）

**Interfaces:**
- Consumes: Task 6 `IDeferredProvider`；Task 7 `Models` 管线（测试也可直接调 provider）。
- Produces: `FauxDeferredOptions { PendingFetches; PollAfterMs }`；Faux 请求侧 handle、fetch/cancel/final 缓存、`DeferredFetchCount`/`CancelledDeferred`。

- [ ] **Step 1: Write the failing test（追加到 `DeferredTests.cs`）**

```csharp
public class FauxDeferredTests
{
    private static TranscriptContext Context() => new() { Messages = Array.Empty<Message>() };

    [Fact]
    public async Task Deferred_Request_Emits_Handle_And_Fetch_Returns_Final_After_Pending()
    {
        var faux = new FauxProvider(new FauxProviderOptions
        {
            Deferred = new FauxDeferredOptions { PendingFetches = 1, PollAfterMs = 50 },
        });
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("final"))));
        var model = faux.GetModel();

        var stream = faux.Provider.StreamSimple(model, Context(), new SimpleStreamOptions
        {
            Deferred = new DeferredRequest.Enabled("1h"),
        });
        var first = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Deferred, first.StopReason);
        Assert.NotNull(first.Deferred);
        Assert.StartsWith("deferred:", first.Deferred!.Id);
        Assert.Equal(50, first.Deferred!.PollAfterMs);

        var provider = Assert.IsAssignableFrom<IDeferredProvider>(faux.Provider);
        var pending = await provider.FetchDeferredAsync(model, first.Deferred!, null);
        Assert.Equal(StopReason.Deferred, pending.StopReason);
        Assert.Equal(1, faux.State.DeferredFetchCount);

        var final = await provider.FetchDeferredAsync(model, first.Deferred!, null);
        Assert.Equal(StopReason.Stop, final.StopReason);
        Assert.Equal("final", Assert.IsType<TextContent>(final.Content[0]).Text);
        Assert.Equal(2, faux.State.DeferredFetchCount);

        var cached = await provider.FetchDeferredAsync(model, first.Deferred!, null);
        Assert.Equal("final", Assert.IsType<TextContent>(cached.Content[0]).Text);
        Assert.Equal(3, faux.State.DeferredFetchCount);
    }

    [Fact]
    public async Task Cancel_Marks_Handle_And_Fetch_Reports_Cancelled()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("final"))));
        var model = faux.GetModel();

        var first = await faux.Provider.StreamSimple(model, Context(), new SimpleStreamOptions
        {
            Deferred = new DeferredRequest.Enabled(),
        }).WaitForDoneAsync();
        var handle = first.Deferred!;
        var provider = Assert.IsAssignableFrom<IDeferredProvider>(faux.Provider);

        await provider.CancelDeferredAsync(model, handle, null);
        Assert.Single(faux.State.CancelledDeferred);
        Assert.Equal(handle.Id, faux.State.CancelledDeferred[0].Id);

        var fetched = await provider.FetchDeferredAsync(model, handle, null);
        Assert.Equal(StopReason.Error, fetched.StopReason);
        Assert.Equal($"Faux deferred response was cancelled: {handle.Id}", fetched.ErrorMessage);
    }

    [Fact]
    public async Task Unknown_Handle_Reports_Exact_Error()
    {
        var faux = new FauxProvider();
        var model = faux.GetModel();
        var unknown = new DeferredHandle
        {
            Provider = model.Provider,
            ModelId = model.Id,
            Api = model.Api,
            Id = "missing",
        };

        var provider = Assert.IsAssignableFrom<IDeferredProvider>(faux.Provider);
        var fetched = await provider.FetchDeferredAsync(model, unknown, null);
        Assert.Equal(StopReason.Error, fetched.StopReason);
        Assert.Equal("Unknown faux deferred response: missing", fetched.ErrorMessage);
    }

    [Fact]
    public async Task Disabled_Deferred_Produces_Normal_Response()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("normal"))));
        var message = await faux.Provider.StreamSimple(faux.GetModel(), Context(), new SimpleStreamOptions
        {
            Deferred = new DeferredRequest.Disabled(),
        }).WaitForDoneAsync();

        Assert.Equal(StopReason.Stop, message.StopReason);
        Assert.Null(message.Deferred);
    }

    [Fact]
    public async Task Fetch_With_Cancelled_Token_Ends_Aborted()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("final"))));
        var model = faux.GetModel();
        var first = await faux.Provider.StreamSimple(model, Context(), new SimpleStreamOptions
        {
            Deferred = new DeferredRequest.Enabled(),
        }).WaitForDoneAsync();
        var provider = Assert.IsAssignableFrom<IDeferredProvider>(faux.Provider);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stream = provider.StreamDeferred(model, first.Deferred!, new DeferredFetchOptions
        {
            CancellationToken = cts.Token,
        });
        var final = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Aborted, final.StopReason);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~FauxDeferredTests`
Expected: compile error: `FauxDeferredOptions` 不存在 / provider 未实现 deferred 行为。

- [ ] **Step 3: Write minimal implementation**

`Faux.cs` 追加：

```csharp
public sealed record FauxDeferredOptions
{
    /// <summary>返回原 handle 的次数；之后一次取回返回最终结果。</summary>
    public int PendingFetches { get; init; }

    public int? PollAfterMs { get; init; }
}
```

`FauxProviderOptions` 追加：

```csharp
    public FauxDeferredOptions? Deferred { get; init; }
```

`FauxProviderState` 追加：

```csharp
    private long _deferredIndex;

    internal long NextDeferredIndex() => Interlocked.Increment(ref _deferredIndex);
```

`FauxProvider.cs` 追加字段：

```csharp
    private readonly object _deferredGate = new();
    private readonly Dictionary<string, DeferredEntry> _deferredResponses = new(StringComparer.Ordinal);
```

构造器 `Provider = new StaticProvider(...)` 的 `auth:` 之后追加：

```csharp
            fetchDeferred: FetchDeferredStream,
            cancelDeferred: CancelDeferredAsync
```

`EmitAsync` 在 `if (step is null)` 分支之后、解析 `AssistantMessage message` 之前插入：

```csharp
            if (options?.Deferred is DeferredRequest.Enabled)
            {
                var handle = CreateDeferredHandle(model);
                StoreDeferred(handle, step, context, options, model);
                await EmitMessageAsync(stream, CreateDeferredMessage(model, handle), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
```

把 `EmitAsync` 中现有的：

```csharp
            var final = message with
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                Timestamp = message.Timestamp == 0 ? _clock.UtcNowMs() : message.Timestamp,
                Usage = EstimateUsage(context, message),
            };
```

替换为：

```csharp
            var final = NormalizeFinal(model, context, message);
```

在类末尾追加：

```csharp
    private AssistantMessage NormalizeFinal(
        Model model,
        TranscriptContext context,
        AssistantMessage message)
        => message with
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Timestamp = message.Timestamp == 0 ? _clock.UtcNowMs() : message.Timestamp,
            Usage = EstimateUsage(context, message),
        };

    private DeferredHandle CreateDeferredHandle(Model model) => new()
    {
        Provider = model.Provider,
        ModelId = model.Id,
        Api = model.Api,
        Id = $"deferred:{_state.NextDeferredIndex()}",
        PollAfterMs = _options.Deferred?.PollAfterMs,
    };

    private AssistantMessage CreateDeferredMessage(Model model, DeferredHandle handle) => new()
    {
        Content = Array.Empty<ContentBlock>(),
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        Usage = new Usage(),
        StopReason = StopReason.Deferred,
        Deferred = handle,
        Timestamp = _clock.UtcNowMs(),
    };

    private void StoreDeferred(
        DeferredHandle handle,
        FauxResponseStep step,
        TranscriptContext context,
        SimpleStreamOptions options,
        Model model)
    {
        var entry = new DeferredEntry
        {
            Handle = handle,
            Step = step,
            Context = context,
            Options = options,
            Model = model,
            PendingFetches = Math.Max(0, _options.Deferred?.PendingFetches ?? 0),
        };
        lock (_deferredGate) _deferredResponses[handle.Id] = entry;
    }

    private IAssistantMessageEventStream FetchDeferredStream(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options)
    {
        _state.IncrementDeferredFetchCount();
        var stream = new AssistantMessageEventStream();
        var cancellationToken = options?.CancellationToken ?? default;
        _ = FetchDeferredCoreAsync(stream, model, handle, options, cancellationToken);
        return stream;
    }

    private async Task FetchDeferredCoreAsync(
        AssistantMessageEventStream stream,
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (options?.OnResponse is not null)
            {
                await options.OnResponse(
                    new ProviderResponse(200, new Dictionary<string, string>()),
                    model).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            DeferredEntry entry;
            lock (_deferredGate)
            {
                if (!_deferredResponses.TryGetValue(handle.Id, out var found)
                    || found.Handle.Provider != handle.Provider
                    || found.Handle.ModelId != handle.ModelId
                    || found.Handle.Api != handle.Api)
                {
                    throw new InvalidOperationException(
                        $"Unknown faux deferred response: {handle.Id}");
                }
                if (found.Cancelled)
                {
                    throw new InvalidOperationException(
                        $"Faux deferred response was cancelled: {handle.Id}");
                }
                entry = found;
            }

            if (entry.PendingFetches > 0)
            {
                lock (_deferredGate) entry.PendingFetches--;
                await EmitMessageAsync(
                    stream,
                    CreateDeferredMessage(model, handle),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            AssistantMessage final;
            lock (_deferredGate) final = entry.Final;
            if (final is null)
            {
                final = await ResolveDeferredFinalAsync(entry, model).ConfigureAwait(false);
                lock (_deferredGate)
                {
                    if (entry.Final is null) entry.Final = final;
                    final = entry.Final;
                }
            }

            await EmitMessageAsync(stream, final, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EmitAborted(stream, model, "Request was aborted");
        }
        catch (Exception error)
        {
            EmitError(stream, model, error.Message);
        }
    }

    private async Task<AssistantMessage> ResolveDeferredFinalAsync(
        DeferredEntry entry,
        Model requestModel)
    {
        try
        {
            var submissionOptions = entry.Options with
            {
                Deferred = null,
                CancellationToken = default,
                OnResponse = null,
            };
            var message = entry.Step is FauxResponseStep.Factory factory
                ? await factory.Create(entry.Context, submissionOptions, _state, entry.Model)
                    .ConfigureAwait(false)
                : ((FauxResponseStep.Response)entry.Step).Message;
            return NormalizeFinal(entry.Model, entry.Context, message);
        }
        catch (Exception error)
        {
            return new AssistantMessage
            {
                Content = Array.Empty<ContentBlock>(),
                Api = requestModel.Api,
                Provider = requestModel.Provider,
                Model = requestModel.Id,
                Usage = new Usage(),
                StopReason = StopReason.Error,
                ErrorMessage = error.Message,
                Timestamp = _clock.UtcNowMs(),
            };
        }
    }

    private async Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options,
        CancellationToken cancellationToken)
    {
        var clone = handle with
        {
            Data = handle.Data is null ? null : JsonNode.Parse(handle.Data.ToJsonString()),
        };
        lock (_deferredGate)
        {
            _state.CancelledDeferred.Add(clone);
            if (_deferredResponses.TryGetValue(handle.Id, out var entry)) entry.Cancelled = true;
        }

        if (options?.OnResponse is not null)
        {
            await options.OnResponse(
                new ProviderResponse(200, new Dictionary<string, string>()),
                model).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class DeferredEntry
    {
        public required DeferredHandle Handle { get; init; }
        public required FauxResponseStep Step { get; init; }
        public required TranscriptContext Context { get; init; }
        public required SimpleStreamOptions Options { get; init; }
        public required Model Model { get; init; }
        public int PendingFetches { get; set; }
        public bool Cancelled { get; set; }
        public AssistantMessage? Final { get; set; }
    }
```

> `FauxProvider.cs` 若缺少 `using System; using System.Collections.Generic;`，补上。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~FauxDeferredTests`
Expected: PASS（4 tests）。

- [ ] **Step 5: Commit**

```bash
git add src/PiUnityAgent.Ai/Providers/Faux/Faux.cs src/PiUnityAgent.Ai/Providers/Faux/FauxProvider.cs tests/PiUnityAgent.Ai.Tests/Providers/DeferredTests.cs
git commit -m "feat(s8): Faux deferred 请求/取回/取消/缓存全行为（D3）"
```

---

### Task 9: DeferredRequest.Enabled.Window 校验

**Files:**
- Modify: `src/PiUnityAgent.Ai/Types/StreamOptionTypes.cs`
- Test: `tests/PiUnityAgent.Ai.Tests/Types/DeferredWindowTests.cs`

**Interfaces:**
- Produces: `new DeferredRequest.Enabled(window)` 构造校验；`Window` 属性保持可读、可 `with` 覆盖（`with` 绕过构造校验，仍然可用）。

- [ ] **Step 1: Write the failing test**

```csharp
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class DeferredWindowTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("15m")]
    [InlineData("1h")]
    [InlineData("24h")]
    public void Allowed_Windows_Construct(string? window)
    {
        var request = new DeferredRequest.Enabled(window);
        Assert.Equal(window, request.Window);
    }

    [Fact]
    public void Invalid_Window_Throws_Exact_Message()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new DeferredRequest.Enabled("2h"));
        Assert.Equal(
            "Unknown deferred window: 2h. Expected 15m, 1h, or 24h.",
            error.Message);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~DeferredWindowTests`
Expected: FAIL — `"2h"` 当前不会抛。

- [ ] **Step 3: Write minimal implementation**

`StreamOptionTypes.cs` 中把：

```csharp
    public sealed record Enabled(string? Window = null) : DeferredRequest
    {
    }
```

替换为：

```csharp
    public sealed record Enabled : DeferredRequest
    {
        /// <summary>延迟窗口；null / "15m" / "1h" / "24h"（pi TS 类型约束，C# 构造时校验）。</summary>
        public string? Window { get; init; }

        public Enabled(string? window = null)
        {
            if (window is not null && window is not ("15m" or "1h" or "24h"))
            {
                throw new ArgumentException(
                    $"Unknown deferred window: {window}. Expected 15m, 1h, or 24h.");
            }
            Window = window;
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~DeferredWindowTests`
Expected: PASS（5 cases）。

- [ ] **Step 5: Commit**

```bash
git add src/PiUnityAgent.Ai/Types/StreamOptionTypes.cs tests/PiUnityAgent.Ai.Tests/Types/DeferredWindowTests.cs
git commit -m "feat(s8): DeferredRequest.Enabled.Window 构造校验（D3）"
```

---

### Task 10: D17 Retry 公共模块

**Files:**
- Create: `src/PiUnityAgent.Ai/Utils/Retry.cs`
- Test: `tests/PiUnityAgent.Ai.Tests/Utils/RetryTests.cs`

**Interfaces:**
- Produces: `RetryPolicy`、`RetryCallbacks`、`Retry.DefaultMaxAgentRetryDelayMs`、`Retry.RetryDelayMs(...)`、`Retry.IsRetryableAssistantError(...)`、`Retry.RetryAssistantCall(...)`。
- Consumes: `AssistantMessage`/`StopReason`。

- [ ] **Step 1: Write the failing test**

```csharp
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Ai.Utils;

namespace PiUnityAgent.Ai.Tests.Utils;

public class RetryTests
{
    private static AssistantMessage Error(string message) => new()
    {
        Content = Array.Empty<ContentBlock>(),
        Api = "faux",
        Provider = "faux",
        Model = "faux-1",
        Usage = new Usage(),
        StopReason = StopReason.Error,
        ErrorMessage = message,
        Timestamp = 1,
    };

    private static AssistantMessage Stop(string text = "ok") => new()
    {
        Content = new ContentBlock[] { new TextContent { Text = text } },
        Api = "faux",
        Provider = "faux",
        Model = "faux-1",
        Usage = new Usage(),
        StopReason = StopReason.Stop,
        Timestamp = 1,
    };

    [Theory]
    [InlineData("GoUsageLimitError", false)]
    [InlineData("insufficient_quota", false)]
    [InlineData("quota exceeded", false)]
    [InlineData("subscription_sharing_usage_limit_exceeded", false)]
    [InlineData("overloaded", true)]
    [InlineData("HTTP 429 Too Many Requests", true)]
    [InlineData("server error", true)]
    [InlineData("connection refused", true)]
    [InlineData("stream ended before message_stop", true)]
    [InlineData("something else", false)]
    public void Classification_Match_Pi_Patterns(string message, bool expected)
        => Assert.Equal(expected, Retry.IsRetryableAssistantError(Error(message)));

    [Fact]
    public void Non_Error_And_Empty_Message_Are_Not_Retryable()
    {
        Assert.False(Retry.IsRetryableAssistantError(Stop()));
        Assert.False(Retry.IsRetryableAssistantError(Error(string.Empty)));
    }

    [Fact]
    public void Delay_Is_Exponential_And_Capped_At_Sixty_Seconds()
    {
        Assert.Equal(1_000, Retry.RetryDelayMs(1_000, null, 0));
        Assert.Equal(1_000, Retry.RetryDelayMs(1_000, null, 1));
        Assert.Equal(2_000, Retry.RetryDelayMs(1_000, null, 2));
        Assert.Equal(4_000, Retry.RetryDelayMs(1_000, null, 3));
        Assert.Equal(60_000, Retry.RetryDelayMs(1_000, null, 10));
        Assert.Equal(5_000, Retry.RetryDelayMs(1_000, 5_000, 10));
    }

    [Fact]
    public async Task RetryAssistantCall_Returns_Success_And_Emits_Callbacks_In_Order()
    {
        var calls = new List<string>();
        var attempts = 0;
        var callbacks = new RetryCallbacks
        {
            OnRetryScheduled = (attempt, max, delay, error) =>
            {
                calls.Add($"scheduled:{attempt}:{max}:{delay}:{error}");
                return Task.CompletedTask;
            },
            OnRetryAttemptStart = () =>
            {
                calls.Add("start");
                return Task.CompletedTask;
            },
            OnRetryFinished = (success, attempt, error) =>
            {
                calls.Add($"finished:{success}:{attempt}:{error}");
                return Task.CompletedTask;
            },
        };

        var result = await Retry.RetryAssistantCall(
            () => Task.FromResult(++attempts <= 2 ? Error("overloaded") : Stop()),
            new RetryPolicy
            {
                Enabled = true,
                MaxRetries = 2,
                BaseDelayMs = 0,
            },
            CancellationToken.None,
            callbacks);

        Assert.Equal(StopReason.Stop, result.StopReason);
        Assert.Equal(3, attempts);
        Assert.Equal(
            new[]
            {
                "scheduled:1:2:0:overloaded",
                "start",
                "scheduled:2:2:0:overloaded",
                "start",
                "finished:True:2:",
            },
            calls);
    }

    [Fact]
    public async Task RetryAssistantCall_Exhausted_Returns_Final_Error()
    {
        var result = await Retry.RetryAssistantCall(
            () => Task.FromResult(Error("overloaded")),
            new RetryPolicy { Enabled = true, MaxRetries = 1, BaseDelayMs = 0 },
            CancellationToken.None);

        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Equal("overloaded", result.ErrorMessage);
    }

    [Fact]
    public async Task RetryAssistantCall_Non_Retryable_Fails_Fast()
    {
        var attempts = 0;
        var result = await Retry.RetryAssistantCall(
            () =>
            {
                attempts++;
                return Task.FromResult(Error("insufficient_quota"));
            },
            new RetryPolicy { Enabled = true, MaxRetries = 5, BaseDelayMs = 0 },
            CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(StopReason.Error, result.StopReason);
    }

    [Fact]
    public async Task RetryAssistantCall_Cancelled_Backoff_Returns_Aborted_Without_Error()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await Retry.RetryAssistantCall(
            () => Task.FromResult(Error("overloaded")),
            new RetryPolicy { Enabled = true, MaxRetries = 3, BaseDelayMs = 10 },
            cts.Token);

        Assert.Equal(StopReason.Aborted, result.StopReason);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Disabled_Policy_Returns_First_Response_Unchanged()
    {
        var attempts = 0;
        var result = await Retry.RetryAssistantCall(
            () =>
            {
                attempts++;
                return Task.FromResult(Error("overloaded"));
            },
            new RetryPolicy { Enabled = false, MaxRetries = 5, BaseDelayMs = 0 },
            CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal("overloaded", result.ErrorMessage);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~RetryTests`
Expected: compile error: `Retry`/`RetryPolicy` not found.

- [ ] **Step 3: Write minimal implementation**

`src/PiUnityAgent.Ai/Utils/Retry.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Utils;

/// <summary>pi retry.ts: 有界重试策略；maxRetries 不含首次调用。</summary>
public sealed record RetryPolicy
{
    public bool Enabled { get; init; }
    public int MaxRetries { get; init; }
    public int BaseDelayMs { get; init; }
    public int? MaxAgentDelayMs { get; init; }
}

/// <summary>pi retry.ts RetryCallbacks；回调允许 async（Task）。</summary>
public sealed record RetryCallbacks
{
    public Func<int, int, int, string, Task>? OnRetryScheduled { get; init; }
    public Func<Task>? OnRetryAttemptStart { get; init; }
    public Func<bool, int, string?, Task>? OnRetryFinished { get; init; }
}

public static class Retry
{
    public const int DefaultMaxAgentRetryDelayMs = 60_000;

    // pi retry.ts:7-35：配额/计费/订阅限制——重试也不会成功，必须快速失败。
    private static readonly string[] NonRetryablePatterns =
    {
        "GoUsageLimitError",
        "FreeUsageLimitError",
        "Monthly usage limit reached",
        "available balance",
        "insufficient_quota",
        "out of budget",
        "quota exceeded",
        "billing",
        "subscription_sharing_usage_limit_exceeded",
        "subscription_sharing_workplace_individual_usage_limit_exceeded",
        "\"code\"\\s*:\\s*\"?1308(?![0-9])",
    };

    // pi retry.ts:37-125：瞬态 provider/传输错误。
    private static readonly string[] RetryablePatterns =
    {
        "overloaded",
        "server_busy",
        "servers are currently busy",
        "currently experiencing high demand",
        "model is at capacity",
        "rate.?limit",
        "too many requests",
        "(?<![a-z0-9])(?:429|500|502|503|504|520|524)(?![a-z0-9])",
        "service.?(?:is.?)?(?:temporarily.?)?unavailable",
        "server.?error",
        "internal.?error",
        "provider.?returned.?(?:an.?)?error",
        "exceeded request buffer limit while retrying upstream",
        "unable to forward upstream",
        "upstream.?request.?failed",
        "upstream_stream_read_error",
        "stream was interrupted",
        "http/?2 stream failed",
        "network.?error",
        "connection.?error",
        "connection.?refused",
        "connection.?lost",
        "other side closed",
        "fetch failed",
        "request to \\S+ failed, reason:",
        "getaddrinfo",
        "ENOTFOUND",
        "EAI_AGAIN",
        "upstream.?connect",
        "reset before headers",
        "socket hang up",
        "socket connection was closed",
        "timed? out",
        "timeout",
        "terminated",
        "websocket.?closed",
        "websocket.?error",
        "ended without",
        "stream ended before message_stop",
        "stream ended before a terminal response event",
        "http2 request did not get a response",
        "stream disconnected before completion",
        "pending stream has been canceled",
        "retry delay",
        "you can retry your request",
        "try your request again",
        "please retry your request",
        "ResourceExhausted",
        "subscription_sharing_usage_unavailable",
        "subscription_sharing_user_unavailable",
    };

    private static readonly Regex NonRetryablePattern = Build(NonRetryablePatterns);
    private static readonly Regex RetryablePattern = Build(RetryablePatterns);

    public static int RetryDelayMs(int baseDelayMs, int? maxAgentDelayMs, int attempt)
    {
        var exponent = Math.Max(0, attempt - 1);
        var computed = Math.Max(0, baseDelayMs) * Math.Pow(2, exponent);
        var safe = double.IsInfinity(computed) || computed > int.MaxValue
            ? int.MaxValue
            : (int)Math.Round(computed);
        var cap = maxAgentDelayMs ?? DefaultMaxAgentRetryDelayMs;
        return Math.Min(safe, cap);
    }

    public static int RetryDelayMs(RetryPolicy policy, int attempt)
        => RetryDelayMs(policy.BaseDelayMs, policy.MaxAgentDelayMs, attempt);

    public static bool IsRetryableAssistantError(AssistantMessage message)
    {
        if (message.StopReason != StopReason.Error
            || string.IsNullOrEmpty(message.ErrorMessage))
        {
            return false;
        }

        if (NonRetryablePattern.IsMatch(message.ErrorMessage)) return false;
        return RetryablePattern.IsMatch(message.ErrorMessage);
    }

    public static async Task<AssistantMessage> RetryAssistantCall(
        Func<Task<AssistantMessage>> produce,
        RetryPolicy? policy,
        CancellationToken signal,
        RetryCallbacks? callbacks = null)
    {
        var maxAttempts = policy?.Enabled == true ? policy.MaxRetries : 0;
        var attempt = 0;
        var lastRetryAttempt = 0;
        var lastRetryError = string.Empty;
        var retried = false;

        while (true)
        {
            var response = await produce().ConfigureAwait(false);

            if (response.StopReason == StopReason.Aborted)
            {
                if (retried && callbacks?.OnRetryFinished is not null)
                {
                    await callbacks.OnRetryFinished(false, lastRetryAttempt, null)
                        .ConfigureAwait(false);
                }
                return response;
            }

            if (response.StopReason != StopReason.Error)
            {
                if (retried && callbacks?.OnRetryFinished is not null)
                {
                    await callbacks.OnRetryFinished(true, lastRetryAttempt, null)
                        .ConfigureAwait(false);
                }
                return response;
            }

            if (attempt >= maxAttempts || !IsRetryableAssistantError(response))
            {
                if (retried && callbacks?.OnRetryFinished is not null)
                {
                    await callbacks.OnRetryFinished(
                        false,
                        lastRetryAttempt,
                        response.ErrorMessage).ConfigureAwait(false);
                }
                return response;
            }

            attempt++;
            retried = true;
            lastRetryAttempt = attempt;
            lastRetryError = string.IsNullOrEmpty(response.ErrorMessage)
                ? "Unknown error"
                : response.ErrorMessage;
            var delayMs = RetryDelayMs(policy!, attempt);

            if (callbacks?.OnRetryScheduled is not null)
            {
                await callbacks.OnRetryScheduled(
                    attempt,
                    maxAttempts,
                    delayMs,
                    lastRetryError).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(delayMs, signal).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (callbacks?.OnRetryFinished is not null)
                {
                    await callbacks.OnRetryFinished(false, attempt, lastRetryError)
                        .ConfigureAwait(false);
                }
                return response with
                {
                    StopReason = StopReason.Aborted,
                    ErrorMessage = null,
                };
            }

            if (callbacks?.OnRetryAttemptStart is not null)
            {
                await callbacks.OnRetryAttemptStart().ConfigureAwait(false);
            }
        }
    }

    private static Regex Build(IReadOnlyList<string> patterns)
        => new Regex(
            string.Join("|", patterns),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --filter FullyQualifiedName~RetryTests`
Expected: PASS（8 tests）。

- [ ] **Step 5: Commit**

```bash
git add src/PiUnityAgent.Ai/Utils/Retry.cs tests/PiUnityAgent.Ai.Tests/Utils/RetryTests.cs
git commit -m "feat(s8): 助手轮次重试公共模块（D17）"
```

---

### Task 11: 门禁、台账与文档收尾

**Files:**
- Modify: `docs/待办-后置能力.md`（D3/D17/D23 → `已完成`，填完成提交与验收锚点）
- Modify: `docs/05-教学阶段路线.md`（S8 状态）
- Modify: `PORTING.md`（Agent/deferred/retry 文件状态）
- Modify: `.superpowers/sdd/progress.md`（S8 任务完成记录）

**Interfaces:**
- Consumes: Tasks 1–10 全部提交。
- Produces: S8 阶段收口证据。

- [ ] **Step 1: 跑全量门禁**

```bash
dotnet build PiUnityAgent.slnx -c Release
dotnet test tests/PiUnityAgent.Core.Tests/PiUnityAgent.Core.Tests.csproj
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj
python scripts/check-deferred.py --root . --current-stage S8 --gate exit
```

Expected:
- Release：`Build succeeded`，0 warning，0 error；
- Core：全绿；
- Ai：全绿（含新增 Deferred/Retry/Window）；
- check-deferred：`OK`，D3/D17/D23 无 stub，目标 S8 项已完成。

- [ ] **Step 2: 更新台账**

`docs/待办-后置能力.md`：
- D3 行：状态 `已完成`，完成提交填本阶段实现提交，备注补“Models 管线/工厂/StaticProvider/Faux 全行为 + Window 校验”；验收锚点补 `DeferredProviderCapabilityTests`/`DeferredModelsTests`/`FauxDeferredTests`/`DeferredWindowTests`。
- D17 行：状态 `已完成`，完成提交填 `Retry` 提交；验收锚点补 `RetryTests`；备注“pi 的 agent-core 不消费，调用方 2A”。
- D23 行：状态 `已完成`，完成提交填队列提交；验收锚点补 `AgentQueueTests`/`AgentQueueIntegrationTests`。
- 最后更新行日期改 `2026-11-09`，补“S8 收口：D3/D17/D23 已完成”。

- [ ] **Step 3: 更新阶段状态与 PORTING**

`docs/05-教学阶段路线.md` S8 行改为：

```markdown
| **S8** | 双队列与钩子：steer/followUp、abort、before/after、prepare/finish | `agent/src/agent.ts`、`agent/src/types.ts` | `pi-agent-book/02-runtime` §2.7–2.8；`pi-agent-book/06-extensibility` §6.2 | **实现完成，独立审计待做**（`feature/s8-agent-facade`；D3/D17/D23 已完成） | 队列/钩子测试 |
```

`PORTING.md` 增补：

```markdown
| `agent/src/agent.ts` | `Core/Agent/Agent.cs` + `Agent.Run.cs` + `Agent.Queues.cs` | S8 | ✅ |
| `ai/src/utils/retry.ts` | `Ai/Utils/Retry.cs` | S8/D17 | ✅ |
| `ai/src/models.ts`（deferred 段） | `Ai/Providers/Models.Capabilities.cs` | S8/D3 | ✅ |
| `ai/src/providers/faux.ts`（deferred 段） | `Ai/Providers/Faux/FauxProvider.cs` | S8/D3 | ✅ |
```

`.superpowers/sdd/progress.md` 追加（该文件在 `.superpowers/sdd/.gitignore` 中，只作本地进度台账，不提交）：

```markdown
S8 Agent 门面（Tasks 1–11）：完成；Release 0/0；Core tests green；Ai tests green；check-deferred S8 exit OK；待独立偏离审计。
```

- [ ] **Step 4: 提交收尾**

```bash
git add docs/待办-后置能力.md docs/05-教学阶段路线.md PORTING.md
git commit -m "docs(s8): 阶段收尾——台账 D3/D17/D23 完成与 PORTING 状态"
```

- [ ] **Step 5: 独立偏离审计（SOP 门禁，不在此实现）**

按 [`docs/SOP/阶段偏离审查-SOP-2026-11-08.md`](../../SOP/阶段偏离审查-SOP-2026-11-08.md) 派独立审计 subagent（只读代码，只写 `docs/audits/` 报告）。审计需核对：

- S7 形状零回改；
- §6 偏离表逐行与代码一致（尤其 `AgentOptions.prepareNextTurn` 门面/loop 两层形状、`AgentState` 快照、`IDeferredProvider.Supports*`、Window 校验）；
- D3/D17/D23 验收锚点真实存在且测试转绿。

审计结论非 FAIL 且复核通过后，才向用户申请合并 main（项目合并门禁）。

