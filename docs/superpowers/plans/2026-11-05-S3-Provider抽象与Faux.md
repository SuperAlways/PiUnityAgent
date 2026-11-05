# S3 Provider 抽象与 Faux Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 S3：`Model`/`Provider`/`Models` 注册表、`StreamFn`、`SimpleStreamOptions`，以及确定性、无网络的 `FauxProvider`，让 S7 Agent Loop 可以无网跑通。

**Architecture:** `Types/` 放 Model/Options/StreamFn；`Providers/` 放 Provider/Models/Faux。`FauxProvider` 用脚本队列决定响应，按 S2 事件流输出文本/思考/工具调用/终态；ID、分块、时间均可固定，默认零延迟。不做 HTTP/SSE/Auth。

**Tech Stack:** C# / netstandard2.1 / System.Text.Json 8.0.5 / xUnit（net10.0）。

## Global Constraints

- 库目标框架：`netstandard2.1`；测试：`net10.0`。
- 禁止引用 `UnityEngine` / `GameData.*` / Harmony。
- 禁止 .NET 9 专有 API（无 `Task.WaitAsync`、无 `Stopwatch.GetElapsedTime`）。
- `StreamFn` 不得抛异常；失败必须编码为 `error` 终态事件。
- Faux 必须确定性：ID 用计数器，时间用 `IClock`，分块固定大小；不使用 `Random`/`DateTime.Now` 参与 ID。
- Faux 默认零延迟（`TokensPerSecond = 0`）；不做 deferred fetch/cancel。
- 事件流单消费者；错误是事件。
- 提交时显式 `git add`，禁止 `git add -A`。

---

## File Structure

```
src/PiUnityAgent.Ai/
├─ Types/
│  ├─ Model.cs                 # Model / ModelCost / ModelInputModality
│  ├─ SimpleStreamOptions.cs   # 选项
│  └─ StreamFn.cs              # 委托
├─ Providers/
│  ├─ IClock.cs                # IClock + SystemClock
│  ├─ Provider.cs              # Provider record
│  ├─ Models.cs                # IModels + Models registry
│  └─ Faux/
│     ├─ Faux.cs               # 工厂/脚本条目/状态/选项
│     └─ FauxProvider.cs       # 脚本队列 + 事件流

tests/PiUnityAgent.Ai.Tests/
└─ Providers/
   ├─ ModelsTests.cs
   ├─ FauxTests.cs
   └─ FauxStreamTests.cs
```

---

## Task 0: 准备分支

**Files:** none

- [ ] **Step 0.1: 创建 S3 分支**

Run:
```bash
cd D:/shuoshu/taiwuask/PiUnityAgent
git switch -c feature/s3-provider-faux
```
Expected: `Switched to a new branch 'feature/s3-provider-faux'`

- [ ] **Step 0.2: 确认基线**

Run:
```bash
git status --short
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo
```
Expected: 工作区干净；104 tests pass。

---

## Task 1: Model / Options / StreamFn / Provider / Models

**Files:**
- Create: `src/PiUnityAgent.Ai/Types/Model.cs`
- Create: `src/PiUnityAgent.Ai/Types/SimpleStreamOptions.cs`
- Create: `src/PiUnityAgent.Ai/Types/StreamFn.cs`
- Create: `src/PiUnityAgent.Ai/Providers/IClock.cs`
- Create: `src/PiUnityAgent.Ai/Providers/Provider.cs`
- Create: `src/PiUnityAgent.Ai/Providers/Models.cs`
- Test: `tests/PiUnityAgent.Ai.Tests/Providers/ModelsTests.cs`

**Interfaces:**
- Produces: `Model`、`ModelCost`、`ModelInputModality`、`SimpleStreamOptions`、`StreamFn`、`IClock`、`Provider`、`IModels`、`Models`。

- [ ] **Step 1.1: 写失败测试**

Create `tests/PiUnityAgent.Ai.Tests/Providers/ModelsTests.cs`:

```csharp
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class ModelsTests
{
    private static Model CreateModel(string id = "m1", string provider = "p1")
        => new()
        {
            Id = id,
            Name = id,
            Api = "test",
            Provider = provider,
            BaseUrl = "http://localhost:0",
        };

    private static Provider CreateProvider(string id = "p1", StreamFn? stream = null)
        => new()
        {
            Id = id,
            Name = id,
            Models = new[] { CreateModel("m1", id) },
            Stream = stream ?? ((model, context, options, ct) =>
            {
                var stream = new AssistantMessageEventStream();
                var message = new AssistantMessage
                {
                    Content = Array.Empty<ContentBlock>(),
                    Api = model.Api,
                    Provider = model.Provider,
                    Model = model.Id,
                    Usage = new Usage(),
                    StopReason = StopReason.Stop,
                    Timestamp = 1,
                };
                stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
                stream.End(message);
                return Task.FromResult<IAssistantMessageEventStream>(stream);
            }),
        };

    [Fact]
    public void Set_Get_Replace_Delete_Provider()
    {
        var models = new Models();
        var first = CreateProvider();
        models.SetProvider(first);

        Assert.Same(first, models.GetProvider("p1"));
        Assert.Single(models.GetModels());
        Assert.NotNull(models.GetModel("p1", "m1"));

        var replacement = CreateProvider();
        models.SetProvider(replacement);
        Assert.Same(replacement, models.GetProvider("p1"));
        Assert.True(models.DeleteProvider("p1"));
        Assert.Null(models.GetProvider("p1"));
    }

    [Fact]
    public void GetModels_Filters_By_Provider()
    {
        var models = new Models();
        models.SetProvider(CreateProvider("p1"));
        models.SetProvider(CreateProvider("p2"));

        Assert.Single(models.GetModels("p1"));
        Assert.Equal(2, models.GetModels().Count);
    }

    [Fact]
    public async Task StreamSimple_Dispatches_To_Provider()
    {
        var models = new Models();
        var called = false;
        models.SetProvider(CreateProvider(stream: (model, context, options, ct) =>
        {
            called = true;
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage
            {
                Content = Array.Empty<ContentBlock>(),
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                Usage = new Usage(),
                StopReason = StopReason.Stop,
                Timestamp = 1,
            };
            stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
            stream.End(message);
            return Task.FromResult<IAssistantMessageEventStream>(stream);
        }));

        var result = await models.StreamSimpleAsync(
            CreateModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var done = await result.WaitForDoneAsync();

        Assert.True(called);
        Assert.Equal(StopReason.Stop, done.StopReason);
    }

    [Fact]
    public async Task StreamSimple_Unknown_Provider_Returns_Error_Event()
    {
        var models = new Models();
        var stream = await models.StreamSimpleAsync(
            CreateModel(provider: "missing"),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Contains("Unknown provider", done.ErrorMessage);
    }
}
```

- [ ] **Step 1.2: 运行测试确认失败**

Run:
```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo --filter "FullyQualifiedName~ModelsTests"
```
Expected: FAIL（类型不存在）。

- [ ] **Step 1.3: 实现 Model 类型**

Create `src/PiUnityAgent.Ai/Types/Model.cs`:

```csharp
namespace PiUnityAgent.Ai.Types;

public enum ModelInputModality { Text, Image }

public sealed record ModelCost(
    double Input = 0,
    double Output = 0,
    double CacheRead = 0,
    double CacheWrite = 0);

public sealed record Model
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Api { get; init; }
    public required string Provider { get; init; }
    public required string BaseUrl { get; init; }

    public IReadOnlyList<ModelInputModality> Input { get; init; } =
        new[] { ModelInputModality.Text };

    public ModelCost Cost { get; init; } = new();
    public bool Reasoning { get; init; }
    public int ContextWindow { get; init; } = 128000;
    public int MaxTokens { get; init; } = 16384;
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    public IReadOnlyDictionary<ModelThinkingLevel, string?>? ThinkingLevelMap { get; init; }
}
```

- [ ] **Step 1.4: 实现 SimpleStreamOptions**

Create `src/PiUnityAgent.Ai/Types/SimpleStreamOptions.cs`:

```csharp
using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Types;

public sealed record SimpleStreamOptions
{
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public int? MaxTokens { get; init; }
    public double? Temperature { get; init; }
    public IReadOnlyList<string>? StopSequences { get; init; }
    public string? SessionId { get; init; }
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    public JsonObject? SamplingParams { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
    public JsonNode? ToolChoice { get; init; }
    public int? TimeoutMs { get; init; }
    public int? MaxRetries { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
```

- [ ] **Step 1.5: 实现 StreamFn**

Create `src/PiUnityAgent.Ai/Types/StreamFn.cs`:

```csharp
using PiUnityAgent.Ai.Events;

namespace PiUnityAgent.Ai.Types;

public delegate Task<IAssistantMessageEventStream> StreamFn(
    Model model,
    TranscriptContext context,
    SimpleStreamOptions? options,
    CancellationToken cancellationToken);
```

- [ ] **Step 1.6: 实现 IClock / Provider / Models**

Create `src/PiUnityAgent.Ai/Providers/IClock.cs`:

```csharp
namespace PiUnityAgent.Ai.Providers;

public interface IClock
{
    long UtcNowMs();
}

public sealed class SystemClock : IClock
{
    public long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
```

Create `src/PiUnityAgent.Ai/Providers/Provider.cs`:

