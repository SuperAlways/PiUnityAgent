using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Tests.Fakes;
using PiUnityAgent.Ai.Types;
using Xunit;

namespace PiUnityAgent.Ai.Tests.Providers;

/// <summary>
/// S4 后置能力承接验收（D12/D13）：
/// - D12 可用性单次解析：`GetAvailable*` 每 provider 只调用一次 `ApiKeyAuth.Check`（权威口径），
///   且 `AuthOperationOptions.CancellationToken` 传递到 Check；Check 存在即不回落 Resolve；不触进程 env；
/// - D13 Fetch 超时映射：provider 适配器把 `StreamOptions.TimeoutMs` 映射为
///   `FetchOptions.ConnectTimeout/FirstByteTimeout/IdleTimeout` 同值；
///   Fetch 抛 FirstByteTimeout/IdleTimeout → 终态 Error（消息透传，默认 MaxRetries=0 不重试）。
/// 「无默认 Fetch → 终态 Error」已由协议测试锁定：
/// `OpenAiCompletionsTests.Missing_Fetch_Becomes_Terminal_Error` /
/// `OpenAiResponsesTests.Missing_Fetch_Becomes_Terminal_Error`。
/// </summary>
public class S4DeferredCapabilityTests
{
    private const string DefaultBaseUrl = "https://api.test.example/v1";

