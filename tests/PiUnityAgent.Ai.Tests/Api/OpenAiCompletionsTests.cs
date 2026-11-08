using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Api;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Tests.Fakes;
using PiUnityAgent.Ai.Types;
using Xunit;

namespace PiUnityAgent.Ai.Tests.Api;

/// <summary>
/// OpenAI Completions 协议适配器 MockFetch 验收（S4 Task 5 简报矩阵）：
/// 文本/推理/工具参数分片/usage 与成本/finish_reason 映射/HTTP 错误/取消，
/// 外加 Fetch 缺失、finish_reason 缺失与 hooks/请求形状。
/// </summary>
public class OpenAiCompletionsTests
{
    private const string DefaultBaseUrl = "https://api.test.example/v1";

    private static Model CreateModel()
        => new()
        {
            Id = "test-model",
            Name = "Test Model",
            Api = "openai-completions",
            Provider = "test",
            BaseUrl = DefaultBaseUrl,
            Cost = new ModelCost(3, 15, 0.3, 0.6),
        };

    private static TranscriptContext CreateContext(params Message[] messages)
        => new()
        {
            Messages = messages.Length > 0
                ? messages
                : new Message[] { new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") } },
        };

    private static OpenAiCompletionsOptions CreateOptions(MockFetch fetch, OpenAiCompletionsOptions? preset = null)
        => (preset ?? new OpenAiCompletionsOptions()) with { ApiKey = "test-key", Fetch = fetch.Fetch };

    private static string Sse(params string[] dataPayloads)
        => string.Concat(dataPayloads.Select(payload => $"data: {payload}\n\n")) + "data: [DONE]\n\n";

    private static IReadOnlyDictionary<string, string?> SseHeaders()
        => new Dictionary<string, string?> { ["content-type"] = "text/event-stream" };

    private static string ChoiceChunk(string deltaJson, string? finishReason = null, string? usageJson = null)
    {
        var choice = new JsonObject
        {
            ["index"] = 0,
            ["delta"] = JsonNode.Parse(deltaJson),
        };
        if (finishReason is not null)
        {
            choice["finish_reason"] = finishReason;
        }

        var chunk = new JsonObject { ["choices"] = new JsonArray(choice) };
        if (usageJson is not null)
        {
            chunk["usage"] = JsonNode.Parse(usageJson);
        }

        return chunk.ToJsonString();
    }

    private static List<string> EventTypes(IReadOnlyList<AssistantMessageEvent> events)
        => events.Select(@event => @event.Type).ToList();

    private static async Task<List<AssistantMessageEvent>> CollectAsync(IAssistantMessageEventStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            events.Add(@event);
        }

        return events;
    }

    /// <summary>自建 IFetchResponse：给协议测试一个可替换 Body 的响应替身。</summary>
    private sealed class TestFetchResponse : IFetchResponse
    {
        private readonly Stream _body;

        public TestFetchResponse(int statusCode, FetchHeaders headers, Stream body)
        {
            StatusCode = statusCode;
            Headers = headers;
            _body = body;
        }

        public int StatusCode { get; }
        public FetchHeaders Headers { get; }
        public Stream Body => _body;

        public void Abort() => _body.Dispose();

        public ValueTask DisposeAsync()
        {
            _body.Dispose();
            return default;
        }
    }

    /// <summary>读取时先按行回放一段固定内容、随后阻塞到取消令牌触发再抛 OCE 的流。</summary>
    private sealed class CancelBlockingStream : Stream
    {
        private readonly MemoryStream _head;
        private readonly CancellationTokenSource _cts;

        public CancelBlockingStream(string head, CancellationTokenSource cts)
        {
            _head = new MemoryStream(Encoding.UTF8.GetBytes(head));
            _cts = cts;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_head.Position < _head.Length)
            {
                return await _head.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = _cts.Token.Register(() => released.TrySetResult(true));
            await released.Task.ConfigureAwait(false);
            _cts.Token.ThrowIfCancellationRequested();
            return 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _head.Length;
        public override long Position { get => _head.Position; set => throw new NotSupportedException(); }
    }