```csharp
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed record Provider
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<Model> Models { get; init; }
    public required StreamFn Stream { get; init; }
}
```

Create `src/PiUnityAgent.Ai/Providers/Models.cs`:

```csharp
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public interface IModels
{
    IReadOnlyList<Provider> Providers { get; }
    Provider? GetProvider(string id);
    IReadOnlyList<Model> GetModels(string? provider = null);
    Model? GetModel(string provider, string id);
    Task<IAssistantMessageEventStream> StreamSimpleAsync(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken);
}

public sealed class Models : IModels
{
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);

    public IReadOnlyList<Provider> Providers => _providers.Values.ToList();

    public void SetProvider(Provider provider) => _providers[provider.Id] = provider;

    public bool DeleteProvider(string id) => _providers.Remove(id);

    public void ClearProviders() => _providers.Clear();

    public Provider? GetProvider(string id)
        => _providers.TryGetValue(id, out var provider) ? provider : null;

    public IReadOnlyList<Model> GetModels(string? provider = null)
    {
        if (provider is not null)
            return GetProvider(provider)?.Models ?? Array.Empty<Model>();
        return _providers.Values.SelectMany(p => p.Models).ToList();
    }

    public Model? GetModel(string provider, string id)
        => GetProvider(provider)?.Models.FirstOrDefault(m => m.Id == id);

    public async Task<IAssistantMessageEventStream> StreamSimpleAsync(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken)
    {
        var provider = GetProvider(model.Provider);
        if (provider is null)
            return ErrorStream(model, $"Unknown provider: {model.Provider}");

        try
        {
            return await provider.Stream(model, context, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return ErrorStream(model, error.Message);
        }
    }

    private static IAssistantMessageEventStream ErrorStream(Model model, string message)
    {
        var stream = new AssistantMessageEventStream();
        var assistant = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = StopReason.Error,
            ErrorMessage = message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        stream.Push(new AssistantMessageEvent.Error(StopReason.Error, assistant));
        stream.End(assistant);
        return stream;
    }
}
```

- [ ] **Step 1.7: 运行测试确认通过**

Run:
```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo --filter "FullyQualifiedName~ModelsTests"
```
Expected: PASS（4 tests）。

- [ ] **Step 1.8: 提交**

```bash
git add src/PiUnityAgent.Ai/Types/Model.cs \
        src/PiUnityAgent.Ai/Types/SimpleStreamOptions.cs \
        src/PiUnityAgent.Ai/Types/StreamFn.cs \
        src/PiUnityAgent.Ai/Providers/IClock.cs \
        src/PiUnityAgent.Ai/Providers/Provider.cs \
        src/PiUnityAgent.Ai/Providers/Models.cs \
        tests/PiUnityAgent.Ai.Tests/Providers/ModelsTests.cs
git commit -m "feat(ai): model, provider, and models registry"
```

---

## Task 2: Faux 类型、脚本队列与辅助工厂

**Files:**
- Create: `src/PiUnityAgent.Ai/Providers/Faux/Faux.cs`
- Create: `src/PiUnityAgent.Ai/Providers/Faux/FauxProvider.cs`
- Test: `tests/PiUnityAgent.Ai.Tests/Providers/FauxTests.cs`

**Interfaces:**
- Consumes: `Model`, `Provider`, `StreamFn`, `IClock`, S1 消息类型。
- Produces: `Faux` 工厂、`FauxResponseStep`、`FauxProviderOptions`、`FauxModelDefinition`、`FauxProviderState`、`FauxProvider`（`SetResponses`/`AppendResponses`/`PendingResponseCount`/`GetModel`/`Provider`/`State`）。

- [ ] **Step 2.1: 写失败测试**