    // ------------------------------------------------------------------
    // D12：可用性过滤每 provider 单次解析 + CT 传递
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAvailable_Checks_Each_Provider_Only_Once_And_Passes_CancellationToken()
    {
        var checkCount = 0;
        var resolveCount = 0;
        CancellationToken seenCt = default;
        using var cts = new CancellationTokenSource();
        var models = NewModels();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            id: "p1",
            auth: CheckedAuth(
                () => Interlocked.Increment(ref checkCount),
                ct => seenCt = ct,
                onResolve: () => Interlocked.Increment(ref resolveCount))));

        var available = await models.GetAvailableAsync(
            provider: null,
            new AuthOperationOptions { CancellationToken = cts.Token });

        Assert.Equal(1, checkCount);
        Assert.Equal(0, resolveCount); // Check 为权威口径：存在即不回落 Resolve
        var model = Assert.Single(available);
        Assert.Equal("m1", model.Id);
        Assert.Equal("p1", model.Provider);
        Assert.Equal(cts.Token, seenCt);
    }

    [Fact]
    public async Task GetAllAvailable_Checks_Each_Provider_Only_Once()
    {
        var checkCount = 0;
        var models = NewModels();
        models.SetProvider(ProviderTestSupport.CreateProvider(
            id: "p1",
            auth: CheckedAuth(() => Interlocked.Increment(ref checkCount), _ => { })));

        var all = await models.GetAllAvailableAsync();

        Assert.Equal(1, checkCount);
        var model = Assert.Single(all);
        Assert.Equal(ModelType.Chat, model.Type);
        Assert.Equal("p1", model.Provider);
    }

    private static Models NewModels()
        => new(new CreateModelsOptions
        {
            AuthContext = new StaticEnvAuthContext(new Dictionary<string, string>()),
        });

    /// <summary>Check 为权威口径（存在即不回落 Resolve）；Resolve 仅兜底，不应被触达。</summary>
    private static ProviderAuth CheckedAuth(
        Action onCheck,
        Action<CancellationToken> onCheckCt,
        Action? onResolve = null)
        => new()
        {
            ApiKey = new ApiKeyAuth
            {
                Name = "checked",
                Check = (_, ct) =>
                {
                    onCheck();
                    onCheckCt(ct);
                    return Task.FromResult<AuthCheck?>(new AuthCheck(AuthType.ApiKey, "checked"));
                },
                Resolve = (_, _) =>
                {
                    onResolve?.Invoke();
                    return Task.FromResult<AuthResult?>(
                        new AuthResult(new ModelAuth { ApiKey = "resolved" }, null, "resolver"));
                },
            },
        };

    private sealed class StaticEnvAuthContext(IReadOnlyDictionary<string, string> env) : IAuthContext
    {
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(env.TryGetValue(name, out var value) ? value : null);

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    // ------------------------------------------------------------------
    // D13：TimeoutMs → FetchOptions Connect/FirstByte/Idle 同值
    // ------------------------------------------------------------------

    [Fact]
    public async Task Completions_TimeoutMs_Maps_To_Uniform_Fetch_Timeouts()
    {
        var capture = new CapturingFetch(CompletionsSse());
        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel("openai-completions"),
            CreateContext(),
            new OpenAiCompletionsOptions { ApiKey = "test-key", TimeoutMs = 4321, Fetch = capture.Mock.Fetch }));

        AssertStreamCompletedNormally(events);
        AssertUniformTimeouts(capture.Seen!, TimeSpan.FromMilliseconds(4321));
    }

    [Fact]
    public async Task Responses_TimeoutMs_Maps_To_Uniform_Fetch_Timeouts()
    {
        var capture = new CapturingFetch(ResponsesSse());
        var events = await CollectAsync(OpenAiResponses.Stream(
            CreateModel("openai-responses"),
            CreateContext(),
            new OpenAiResponsesOptions { ApiKey = "test-key", TimeoutMs = 4321, Fetch = capture.Mock.Fetch }));

        AssertStreamCompletedNormally(events);
        AssertUniformTimeouts(capture.Seen!, TimeSpan.FromMilliseconds(4321));
    }

    [Fact]
    public async Task Completions_FirstByteTimeout_Becomes_Error_Terminal()
    {
        // D13 证据补强：Fetch 抛 FirstByteTimeout（传输层标可重试）→ 适配器终态 Error，
        // 消息透传「first byte」；默认 MaxRetries=0 ⇒ 即使 IsRetryable=true 也只打一发。
        var fetch = new MockFetch();
        fetch.EnqueueException(new FetchException(FetchErrorKind.FirstByteTimeout, "first byte timeout")
        {
            IsRetryable = true,
        });

        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel("openai-completions"),
            CreateContext(),
            new OpenAiCompletionsOptions { ApiKey = "test-key", Fetch = fetch.Fetch }));

        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("first byte", error.Message.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, fetch.CallCount);
    }

    [Fact]
    public async Task Responses_IdleTimeout_Becomes_Error_Terminal()
    {
        // 同上走 Responses 适配器：IdleTimeout 同样归一为终态 Error 且不重试。
        var fetch = new MockFetch();
        fetch.EnqueueException(new FetchException(FetchErrorKind.IdleTimeout, "idle timeout")
        {
            IsRetryable = true,
        });

        var events = await CollectAsync(OpenAiResponses.Stream(
            CreateModel("openai-responses"),
            CreateContext(),
            new OpenAiResponsesOptions { ApiKey = "test-key", Fetch = fetch.Fetch }));

        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("idle", error.Message.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, fetch.CallCount);
    }

    /// <summary>一次 Fetch 即回放固定 SSE 的 MockFetch 包装；捕获传给 Fetch 的 FetchOptions。</summary>
    private sealed class CapturingFetch
    {
        public CapturingFetch(string responseBody)
        {
            Mock.Enqueue((_, options, _) =>
            {
                Seen = options;
                return Task.FromResult<IFetchResponse>(new MockFetchResponse(
                    200, Encoding.UTF8.GetBytes(responseBody), SseFetchHeaders()));
            });
        }

        public MockFetch Mock { get; } = new();

        public FetchOptions? Seen { get; private set; }
    }

    private static void AssertStreamCompletedNormally(List<AssistantMessageEvent> events)
    {
        // 流必须正常收尾：本用例同时锁住 SSE 映射与 Done 终态。
        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.Stop, done.Reason);
    }

    private static void AssertUniformTimeouts(FetchOptions options, TimeSpan expected)
    {
        Assert.Equal(expected, options.ConnectTimeout);
        Assert.Equal(expected, options.FirstByteTimeout);
        Assert.Equal(expected, options.IdleTimeout);
    }

    // ------------------------------------------------------------------
    // 测试脚手架（与 OpenAiCompletionsTests/OpenAiResponsesTests 同构）
    // ------------------------------------------------------------------

    private static Model CreateModel(string api)
        => new()
        {
            Id = "test-model",
            Name = "Test Model",
            Api = api,
            Provider = "test",
            BaseUrl = DefaultBaseUrl,
            Cost = new ModelCost(3, 15, 0.3, 0.6),
        };

    private static TranscriptContext CreateContext()
        => new()
        {
            Messages = new Message[]
            {
                new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") },
            },
        };

    private static FetchHeaders SseFetchHeaders()
        => new(new Dictionary<string, string?> { ["content-type"] = "text/event-stream" });

    private static string Sse(params string[] payloads)
        => string.Concat(payloads.Select(payload => $"data: {payload}\n\n"));

    private static string CompletionsSse()
        => Sse(
            "{\"id\":\"chatcmpl-1\",\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}",
            "{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "[DONE]");

    private static string ResponsesSse()
    {
        var completed = new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = new JsonObject { ["id"] = "resp_1", ["status"] = "completed" },
        };
        return Sse(
            new JsonObject
            {
                ["type"] = "response.created",
                ["response"] = new JsonObject { ["id"] = "resp_1" },
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "response.output_item.added",
                ["output_index"] = 0,
                ["item"] = MessageItem("msg_1", "in_progress"),
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "response.output_text.delta",
                ["output_index"] = 0,
                ["delta"] = "hi",
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = 0,
                ["item"] = MessageItem("msg_1", "completed", new JsonArray(OutputTextContent("hi"))),
            }.ToJsonString(),
            completed.ToJsonString());
    }

    private static JsonObject MessageItem(string id, string status, JsonArray? content = null)
        => new()
        {
            ["type"] = "message",
            ["role"] = "assistant",
            ["id"] = id,
            ["status"] = status,
            ["content"] = content ?? new JsonArray(),
        };

    private static JsonObject OutputTextContent(string text)
        => new()
        {
            ["type"] = "output_text",
            ["text"] = text,
            ["annotations"] = new JsonArray(),
        };

    private static async Task<List<AssistantMessageEvent>> CollectAsync(IAssistantMessageEventStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            events.Add(@event);
        }

        return events;
    }
}