    [Fact]
    public async Task Text_Stream_Ends_Done()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                "{\"id\":\"chatcmpl-1\",\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"}}]}",
                ChoiceChunk("{\"content\":\" world\"}"),
                ChoiceChunk("{}", finishReason: "stop", usageJson: "{\"prompt_tokens\":10,\"completion_tokens\":5}")),
            headers: SseHeaders());

        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "text_delta", "text_end", "done" },
            EventTypes(events));

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.Stop, done.Reason);
        Assert.Equal("Hello world", Assert.IsType<TextContent>(done.Message.Content[0]).Text);
        Assert.Equal("chatcmpl-1", done.Message.ResponseId);
        Assert.Equal(15, done.Message.Usage.TotalTokens);
    }

    [Fact]
    public async Task Reasoning_Is_Emitted_Before_Text()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                ChoiceChunk("{\"reasoning_content\":\"step one \"}"),
                ChoiceChunk("{\"reasoning_content\":\"step two\"}"),
                ChoiceChunk("{\"content\":\"answer\"}"),
                ChoiceChunk("{}", finishReason: "stop")),
            headers: SseHeaders());

        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        Assert.Equal(
            new[]
            {
                // pi 语义：thinking/text 块的 end 事件统一在流循环收尾时补发（finishBlock），
                // 因此 thinking_delta 之后可以紧跟 text_start —— 块创建顺序才是"先于"的锚。
                "start", "thinking_start", "thinking_delta", "thinking_delta",
                "text_start", "text_delta", "thinking_end", "text_end", "done",
            },
            EventTypes(events));

        var thinkingStart = (AssistantMessageEvent.ThinkingStart)events[1];
        Assert.Equal("reasoning_content", Assert.IsType<ThinkingContent>(thinkingStart.Partial.Content[0]).ThinkingSignature);
        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal("step one step two", Assert.IsType<ThinkingContent>(done.Message.Content[0]).Thinking);
        Assert.Equal("answer", Assert.IsType<TextContent>(done.Message.Content[1]).Text);
    }

    [Fact]
    public async Task Tool_Call_Arguments_Are_Accumulated()
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

        Assert.Equal(
            new[] { "start", "toolcall_start", "toolcall_delta", "toolcall_delta", "toolcall_delta", "toolcall_end", "done" },
            EventTypes(events));
        var start = Assert.IsType<AssistantMessageEvent.ToolCallStart>(events[1]);
        Assert.Equal(0, start.ContentIndex);
        Assert.Equal("{\"city\"", Assert.IsType<AssistantMessageEvent.ToolCallDelta>(events[3]).Delta);
        var end = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(events[^2]);
        Assert.Equal(0, end.ContentIndex);
        Assert.Equal("call_1", end.ToolCall.Id);
        Assert.Equal("get_weather", end.ToolCall.Name);
        Assert.Null(end.ToolCall.ArgumentsError);
        Assert.Equal("SF", end.ToolCall.Arguments["city"]?.GetValue<string>());

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.ToolUse, done.Reason);
    }

    [Fact]
    public async Task Usage_And_Cost_Are_Parsed()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                ChoiceChunk("{\"content\":\"hi\"}"),
                ChoiceChunk(
                    "{}",
                    finishReason: "stop",
                    usageJson:
                    "{\"prompt_tokens\":100,\"completion_tokens\":50,"
                    + "\"prompt_tokens_details\":{\"cached_tokens\":20,\"cache_write_tokens\":5}}")),
            headers: SseHeaders());

        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        var usage = done.Message.Usage;
        Assert.Equal(75, usage.Input);
        Assert.Equal(50, usage.Output);
        Assert.Equal(20, usage.CacheRead);
        Assert.Equal(5, usage.CacheWrite);
        Assert.Equal(150, usage.TotalTokens);

        Assert.Equal(3.0 / 1_000_000 * 75, usage.Cost.Input, 12);
        Assert.Equal(15.0 / 1_000_000 * 50, usage.Cost.Output, 12);
        Assert.Equal(0.3 / 1_000_000 * 20, usage.Cost.CacheRead, 12);
        Assert.Equal(0.6 / 1_000_000 * 5, usage.Cost.CacheWrite, 12);
        Assert.Equal(
            usage.Cost.Input + usage.Cost.Output + usage.Cost.CacheRead + usage.Cost.CacheWrite,
            usage.Cost.Total, 12);
        Assert.False(usage.Cost.TotalFromProvider ?? false);
    }

    [Theory]
    [InlineData("length", "length")]
    [InlineData("tool_calls", "toolUse")]
    [InlineData("content_filter", "error")]
    public async Task Finish_Reason_Maps_To_Stop_Reason(string finishReason, string expectedWire)
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(ChoiceChunk("{\"content\":\"x\"}"), ChoiceChunk("{}", finishReason: finishReason)),
            headers: SseHeaders());

        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        var terminal = events[^1];
        switch (expectedWire)
        {
            case "length":
                var done = Assert.IsType<AssistantMessageEvent.Done>(terminal);
                Assert.Equal(StopReason.Length, done.Reason);
                Assert.Equal("length", done.Message.RawStopReason);
                break;
            case "toolUse":
                var toolDone = Assert.IsType<AssistantMessageEvent.Done>(terminal);
                Assert.Equal(StopReason.ToolUse, toolDone.Reason);
                break;
            default:
                var error = Assert.IsType<AssistantMessageEvent.Error>(terminal);
                Assert.Equal(StopReason.Error, error.Reason);
                Assert.Equal("Provider finish_reason: content_filter", error.Message.ErrorMessage);
                break;
        }
    }

    [Fact]
    public async Task Http_Error_Becomes_Error_Terminal()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            "{\"error\":{\"message\":\"Invalid API key\",\"type\":\"invalid_request_error\"}}",
            status: 401,
            headers: new Dictionary<string, string?> { ["content-type"] = "application/json" });

        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("401", error.Message.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Invalid API key", error.Message.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("test", error.Message.Provider);
        Assert.Equal("test-model", error.Message.Model);
        Assert.Empty(error.Message.Content);
    }

    [Fact]
    public async Task Cancel_Becomes_Aborted_And_Keeps_Partial()
    {
        var cts = new CancellationTokenSource();
        var fetch = new MockFetch();
        var head = Sse(ChoiceChunk("{\"content\":\"partial\"}")) + ": keep-alive\n\n";
        fetch.Enqueue((_, _, _) => Task.FromResult<IFetchResponse>(new TestFetchResponse(
            200,
            new FetchHeaders(new Dictionary<string, string?> { ["content-type"] = "text/event-stream" }),
            new CancelBlockingStream(head, cts))));

        var options = CreateOptions(fetch);
        options = options with { CancellationToken = cts.Token };
        var stream = OpenAiCompletions.Stream(CreateModel(), CreateContext(), options);

        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            events.Add(@event);
            if (@event is AssistantMessageEvent.TextDelta)
            {
                cts.Cancel();
            }
        }

        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "error" },
            EventTypes(events));
        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Aborted, error.Reason);
        Assert.Equal("Request was aborted", error.Message.ErrorMessage);
        var text = Assert.IsType<TextContent>(error.Message.Content[0]);
        Assert.Equal("partial", text.Text);
    }

    [Fact]
    public async Task Stream_Without_Finish_Reason_Becomes_Error()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(ChoiceChunk("{\"content\":\"x\"}")), headers: SseHeaders());

        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Equal("Stream ended without finish_reason", error.Message.ErrorMessage);
    }

    [Fact]
    public async Task Missing_Fetch_Becomes_Terminal_Error()
    {
        var options = new OpenAiCompletionsOptions { ApiKey = "test-key" };

        var events = await CollectAsync(OpenAiCompletions.Stream(CreateModel(), CreateContext(), options));

        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("Fetch", error.Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_Shape_Hooks_And_Headers()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(ChoiceChunk("{\"content\":\"ok\"}"), ChoiceChunk("{}", finishReason: "stop")),
            headers: SseHeaders());

        JsonNode? payloadSeen = null;
        ProviderResponse? responseSeen = null;
        var streamChunkCount = 0;
        var options = CreateOptions(fetch, new OpenAiCompletionsOptions
        {
            MaxTokens = 512,
            SessionId = "session-1",
            // 钉死 retention，断言不受 PI_CACHE_RETENTION 环境变量影响（long 会写入 prompt_cache_key）。
            CacheRetention = CacheRetention.Short,
            OnPayload = (payload, _) =>
            {
                payloadSeen = payload?.DeepClone();
                if (payload is JsonObject obj)
                {
                    obj["extra"] = "injected";
                }

                return Task.FromResult<JsonNode?>(payload);
            },
            OnResponse = (response, _) =>
            {
                responseSeen = response;
                return Task.CompletedTask;
            },
            OnProviderStreamEvent = (_, _) =>
            {
                Interlocked.Increment(ref streamChunkCount);
                return Task.CompletedTask;
            },
        });

        var events = await CollectAsync(OpenAiCompletions.Stream(CreateModel(), CreateContext(), options));

        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "text_end", "done" },
            EventTypes(events));

        var request = Assert.Single(fetch.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal($"{DefaultBaseUrl}/chat/completions", request.Url);
        Assert.Equal("Bearer test-key", request.Headers["authorization"]);
        Assert.Equal("application/json", request.Headers["content-type"]);
        Assert.Equal("text/event-stream", request.Headers["accept"]);
        Assert.StartsWith("pi/", request.Headers["user-agent"]);

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(Encoding.UTF8.GetString(request.Body.Span)));
        Assert.Equal("test-model", body["model"]?.GetValue<string>());
        Assert.True(body["stream"]?.GetValue<bool>());
        Assert.True(body["stream_options"]?["include_usage"]?.GetValue<bool>());
        Assert.Equal(512, body["max_completion_tokens"]?.GetValue<int>());
        Assert.Equal("injected", body["extra"]?.GetValue<string>());
        Assert.Null(body["prompt_cache_key"]);
        Assert.Equal("user", body["messages"]?.AsArray()[0]?["role"]?.GetValue<string>());

        Assert.NotNull(payloadSeen);
        Assert.NotNull(responseSeen);
        Assert.Equal(200, responseSeen.Status);
        Assert.Equal(2, streamChunkCount);
    }

    [Fact]
    public async Task Header_Suppression_And_Override()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(ChoiceChunk("{}", finishReason: "stop")), headers: SseHeaders());

        var options = new OpenAiCompletionsOptions
        {
            Fetch = fetch.Fetch,
            Headers = new Dictionary<string, string?>
            {
                ["authorization"] = "Bearer alt-key",
                ["x-custom"] = "1",
                ["user-agent"] = null,
            },
        };

        await CollectAsync(OpenAiCompletions.Stream(CreateModel(), CreateContext(), options));

        var request = Assert.Single(fetch.Requests);
        Assert.Equal("Bearer alt-key", request.Headers["authorization"]);
        Assert.Equal("1", request.Headers["x-custom"]);
        // null 抑制默认头：User-Agent（identity 头）被去掉。
        Assert.False(request.Headers.ContainsKey("user-agent"));
    }

    [Fact]
    public async Task System_Messages_Tools_And_Assistant_Replay_In_Body()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(ChoiceChunk("{}", finishReason: "stop")), headers: SseHeaders());

        var tool = new Tool
        {
            Name = "get_weather",
            Description = "Get weather",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["city"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("city"),
            },
        };
        var context = new TranscriptContext
        {
            Messages = new Message[]
            {
                new SystemMessage
                {
                    Timestamp = 0,
                    Content = MessageContent.From("You are helpful."),
                    ToolsAdded = new[] { tool },
                },
                new UserMessage { Timestamp = 1, Content = MessageContent.From("What is the weather?") },
                new AssistantMessage
                {
                    Timestamp = 2,
                    Content = new ContentBlock[]
                    {
                        new ThinkingContent { Thinking = "let me think", ThinkingSignature = "reasoning_content" },
                        new ToolCall
                        {
                            Id = "call_9",
                            Name = "get_weather",
                            Arguments = new JsonObject { ["city"] = "SF" },
                        },
                    },
                    Api = "openai-completions",
                    Provider = "test",
                    Model = "test-model",
                    Usage = new Usage(),
                    StopReason = StopReason.ToolUse,
                },
                new ToolResultMessage
                {
                    Timestamp = 3,
                    ToolCallId = "call_9",
                    ToolName = "get_weather",
                    Content = new ContentBlock[] { new TextContent { Text = "Sunny" } },
                    IsError = false,
                },
            },
        };

        await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), context, CreateOptions(fetch)));

        var body = Assert.IsType<JsonObject>(
            JsonNode.Parse(Encoding.UTF8.GetString(Assert.Single(fetch.Requests).Body.Span)));
        var messages = body["messages"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("messages missing");
        Assert.Equal(4, messages.Count);

        // System 消息首条为 system role，内容带 prompt 文本；工具进顶层 tools。
        Assert.Equal("system", messages[0]?["role"]?.GetValue<string>());
        Assert.Equal("You are helpful.", messages[0]?["content"]?.GetValue<string>());
        Assert.Equal("user", messages[1]?["role"]?.GetValue<string>());
        Assert.Equal("What is the weather?", messages[1]?["content"]?.GetValue<string>());

        var assistant = Assert.IsType<JsonObject>(messages[2]);
        Assert.Equal("assistant", assistant["role"]?.GetValue<string>());
        Assert.Equal("let me think", assistant["reasoning_content"]?.GetValue<string>());
        var toolCalls = assistant["tool_calls"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("tool_calls missing");
        Assert.Equal("function", toolCalls[0]?["type"]?.GetValue<string>());
        Assert.Equal("get_weather", toolCalls[0]?["function"]?["name"]?.GetValue<string>());
        Assert.Equal("call_9", toolCalls[0]?["id"]?.GetValue<string>());
        Assert.Equal("{\"city\":\"SF\"}", toolCalls[0]?["function"]?["arguments"]?.GetValue<string>());

        var toolResult = Assert.IsType<JsonObject>(messages[3]);
        Assert.Equal("tool", toolResult["role"]?.GetValue<string>());
        Assert.Equal("call_9", toolResult["tool_call_id"]?.GetValue<string>());
        Assert.Equal("Sunny", toolResult["content"]?.GetValue<string>());

        var tools = body["tools"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("tools missing");
        Assert.Single(tools);
        Assert.Equal("function", tools[0]?["type"]?.GetValue<string>());
        Assert.Equal("get_weather", tools[0]?["function"]?["name"]?.GetValue<string>());
        // 非 strict-mode provider（默认）不写 strict 字段。
        Assert.Null(tools[0]?["function"]?["strict"]);
    }

    [Fact]
    public async Task Http_429_Is_Retried_Inside_ProviderRetry()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            "{\"error\":{\"message\":\"rate limited\"}}",
            status: 429,
            headers: new Dictionary<string, string?> { ["retry-after-ms"] = "1" });
        fetch.EnqueueText(
            Sse(ChoiceChunk("{\"content\":\"recovered\"}"), ChoiceChunk("{}", finishReason: "stop")),
            headers: SseHeaders());

        var options = CreateOptions(fetch) with { MaxRetries = 1 };
        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), CreateContext(), options));

        // 429 在重试范围内（pi：SDK 在 retryProviderRequest 内抛非 2xx）→ 第二次尝试成功。
        Assert.Equal(2, fetch.Requests.Count);
        Assert.Equal($"{DefaultBaseUrl}/chat/completions", fetch.Requests[0].Url);
        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.Stop, done.Reason);
        Assert.Equal("recovered", Assert.IsType<TextContent>(done.Message.Content[0]).Text);
    }

    [Fact]
    public async Task Http_400_Is_Not_Retried()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            "{\"error\":{\"message\":\"bad request\"}}",
            status: 400,
            headers: new Dictionary<string, string?> { ["content-type"] = "application/json" });

        var options = CreateOptions(fetch) with { MaxRetries = 3 };
        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), CreateContext(), options));

        // 400 不可重试：即使 MaxRetries=3 也只调一次 Fetch，编码为单条 Error 终态。
        Assert.Single(fetch.Requests);
        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("400", error.Message.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("bad request", error.Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamSimple_Forwards_Hooks()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(ChoiceChunk("{\"content\":\"ok\"}"), ChoiceChunk("{}", finishReason: "stop")),
            headers: SseHeaders());

        JsonNode? payloadSeen = null;
        ProviderResponse? responseSeen = null;
        var streamChunkCount = 0;
        var options = new SimpleStreamOptions
        {
            ApiKey = "test-key",
            MaxTokens = 256,
            Fetch = fetch.Fetch,
            OnPayload = (payload, _) =>
            {
                payloadSeen = payload?.DeepClone();
                if (payload is JsonObject obj)
                {
                    obj["extra"] = "simple";
                }

                return Task.FromResult<JsonNode?>(payload);
            },
            OnResponse = (response, _) =>
            {
                responseSeen = response;
                return Task.CompletedTask;
            },
            OnProviderStreamEvent = (_, _) =>
            {
                Interlocked.Increment(ref streamChunkCount);
                return Task.CompletedTask;
            },
        };

        var events = await CollectAsync(OpenAiCompletions.StreamSimple(
            CreateModel(), CreateContext(), options));

        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "text_end", "done" },
            EventTypes(events));
        Assert.NotNull(payloadSeen);
        Assert.NotNull(responseSeen);
        Assert.Equal(200, responseSeen.Status);
        Assert.Equal(2, streamChunkCount);

        var body = Assert.IsType<JsonObject>(
            JsonNode.Parse(Encoding.UTF8.GetString(Assert.Single(fetch.Requests).Body.Span)));
        Assert.Equal("simple", body["extra"]?.GetValue<string>());
        Assert.Equal(256, body["max_completion_tokens"]?.GetValue<int>());
    }

    [Fact]
    public async Task Invalid_Tool_Arguments_Json_Falls_To_ArgumentsError_Not_Terminal()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_bad\","
                + "\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"\"}}]}}]}",
                "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":"
                + "{\"arguments\":\"{city: SF}\"}}]}}]}",
                ChoiceChunk("{}", finishReason: "tool_calls")),
            headers: SseHeaders());

        var events = await CollectAsync(OpenAiCompletions.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        // pi parseToolCallArguments 语义：非法 JSON 落 block.argumentsError，流照常 Done（非 Error 终态）。
        Assert.Equal(
            new[] { "start", "toolcall_start", "toolcall_delta", "toolcall_delta", "toolcall_end", "done" },
            EventTypes(events));

        var end = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(events[^2]);
        Assert.Equal("call_bad", end.ToolCall.Id);
        Assert.NotNull(end.ToolCall.ArgumentsError);
        Assert.Contains("Received arguments", end.ToolCall.ArgumentsError!, StringComparison.Ordinal);
        Assert.Empty(end.ToolCall.Arguments);

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.ToolUse, done.Reason);
    }

    [Fact]
    public async Task Streams_Stream_Accepts_Base_StreamOptions_And_Keeps_Auth()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                ChoiceChunk("{\"content\":\"hi\"}"),
                ChoiceChunk("{}", finishReason: "stop")),
            headers: SseHeaders());
        using var cts = new CancellationTokenSource();

        // 门面 Models.Stream 走 base StreamOptions（applyAuth 之后）——适配器不得 `as` 直转丢认证。
        var events = await CollectAsync(OpenAiCompletions.Streams().Stream(
            CreateModel(),
            CreateContext(),
            new StreamOptions
            {
                ApiKey = "test-key",
                MaxTokens = 32,
                Fetch = fetch.Fetch,
                CancellationToken = cts.Token,
            }));

        Assert.Equal("Bearer test-key", fetch.Requests[0].Headers["authorization"]);
        Assert.Null(events.OfType<AssistantMessageEvent.Error>().FirstOrDefault());
        Assert.Equal(StopReason.Stop, Assert.IsType<AssistantMessageEvent.Done>(events[^1]).Reason);
    }
}