Create `tests/PiUnityAgent.Ai.Tests/Providers/FauxTests.cs`:

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class FauxTests
{
    [Fact]
    public void Default_Model_And_Provider_Are_Registered()
    {
        var faux = new FauxProvider();
        var model = faux.GetModel();

        Assert.Equal("faux", faux.Provider.Id);
        Assert.Equal("faux-1", model.Id);
        Assert.Equal("faux", model.Api);
        Assert.Contains(model, faux.Models);
    }

    [Fact]
    public void Set_Append_And_Pending_Responses()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("a")),
        });
        faux.AppendResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("b")),
        });

        Assert.Equal(2, faux.PendingResponseCount);
    }

    [Fact]
    public void Helpers_Create_Content_And_Messages()
    {
        var text = Faux.Text("hi");
        var thinking = Faux.Thinking("hmm");
        var call = Faux.ToolCall("query_hp", new JsonObject { ["target"] = "player" }, "call_1");
        var message = Faux.AssistantMessage(new ContentBlock[] { text, thinking, call }, StopReason.ToolUse);

        Assert.Equal("text", text.Type);
        Assert.Equal("thinking", thinking.Type);
        Assert.Equal("toolCall", call.Type);
        Assert.Equal(StopReason.ToolUse, message.StopReason);
        Assert.Equal(3, message.Content.Count);
    }
}
```

- [ ] **Step 2.2: 运行测试确认失败**

Run:
```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo --filter "FullyQualifiedName~FauxTests"
```
Expected: FAIL（Faux 类型不存在）。

- [ ] **Step 2.3: 实现 Faux 类型与工厂**

Create `src/PiUnityAgent.Ai/Providers/Faux/Faux.cs`:

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public static class Faux
{
    public const string DefaultApi = "faux";
    public const string DefaultProvider = "faux";
    public const string DefaultModelId = "faux-1";
    public const string DefaultModelName = "Faux Model";
    public const string DefaultBaseUrl = "http://localhost:0";

    public static TextContent Text(string text) => new() { Text = text };

    public static ThinkingContent Thinking(string thinking) => new() { Thinking = thinking };

    public static ToolCall ToolCall(string name, JsonObject arguments, string id)
        => new() { Id = id, Name = name, Arguments = arguments };

    public static AssistantMessage AssistantMessage(
        string content,
        StopReason stopReason = StopReason.Stop,
        string? errorMessage = null,
        string? responseId = null,
        long timestamp = 0)
        => AssistantMessage(new ContentBlock[] { Text(content) }, stopReason, errorMessage, responseId, timestamp);

    public static AssistantMessage AssistantMessage(
        ContentBlock content,
        StopReason stopReason = StopReason.Stop,
        string? errorMessage = null,
        string? responseId = null,
        long timestamp = 0)
        => AssistantMessage(new[] { content }, stopReason, errorMessage, responseId, timestamp);

    public static AssistantMessage AssistantMessage(
        IReadOnlyList<ContentBlock> content,
        StopReason stopReason = StopReason.Stop,
        string? errorMessage = null,
        string? responseId = null,
        long timestamp = 0)
        => new()
        {
            Content = content,
            Api = DefaultApi,
            Provider = DefaultProvider,
            Model = DefaultModelId,
            Usage = new Usage(),
            StopReason = stopReason,
            ErrorMessage = errorMessage,
            ResponseId = responseId,
            Timestamp = timestamp,
        };
}

public sealed record FauxModelDefinition
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public bool Reasoning { get; init; }
    public IReadOnlyList<ModelInputModality>? Input { get; init; }
    public ModelCost? Cost { get; init; }
    public int ContextWindow { get; init; } = 128000;
    public int MaxTokens { get; init; } = 16384;
}

public sealed record FauxProviderOptions
{
    public string Api { get; init; } = Faux.DefaultApi;
    public string Provider { get; init; } = Faux.DefaultProvider;
    public IReadOnlyList<FauxModelDefinition>? Models { get; init; }
    public double TokensPerSecond { get; init; }
    public int MinTokenSize { get; init; } = 3;
    public int MaxTokenSize { get; init; } = 5;
    public IClock? Clock { get; init; }
}

public sealed class FauxProviderState
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);
    internal void IncrementCallCount() => Interlocked.Increment(ref _callCount);
}

public delegate Task<AssistantMessage> FauxResponseFactory(
    TranscriptContext context,
    SimpleStreamOptions? options,
    FauxProviderState state,
    Model model);

public abstract record FauxResponseStep
{
    public sealed record Response(AssistantMessage Message) : FauxResponseStep;
    public sealed record Factory(FauxResponseFactory Create) : FauxResponseStep; // C# 不允许成员与类型同名
}
```

- [ ] **Step 2.4: 实现 FauxProvider 外壳**

Create `src/PiUnityAgent.Ai/Providers/Faux/FauxProvider.cs`:

```csharp
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed class FauxProvider
{
    private readonly object _gate = new();
    private readonly Queue<FauxResponseStep> _responses = new();
    private readonly FauxProviderOptions _options;
    private readonly IClock _clock;
    private readonly FauxProviderState _state = new();

    public FauxProvider(FauxProviderOptions? options = null)
    {
        _options = options ?? new FauxProviderOptions();
        _clock = _options.Clock ?? new SystemClock();
        var definitions = _options.Models is { Count: > 0 }
            ? _options.Models
            : new[]
            {
                new FauxModelDefinition
                {
                    Id = Faux.DefaultModelId,
                    Name = Faux.DefaultModelName,
                    Input = new[] { ModelInputModality.Text, ModelInputModality.Image },
                },
            };

        Models = definitions.Select(definition => new Model
        {
            Id = definition.Id,
            Name = definition.Name ?? definition.Id,
            Api = _options.Api,
            Provider = _options.Provider,
            BaseUrl = Faux.DefaultBaseUrl,
            Input = definition.Input ?? new[] { ModelInputModality.Text, ModelInputModality.Image },
            Cost = definition.Cost ?? new ModelCost(),
            Reasoning = definition.Reasoning,
            ContextWindow = definition.ContextWindow,
            MaxTokens = definition.MaxTokens,
        }).ToList();

        Provider = new Provider
        {
            Id = _options.Provider,
            Name = _options.Provider,
            Models = Models,
            Stream = StreamAsync,
        };
    }

    public Provider Provider { get; }
    public IReadOnlyList<Model> Models { get; }
    public FauxProviderState State => _state;

    public int PendingResponseCount
    {
        get { lock (_gate) return _responses.Count; }
    }

    public void SetResponses(IEnumerable<FauxResponseStep> steps)
    {
        lock (_gate)
        {
            _responses.Clear();
            foreach (var step in steps) _responses.Enqueue(step);
        }
    }

    public void AppendResponses(IEnumerable<FauxResponseStep> steps)
    {
        lock (_gate)
        {
            foreach (var step in steps) _responses.Enqueue(step);
        }
    }

    public Model GetModel(string? id = null)
        => id is null ? Models[0] : Models.First(m => m.Id == id);

    private Task<IAssistantMessageEventStream> StreamAsync(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken)
    {
        FauxResponseStep? step;
        lock (_gate) step = _responses.Count > 0 ? _responses.Dequeue() : null;
        _state.IncrementCallCount();

        var stream = new AssistantMessageEventStream();
        _ = EmitAsync(stream, step, model, context, options, cancellationToken);
        return Task.FromResult<IAssistantMessageEventStream>(stream);
    }

    private async Task EmitAsync(
        AssistantMessageEventStream stream,
        FauxResponseStep? step,
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (step is null)
            {
                EmitError(stream, model, "No more faux responses queued");
                return;
            }

            AssistantMessage message;
            if (step is FauxResponseStep.Factory factory)
            {
                message = await factory.Create(context, options, _state, model).ConfigureAwait(false);
            }
            else
            {
                message = ((FauxResponseStep.Response)step).Message;
            }

            var final = message with
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                Timestamp = message.Timestamp == 0 ? _clock.UtcNowMs() : message.Timestamp,
                Usage = EstimateUsage(context, message),
            };

            await EmitMessageAsync(stream, final, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EmitAborted(stream, model, "Aborted");
        }
        catch (Exception error)
        {
            EmitError(stream, model, error.Message);
        }
    }

    private static Usage EstimateUsage(TranscriptContext context, AssistantMessage message)
    {
        var input = context.Messages.Sum(m => m switch
        {
            UserMessage user => user.Content.ToBlocks().Sum(b => b is TextContent t ? t.Text.Length : 0),
            _ => 0,
        }) / 4;
        var output = message.Content.OfType<TextContent>().Sum(t => t.Text.Length) / 4;
        return new Usage
        {
            Input = input,
            Output = output,
            TotalTokens = input + output,
        };
    }

    private static void EmitError(AssistantMessageEventStream stream, Model model, string message)
        => EmitError(stream, new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = StopReason.Error,
            ErrorMessage = message,
            Timestamp = 0,
        }, message);

    private static void EmitError(AssistantMessageEventStream stream, AssistantMessage message, string text)
    {
        var assistant = message with
        {
            Content = Array.Empty<ContentBlock>(),
            StopReason = StopReason.Error,
            ErrorMessage = text,
        };
        stream.Push(new AssistantMessageEvent.Error(StopReason.Error, assistant));
        stream.End(assistant);
    }

    private static void EmitAborted(AssistantMessageEventStream stream, Model model, string text)
    {
        var assistant = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = StopReason.Aborted,
            ErrorMessage = text,
            Timestamp = 0,
        };
        stream.Push(new AssistantMessageEvent.Error(StopReason.Aborted, assistant));
        stream.End(assistant);
    }

    private async Task EmitMessageAsync(
        AssistantMessageEventStream stream,
        AssistantMessage final,
        CancellationToken cancellationToken)
    {
        // Task 3 implements the streaming details.
        cancellationToken.ThrowIfCancellationRequested();
        if (final.StopReason is StopReason.Error or StopReason.Aborted)
        {
            stream.Push(new AssistantMessageEvent.Error(final.StopReason, final));
            stream.End(final);
            return;
        }

        if (final.StopReason == StopReason.Pending)
        {
            EmitError(stream, final, "Faux response ended without a stop reason");
            return;
        }

        stream.Push(new AssistantMessageEvent.Done(final.StopReason, final));
        stream.End(final);
        await Task.CompletedTask;
    }
}
```

> Task 2 deliberately provides a shell `EmitMessageAsync` that only emits Done/Error; Task 3 replaces it with real chunked streaming.

- [ ] **Step 2.5: 运行测试确认通过**

Run:
```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo --filter "FullyQualifiedName~FauxTests"
```
Expected: PASS（3 tests）。

- [ ] **Step 2.6: 提交**

```bash
git add src/PiUnityAgent.Ai/Providers/Faux/Faux.cs \
        src/PiUnityAgent.Ai/Providers/Faux/FauxProvider.cs \
        tests/PiUnityAgent.Ai.Tests/Providers/FauxTests.cs
git commit -m "feat(ai): faux provider types and script queue"
```

---

## Task 3: Faux 分块流式输出

**Files:**
- Modify: `src/PiUnityAgent.Ai/Providers/Faux/FauxProvider.cs`
- Test: `tests/PiUnityAgent.Ai.Tests/Providers/FauxStreamTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `FauxProvider`/`StreamAsync`/`EmitMessageAsync`。
- Produces: 文本/思考/工具调用事件流；取消、空队列、错误、确定性分块。

- [ ] **Step 3.1: 写失败测试**

Create `tests/PiUnityAgent.Ai.Tests/Providers/FauxStreamTests.cs`:

```csharp
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class FauxStreamTests
{
    private static async Task<List<AssistantMessageEvent>> CollectAsync(
        IAssistantMessageEventStream stream, CancellationToken cancellationToken = default)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream.WithCancellation(cancellationToken))
        {
            events.Add(@event);
        }
        return events;
    }

    [Fact]
    public async Task Text_Response_Emits_Start_Deltas_End_Done()
    {
        var faux = new FauxProvider(new FauxProviderOptions { MinTokenSize = 3, MaxTokenSize = 3 });
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("abcdef")),
        });

        var model = faux.GetModel();
        var stream = await faux.Provider.Stream(
            model,
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var events = await CollectAsync(stream);

        Assert.IsType<AssistantMessageEvent.Start>(events[0]);
        Assert.Equal(2, events.OfType<AssistantMessageEvent.TextDelta>().Count());
        Assert.IsType<AssistantMessageEvent.TextEnd>(events[^2]);
        Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(1, faux.State.CallCount);
    }

    [Fact]
    public async Task Thinking_And_ToolCall_Emit_Their_Events()
    {
        var faux = new FauxProvider(new FauxProviderOptions { MinTokenSize = 2, MaxTokenSize = 2 });
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(new ContentBlock[]
            {
                Faux.Thinking("hmm"),
                Faux.ToolCall("query_hp", new JsonObject { ["target"] = "player" }, "call_1"),
            }, StopReason.ToolUse)),
        });

        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var events = await CollectAsync(stream);

        Assert.Contains(events, e => e is AssistantMessageEvent.ThinkingStart);
        Assert.Contains(events, e => e is AssistantMessageEvent.ThinkingDelta);
        Assert.Contains(events, e => e is AssistantMessageEvent.ToolCallStart);
        Assert.Contains(events, e => e is AssistantMessageEvent.ToolCallDelta);
        Assert.Contains(events, e => e is AssistantMessageEvent.ToolCallEnd);
        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.ToolUse, done.Reason);
    }

    [Fact]
    public async Task Empty_Queue_Emits_Error()
    {
        var faux = new FauxProvider();
        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Contains("No more faux responses", done.ErrorMessage);
    }

    [Fact]
    public async Task Error_Message_Emits_Error_Event()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("bad", StopReason.Error, "boom")),
        });

        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var events = await CollectAsync(stream);

        Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Error, (await stream.WaitForDoneAsync()).StopReason);
    }

    [Fact]
    public async Task Factory_Receives_Context_And_State()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Factory((context, options, state, model) =>
                Task.FromResult(Faux.AssistantMessage("from-factory"))),
        });

        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Stop, done.StopReason);
        Assert.Equal(1, faux.State.CallCount);
    }

    [Fact]
    public async Task Cancellation_Emits_Aborted()
    {
        var faux = new FauxProvider(new FauxProviderOptions
        {
            MinTokenSize = 1,
            MaxTokenSize = 1,
            TokensPerSecond = 1000,
        });
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(new string('x', 50))),
        });

        using var cts = new CancellationTokenSource();
        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            cts.Token);
        cts.Cancel();

        var done = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Aborted, done.StopReason);
    }
}
```

- [ ] **Step 3.2: 运行测试确认失败**

Run:
```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo --filter "FullyQualifiedName~FauxStreamTests"
```
Expected: FAIL（Task 2 shell 只发 Done，没有 delta/thinking/toolcall；取消不会 Aborted）。

- [ ] **Step 3.3: 实现分块流式**

Replace the shell `EmitMessageAsync` in `FauxProvider.cs` with:

```csharp
    private async Task EmitMessageAsync(
        AssistantMessageEventStream stream,
        AssistantMessage final,
        CancellationToken cancellationToken)
    {
        var partial = final with
        {
            Content = Array.Empty<ContentBlock>(),
            StopReason = StopReason.Pending,
        };
        stream.Push(new AssistantMessageEvent.Start(partial));

        var built = new List<ContentBlock>();
        foreach (var block in final.Content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (block)
            {
                case TextContent text:
                    await EmitTextAsync(stream, final, built, text, cancellationToken).ConfigureAwait(false);
                    break;
                case ThinkingContent thinking:
                    await EmitThinkingAsync(stream, final, built, thinking, cancellationToken).ConfigureAwait(false);
                    break;
                case ToolCall call:
                    await EmitToolCallAsync(stream, final, built, call, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (final.StopReason is StopReason.Error or StopReason.Aborted)
        {
            stream.Push(new AssistantMessageEvent.Error(final.StopReason, final));
            stream.End(final);
            return;
        }
        if (final.StopReason == StopReason.Pending)
        {
            EmitError(stream, final, "Faux response ended without a stop reason");
            return;
        }

        stream.Push(new AssistantMessageEvent.Done(final.StopReason, final));
        stream.End(final);
    }

    private async Task EmitTextAsync(
        AssistantMessageEventStream stream,
        AssistantMessage final,
        List<ContentBlock> built,
        TextContent block,
        CancellationToken cancellationToken)
    {
        var index = built.Count;
        var current = new TextContent { Text = string.Empty };
        stream.Push(new AssistantMessageEvent.TextStart(index, Partial(final, built, current)));
        foreach (var chunk in Chunk(block.Text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DelayAsync(cancellationToken).ConfigureAwait(false);
            current = current with { Text = current.Text + chunk };
            stream.Push(new AssistantMessageEvent.TextDelta(index, chunk, Partial(final, built, current)));
        }
        stream.Push(new AssistantMessageEvent.TextEnd(index, current.Text, Partial(final, built, current)));
        built.Add(current);
    }

    private async Task EmitThinkingAsync(
        AssistantMessageEventStream stream,
        AssistantMessage final,
        List<ContentBlock> built,
        ThinkingContent block,
        CancellationToken cancellationToken)
    {
        var index = built.Count;
        var current = new ThinkingContent { Thinking = string.Empty, ThinkingSignature = block.ThinkingSignature, Redacted = block.Redacted };
        stream.Push(new AssistantMessageEvent.ThinkingStart(index, Partial(final, built, current)));
        foreach (var chunk in Chunk(block.Thinking))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DelayAsync(cancellationToken).ConfigureAwait(false);
            current = current with { Thinking = current.Thinking + chunk };
            stream.Push(new AssistantMessageEvent.ThinkingDelta(index, chunk, Partial(final, built, current)));
        }
        stream.Push(new AssistantMessageEvent.ThinkingEnd(index, current.Thinking, Partial(final, built, current)));
        built.Add(current);
    }

    private async Task EmitToolCallAsync(
        AssistantMessageEventStream stream,
        AssistantMessage final,
        List<ContentBlock> built,
        ToolCall block,
        CancellationToken cancellationToken)
    {
        var index = built.Count;
        var current = block with { Arguments = new JsonObject() };
        stream.Push(new AssistantMessageEvent.ToolCallStart(index, Partial(final, built, current)));
        foreach (var chunk in Chunk(block.Arguments.ToJsonString()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DelayAsync(cancellationToken).ConfigureAwait(false);
            stream.Push(new AssistantMessageEvent.ToolCallDelta(index, chunk, Partial(final, built, current)));
        }
        stream.Push(new AssistantMessageEvent.ToolCallEnd(index, block, Partial(final, built, block)));
        built.Add(block);
    }

    private AssistantMessage Partial(
        AssistantMessage final,
        List<ContentBlock> built,
        ContentBlock current)
        => final with
        {
            Content = built.Concat(new[] { current }).ToArray(),
            StopReason = StopReason.Pending,
        };

    private IEnumerable<string> Chunk(string text)
    {
        var size = Math.Max(1, Math.Min(_options.MinTokenSize, _options.MaxTokenSize));
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    private Task DelayAsync(CancellationToken cancellationToken)
        => _options.TokensPerSecond <= 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(1000.0 / _options.TokensPerSecond), cancellationToken);
```

Add `using System.Text.Json.Nodes;` and `using System.Linq;` to the file if not implicit.

- [ ] **Step 3.4: 运行测试确认通过**

Run:
```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo --filter "FullyQualifiedName~FauxStreamTests"
```
Expected: PASS（6 tests）。

- [ ] **Step 3.5: 运行完整测试**

Run:
```bash
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj --nologo
```
Expected: PASS（104 + 4 + 3 + 6 = 117 tests）。

- [ ] **Step 3.6: 提交**

```bash
git add src/PiUnityAgent.Ai/Providers/Faux/FauxProvider.cs \
        tests/PiUnityAgent.Ai.Tests/Providers/FauxStreamTests.cs
git commit -m "feat(ai): faux chunked streaming events"
```

---

## Task 4: 映射、文档与 Release 门禁

**Files:**
- Create: `src/PiUnityAgent.Ai/Providers/_mapping.md`
- Modify: `PORTING.md`
- Test: full solution

- [ ] **Step 4.1: 写 Providers 映射**

Create `src/PiUnityAgent.Ai/Providers/_mapping.md`:

```markdown
# Providers · 上游映射（pi v1.5.0）

| 上游 | 我们 | 说明 |
|---|---|---|
| `packages/ai/src/types.ts` `Model` / `BaseModel` | `Types/Model.cs` | 字段子集；无泛型 api 约束 |
| `packages/ai/src/models.ts` `Models` | `Providers/Models.cs` | 最小 chat 注册表；无 auth/刷新/图片/分类 |
| `packages/ai/src/providers/faux.ts` `createFauxCore` | `Providers/Faux/FauxProvider.cs` + `Faux.cs` | 脚本队列/工厂/状态；无 deferred |
| `pi.net Providers/Faux.cs` | 同名 | pi.net 的 auth/ModelSpec/Deferred 不采用 |
| `pi.net Models/Models.cs` | `Models.cs` | 最小注册表 |

## 2.1 适配

- Faux ID 用计数器，不用时间/随机；
- 时间用 `IClock`；
- 分块固定大小；
- 默认零延迟；`TokensPerSecond` 仅 Faux 选项；
- 无 HTTP/SSE/Auth。
```

- [ ] **Step 4.2: 更新 PORTING**

在 `PORTING.md` Ai 表里新增/更新：

```markdown
| `packages/ai/src/providers/faux.ts` | `src/PiUnityAgent.Ai/Providers/Faux/*` | ✅ | S3 Faux：脚本队列/确定性分块；deferred/auth 后置 |
| `packages/ai/src/models.ts` | `src/PiUnityAgent.Ai/Providers/Models.cs` | 🟡 | 最小 chat 注册表；auth/刷新/图片/分类后置 |
```

- [ ] **Step 4.3: 运行 Release 门禁**

Run:
```bash
dotnet build PiUnityAgent.slnx -c Release --nologo
dotnet test tests/PiUnityAgent.Ai.Tests/PiUnityAgent.Ai.Tests.csproj -c Release --nologo
```
Expected: build 0 warnings / 0 errors；tests all pass（117）。

- [ ] **Step 4.4: 提交**

```bash
git add src/PiUnityAgent.Ai/Providers/_mapping.md PORTING.md
git commit -m "docs: record S3 provider/faux mapping and gate"
```

---

## Self-Review

### Spec coverage

| Spec 要求 | 任务 |
|---|---|
| Model/ModelCost/ModelInputModality | Task 1 |
| SimpleStreamOptions | Task 1 |
| StreamFn 契约 | Task 1 |
| Provider / Models 注册表 | Task 1 |
| 未知 provider → error 事件 | Task 1 |
| Faux 默认模型/选项/脚本条目/状态 | Task 2 |
| Faux 文本/思考/工具调用/错误/空队列/工厂 | Task 3 |
| 确定性 ID/分块 | Task 2/3（ID 由调用方显式提供；分块固定） |
| IClock/时间 | Task 1/2 |
| 取消 → Aborted | Task 3 |
| Usage 估算 | Task 2/3 |
| 映射/门禁 | Task 4 |

### Placeholder scan

- 无 TBD/TODO/“similar to Task N”/“add appropriate error handling”。
- 每个代码步骤都有完整代码。
- Task 2 的 shell `EmitMessageAsync` 是**明确标注的过渡实现**，Task 3 替换它；不是占位符。

### Type consistency

- `FauxResponseStep.Response` / `Factory` 在 Task 2 定义，Task 3 测试使用一致。
- `FauxProvider.Provider.Stream` 指向 `StreamAsync`，Task 1 的 `Provider`/`StreamFn` 签名一致。
- `Faux.AssistantMessage` 的 `StopReason` 默认值、`ToolCall` 需要显式 id，测试一致。
- `SimpleStreamOptions` 不包含 `TokensPerSecond`；它在 `FauxProviderOptions`，测试一致。
