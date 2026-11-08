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
/// OpenAI Responses 协议适配器 MockFetch 验收（S4 Task 6 简报矩阵）：
/// 文本/reasoning/function call 参数累积/usage 与 service tier 定价/终止事件缺失/HTTP 错误，
/// 外加请求形状与 hooks、取消、429 重试、header 抑制与 StreamSimple 钩子透传。
/// </summary>
public class OpenAiResponsesTests
{
    private const string DefaultBaseUrl = "https://api.test.example/v1";

    private static Model CreateModel(bool reasoning = false)
        => new()
        {
            Id = "test-model",
            Name = "Test Model",
            Api = "openai-responses",
            Provider = "test",
            BaseUrl = DefaultBaseUrl,
            Cost = new ModelCost(3, 15, 0.3, 0.6),
            Reasoning = reasoning,
        };

    private static TranscriptContext CreateContext(params Message[] messages)
        => new()
        {
            Messages = messages.Length > 0
                ? messages
                : new Message[] { new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") } },
        };

    private static OpenAiResponsesOptions CreateOptions(MockFetch fetch, OpenAiResponsesOptions? preset = null)
        => (preset ?? new OpenAiResponsesOptions()) with { ApiKey = "test-key", Fetch = fetch.Fetch };

    private static string Sse(params string[] dataPayloads)
        => string.Concat(dataPayloads.Select(payload => $"data: {payload}\n\n"));

    private static IReadOnlyDictionary<string, string?> SseHeaders()
        => new Dictionary<string, string?> { ["content-type"] = "text/event-stream" };

    private static JsonObject ResponseEvent(string type)
        => new() { ["type"] = type };

    private static string CreatedEvent(string responseId = "resp_1")
    {
        var @event = ResponseEvent("response.created");
        @event["response"] = new JsonObject { ["id"] = responseId };
        return @event.ToJsonString();
    }

    private static string ItemAdded(int outputIndex, JsonObject item)
    {
        var @event = ResponseEvent("response.output_item.added");
        @event["output_index"] = outputIndex;
        @event["item"] = item;
        return @event.ToJsonString();
    }

    private static string ItemDone(int outputIndex, JsonObject item)
    {
        var @event = ResponseEvent("response.output_item.done");
        @event["output_index"] = outputIndex;
        @event["item"] = item;
        return @event.ToJsonString();
    }

    private static string TextDelta(int outputIndex, string delta)
    {
        var @event = ResponseEvent("response.output_text.delta");
        @event["output_index"] = outputIndex;
        @event["delta"] = delta;
        return @event.ToJsonString();
    }

    private static string MessageItem(string id, string status = "in_progress", JsonArray? content = null)
        => new JsonObject
        {
            ["type"] = "message",
            ["role"] = "assistant",
            ["id"] = id,
            ["status"] = status,
            ["content"] = content ?? new JsonArray(),
        }.ToJsonString();

    private static string OutputTextContent(string text)
        => new JsonObject
        {
            ["type"] = "output_text",
            ["text"] = text,
            ["annotations"] = new JsonArray(),
        }.ToJsonString();

    private static string CompletedEvent(JsonObject? response = null)
    {
        var @event = ResponseEvent("response.completed");
        @event["response"] = response ?? new JsonObject { ["id"] = "resp_1", ["status"] = "completed" };
        return @event.ToJsonString();
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
    public async Task Text_Response_Stream()
    {
        var fetch = new MockFetch();
        var completed = ResponseEvent("response.completed");
        completed["response"] = new JsonObject
        {
            ["id"] = "resp_1",
            ["status"] = "completed",
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 10,
                ["output_tokens"] = 5,
                ["total_tokens"] = 15,
            },
        };
        fetch.EnqueueText(
            Sse(
                CreatedEvent(),
                ItemAdded(0, (JsonObject)JsonNode.Parse(MessageItem("msg_1"))!),
                TextDelta(0, "Hello"),
                TextDelta(0, " world"),
                ItemDone(0, (JsonObject)JsonNode.Parse(MessageItem(
                    "msg_1",
                    "completed",
                    new JsonArray((JsonObject)JsonNode.Parse(OutputTextContent("Hello world"))!)))!),
                completed.ToJsonString()),
            headers: SseHeaders());

        var events = await CollectAsync(OpenAiResponses.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "text_delta", "text_end", "done" },
            EventTypes(events));

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.Stop, done.Reason);
        Assert.Equal("Hello world", Assert.IsType<TextContent>(done.Message.Content[0]).Text);
        Assert.Equal("resp_1", done.Message.ResponseId);
        Assert.Equal(10, done.Message.Usage.Input);
        Assert.Equal(5, done.Message.Usage.Output);
        Assert.Equal(15, done.Message.Usage.TotalTokens);
    }

    [Fact]
    public async Task Reasoning_Response_Stream()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                CreatedEvent(),
                ItemAdded(0, (JsonObject)JsonNode.Parse(
                    "{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[],\"content\":[]}")!),
                ReasoningDelta("response.reasoning_summary_text.delta", 0, "thinking "),
                ItemDone(0, (JsonObject)JsonNode.Parse(
                    "{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":"
                    + "[{\"type\":\"summary_text\",\"text\":\"thinking hard\"}],\"content\":[]}")!),
                ItemAdded(1, (JsonObject)JsonNode.Parse(MessageItem("msg_1"))!),
                TextDelta(1, "answer"),
                ItemDone(1, (JsonObject)JsonNode.Parse(MessageItem(
                    "msg_1",
                    "completed",
                    new JsonArray((JsonObject)JsonNode.Parse(OutputTextContent("answer"))!)))!),
                CompletedEvent()),
            headers: SseHeaders());

        var events = await CollectAsync(OpenAiResponses.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        // pi：output_item.done 立即补发 thinking_end/text_end（不同于 completions 的收尾统一补发）。
        Assert.Equal(
            new[]
            {
                "start", "thinking_start", "thinking_delta", "thinking_end",
                "text_start", "text_delta", "text_end", "done",
            },
            EventTypes(events));

        var thinkingStart = (AssistantMessageEvent.ThinkingStart)events[1];
        Assert.Equal(0, thinkingStart.ContentIndex);
        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        var thinking = Assert.IsType<ThinkingContent>(done.Message.Content[0]);
        // output_item.done 的 summary 文本整体替换流中累积文本（summaryText || contentText || 既有值）。
        Assert.Equal("thinking hard", thinking.Thinking);
        Assert.NotNull(thinking.ThinkingSignature);
        var signature = (JsonObject)JsonNode.Parse(thinking.ThinkingSignature!)!;
        Assert.Equal("reasoning", signature["type"]?.GetValue<string>());
        Assert.Equal("rs_1", signature["id"]?.GetValue<string>());
        Assert.Equal("answer", Assert.IsType<TextContent>(done.Message.Content[1]).Text);
    }

    private static string ReasoningDelta(string eventType, int outputIndex, string delta)
    {
        var @event = ResponseEvent(eventType);
        @event["output_index"] = outputIndex;
        @event["delta"] = delta;
        return @event.ToJsonString();
    }

    [Fact]
    public async Task Function_Call_Arguments_Accumulated()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                CreatedEvent(),
                ItemAdded(0, (JsonObject)JsonNode.Parse(
                    "{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\","
                    + "\"name\":\"get_weather\",\"arguments\":\"\"}")!),
                ArgumentsDelta(0, "{\"city\""),
                ArgumentsDelta(0, ":\"SF\"}"),
                ArgumentsDone(0, "{\"city\":\"SF\"}"),
                ItemDone(0, (JsonObject)JsonNode.Parse(
                    "{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\","
                    + "\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"SF\\\"}\"}")!),
                CompletedEvent()),
            headers: SseHeaders());

        var events = await CollectAsync(OpenAiResponses.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        // arguments.done 的全量文本与已累积文本一致 → 无新增 delta（pi replace 语义）。
        Assert.Equal(
            new[] { "start", "toolcall_start", "toolcall_delta", "toolcall_delta", "toolcall_end", "done" },
            EventTypes(events));

        var start = Assert.IsType<AssistantMessageEvent.ToolCallStart>(events[1]);
        Assert.Equal(0, start.ContentIndex);
        Assert.Equal("{\"city\"", Assert.IsType<AssistantMessageEvent.ToolCallDelta>(events[2]).Delta);
        var end = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(events[^2]);
        Assert.Equal(0, end.ContentIndex);
        // pi：block.id = `${item.call_id}|${item.id}`。
        Assert.Equal("call_1|fc_1", end.ToolCall.Id);
        Assert.Equal("get_weather", end.ToolCall.Name);
        Assert.Null(end.ToolCall.ArgumentsError);
        Assert.Equal("SF", end.ToolCall.Arguments["city"]?.GetValue<string>());

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.ToolUse, done.Reason);
    }

    private static string ArgumentsDelta(int outputIndex, string delta)
    {
        var @event = ResponseEvent("response.function_call_arguments.delta");
        @event["output_index"] = outputIndex;
        @event["delta"] = delta;
        return @event.ToJsonString();
    }

    private static string ArgumentsDone(int outputIndex, string arguments)
    {
        var @event = ResponseEvent("response.function_call_arguments.done");
        @event["output_index"] = outputIndex;
        @event["arguments"] = arguments;
        return @event.ToJsonString();
    }

    [Fact]
    public async Task Usage_And_ServiceTier()
    {
        var fetch = new MockFetch();
        var usage = new JsonObject
        {
            ["input_tokens"] = 100,
            ["output_tokens"] = 50,
            ["total_tokens"] = 150,
            ["input_tokens_details"] = new JsonObject
            {
                ["cached_tokens"] = 20,
                ["cache_write_tokens"] = 5,
            },
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 7 },
        };
        var completed = ResponseEvent("response.completed");
        completed["response"] = new JsonObject
        {
            ["id"] = "resp_1",
            ["status"] = "completed",
            ["service_tier"] = "flex",
            ["usage"] = usage,
        };

        fetch.EnqueueText(
            Sse(
                CreatedEvent(),
                ItemAdded(0, (JsonObject)JsonNode.Parse(MessageItem("msg_1"))!),
                TextDelta(0, "hi"),
                ItemDone(0, (JsonObject)JsonNode.Parse(MessageItem(
                    "msg_1", "completed", new JsonArray((JsonObject)JsonNode.Parse(OutputTextContent("hi"))!)))!),
                completed.ToJsonString()),
            headers: SseHeaders());

        var options = CreateOptions(fetch) with { ServiceTier = "flex" };
        var events = await CollectAsync(OpenAiResponses.Stream(
            CreateModel(), CreateContext(), options));

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        var result = done.Message.Usage;
        // OpenAI 把 cached/cache-write 计入 input_tokens：input = max(0, 100 - 20 - 5)。
        Assert.Equal(75, result.Input);
        Assert.Equal(50, result.Output);
        Assert.Equal(20, result.CacheRead);
        Assert.Equal(5, result.CacheWrite);
        Assert.Equal(7, result.Reasoning);
        Assert.Equal(150, result.TotalTokens);

        // flex 档 0.5 乘数作用于四项成本（pi applyServiceTierPricing）。
        Assert.Equal(3.0 / 1_000_000 * 75 * 0.5, result.Cost.Input, 12);
        Assert.Equal(15.0 / 1_000_000 * 50 * 0.5, result.Cost.Output, 12);
        Assert.Equal(0.3 / 1_000_000 * 20 * 0.5, result.Cost.CacheRead, 12);
        Assert.Equal(0.6 / 1_000_000 * 5 * 0.5, result.Cost.CacheWrite, 12);
        Assert.Equal(
            result.Cost.Input + result.Cost.Output + result.Cost.CacheRead + result.Cost.CacheWrite,
            result.Cost.Total, 12);
        Assert.False(result.Cost.TotalFromProvider ?? false);
    }

    [Fact]
    public async Task Missing_Stop_Is_Error()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                CreatedEvent(),
                ItemAdded(0, (JsonObject)JsonNode.Parse(MessageItem("msg_1"))!),
                TextDelta(0, "partial")),
            headers: SseHeaders());

        var events = await CollectAsync(OpenAiResponses.Stream(
            CreateModel(), CreateContext(), CreateOptions(fetch)));

        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains(
            "ended before a terminal response event", error.Message.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("partial", Assert.IsType<TextContent>(error.Message.Content[0]).Text);
    }

    [Fact]
    public async Task Http_Error_Becomes_Error_Terminal()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            "{\"error\":{\"message\":\"Invalid API key\",\"type\":\"invalid_request_error\"}}",
            status: 401,
            headers: new Dictionary<string, string?> { ["content-type"] = "application/json" });

        var events = await CollectAsync(OpenAiResponses.Stream(
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
    public async Task Request_Shape_Hooks_And_Headers()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                CreatedEvent(),
                ItemAdded(0, (JsonObject)JsonNode.Parse(MessageItem("msg_1"))!),
                TextDelta(0, "ok"),
                ItemDone(0, (JsonObject)JsonNode.Parse(MessageItem(
                    "msg_1", "completed", new JsonArray((JsonObject)JsonNode.Parse(OutputTextContent("ok"))!)))!),
                CompletedEvent()),
            headers: SseHeaders());

        JsonNode? payloadSeen = null;
        ProviderResponse? responseSeen = null;
        var streamEventCount = 0;
        var options = CreateOptions(fetch, new OpenAiResponsesOptions
        {
            MaxTokens = 5,
            SessionId = "session-1",
            // 钉死 retention，断言不受 PI_CACHE_RETENTION 环境变量影响。
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
                Interlocked.Increment(ref streamEventCount);
                return Task.CompletedTask;
            },
        });

        var events = await CollectAsync(OpenAiResponses.Stream(
            CreateModel(), CreateContext(), options));

        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "text_end", "done" },
            EventTypes(events));

        var request = Assert.Single(fetch.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal($"{DefaultBaseUrl}/responses", request.Url);
        Assert.Equal("Bearer test-key", request.Headers["authorization"]);
        Assert.Equal("application/json", request.Headers["content-type"]);
        Assert.Equal("text/event-stream", request.Headers["accept"]);
        Assert.StartsWith("pi/", request.Headers["user-agent"]);

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(Encoding.UTF8.GetString(request.Body.Span)));
        Assert.Equal("test-model", body["model"]?.GetValue<string>());
        Assert.True(body["stream"]?.GetValue<bool>());
        Assert.False(body["store"]?.GetValue<bool>());
        // pi#6265：max_output_tokens 下限 16（Math.Max(5, 16)）。
        Assert.Equal(16, body["max_output_tokens"]?.GetValue<int>());
        Assert.Equal("session-1", body["prompt_cache_key"]?.GetValue<string>());
        Assert.Equal("injected", body["extra"]?.GetValue<string>());
        var input = body["input"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("input missing");
        Assert.Equal("user", input[0]?["role"]?.GetValue<string>());
        Assert.Equal(
            "hi",
            input[0]?["content"]?.AsArray()[0]?["text"]?.GetValue<string>());

        Assert.NotNull(payloadSeen);
        Assert.NotNull(responseSeen);
        Assert.Equal(200, responseSeen.Status);
        Assert.Equal(5, streamEventCount);
    }

    [Fact]
    public async Task Cancel_Becomes_Aborted_And_Keeps_Partial()
    {
        var cts = new CancellationTokenSource();
        var fetch = new MockFetch();
        var head = Sse(
            CreatedEvent(),
            ItemAdded(0, (JsonObject)JsonNode.Parse(MessageItem("msg_1"))!),
            TextDelta(0, "partial"));
        fetch.Enqueue((_, _, _) => Task.FromResult<IFetchResponse>(new TestFetchResponse(
            200,
            new FetchHeaders(new Dictionary<string, string?> { ["content-type"] = "text/event-stream" }),
            new CancelBlockingStream(head, cts))));

        var options = CreateOptions(fetch) with { CancellationToken = cts.Token };
        var stream = OpenAiResponses.Stream(CreateModel(), CreateContext(), options);

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
        Assert.Equal("partial", Assert.IsType<TextContent>(error.Message.Content[0]).Text);
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
            Sse(
                CreatedEvent(),
                ItemAdded(0, (JsonObject)JsonNode.Parse(MessageItem("msg_1"))!),
                TextDelta(0, "recovered"),
                ItemDone(0, (JsonObject)JsonNode.Parse(MessageItem(
                    "msg_1", "completed", new JsonArray((JsonObject)JsonNode.Parse(OutputTextContent("recovered"))!)))!),
                CompletedEvent()),
            headers: SseHeaders());

        var options = CreateOptions(fetch) with { MaxRetries = 1 };
        var events = await CollectAsync(OpenAiResponses.Stream(
            CreateModel(), CreateContext(), options));

        // 429 在重试范围内（pi：SDK 在 retryProviderRequest 内抛非 2xx）→ 第二次尝试成功。
        Assert.Equal(2, fetch.Requests.Count);
        Assert.Equal($"{DefaultBaseUrl}/responses", fetch.Requests[0].Url);
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
        var events = await CollectAsync(OpenAiResponses.Stream(
            CreateModel(), CreateContext(), options));

        // 400 不可重试：即使 MaxRetries=3 也只调一次 Fetch，编码为单条 Error 终态。
        Assert.Single(fetch.Requests);
        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("400", error.Message.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("bad request", error.Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_Fetch_Becomes_Terminal_Error()
    {
        var options = new OpenAiResponsesOptions { ApiKey = "test-key" };

        var events = await CollectAsync(OpenAiResponses.Stream(CreateModel(), CreateContext(), options));

        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("Fetch", error.Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_Messages_Tools_And_Assistant_Replay_In_Body()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(CompletedEvent()), headers: SseHeaders());

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
                        // pi：thinking 块的签名是完整 reasoning item JSON，回放时原样推入 input。
                        new ThinkingContent
                        {
                            Thinking = "hidden reasoning",
                            ThinkingSignature =
                                "{\"type\":\"reasoning\",\"id\":\"rs_9\",\"summary\":"
                                + "[{\"type\":\"summary_text\",\"text\":\"reasoning replay\"}],"
                                + "\"encrypted_content\":\"ENC\"}",
                        },
                        new TextContent { Text = "Here you go." },
                        new ToolCall
                        {
                            Id = "call_9",
                            Name = "get_weather",
                            Arguments = new JsonObject { ["city"] = "SF" },
                        },
                    },
                    Api = "openai-responses",
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

        await CollectAsync(OpenAiResponses.Stream(
            CreateModel(), context, CreateOptions(fetch)));

        var body = Assert.IsType<JsonObject>(
            JsonNode.Parse(Encoding.UTF8.GetString(Assert.Single(fetch.Requests).Body.Span)));
        var input = body["input"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("input missing");
        Assert.Equal(6, input.Count);

        // 非 reasoning 模型：instruction role = system；system 文本进 input 首条。
        Assert.Equal("system", input[0]?["role"]?.GetValue<string>());
        Assert.Equal("You are helpful.", input[0]?["content"]?.GetValue<string>());

        Assert.Equal("user", input[1]?["role"]?.GetValue<string>());
        Assert.Equal(
            "input_text",
            input[1]?["content"]?.AsArray()[0]?["type"]?.GetValue<string>());

        // thinking 签名（reasoning item JSON）原样回放。
        var reasoningItem = Assert.IsType<JsonObject>(input[2]);
        Assert.Equal("reasoning", reasoningItem["type"]?.GetValue<string>());
        Assert.Equal("rs_9", reasoningItem["id"]?.GetValue<string>());
        Assert.Equal("ENC", reasoningItem["encrypted_content"]?.GetValue<string>());

        // 文本块 → message item；无签名时回退 id = msg_pi_{msgIndex}。
        var messageItem = Assert.IsType<JsonObject>(input[3]);
        Assert.Equal("message", messageItem["type"]?.GetValue<string>());
        Assert.Equal("assistant", messageItem["role"]?.GetValue<string>());
        Assert.Equal("msg_pi_1", messageItem["id"]?.GetValue<string>());
        Assert.Equal("completed", messageItem["status"]?.GetValue<string>());
        Assert.Equal(
            "Here you go.",
            messageItem["content"]?.AsArray()[0]?["text"]?.GetValue<string>());
        Assert.Empty(messageItem["content"]?.AsArray()[0]?["annotations"]?.AsArray()
            ?? throw new Xunit.Sdk.XunitException("annotations missing"));

        // 工具调用：同模型回放、id 无管道分隔 → item id 省略（call_id 保留）。
        var functionCall = Assert.IsType<JsonObject>(input[4]);
        Assert.Equal("function_call", functionCall["type"]?.GetValue<string>());
        Assert.Equal("call_9", functionCall["call_id"]?.GetValue<string>());
        Assert.Null(functionCall["id"]);
        Assert.Equal("get_weather", functionCall["name"]?.GetValue<string>());
        Assert.Equal("{\"city\":\"SF\"}", functionCall["arguments"]?.GetValue<string>());

        var functionOutput = Assert.IsType<JsonObject>(input[5]);
        Assert.Equal("function_call_output", functionOutput["type"]?.GetValue<string>());
        Assert.Equal("call_9", functionOutput["call_id"]?.GetValue<string>());
        Assert.Equal("Sunny", functionOutput["output"]?.GetValue<string>());

        var tools = body["tools"]?.AsArray() ?? throw new Xunit.Sdk.XunitException("tools missing");
        Assert.Single(tools);
        Assert.Equal("function", tools[0]?["type"]?.GetValue<string>());
        Assert.Equal("get_weather", tools[0]?["name"]?.GetValue<string>());
        // 非 strict-mode（compat 默认 false）不写 strict 字段。
        Assert.Null(tools[0]?["strict"]);
        Assert.Null(body["tool_choice"]);
    }

    [Fact]
    public async Task Reasoning_Request_Params()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(CompletedEvent()), headers: SseHeaders());

        var options = CreateOptions(fetch) with
        {
            ReasoningEffort = ThinkingLevel.Medium,
            MaxTokens = 5,
        };
        await CollectAsync(OpenAiResponses.Stream(
            CreateModel(reasoning: true), CreateContext(), options));

        var body = Assert.IsType<JsonObject>(
            JsonNode.Parse(Encoding.UTF8.GetString(Assert.Single(fetch.Requests).Body.Span)));
        Assert.Equal("medium", body["reasoning"]?["effort"]?.GetValue<string>());
        Assert.Equal("auto", body["reasoning"]?["summary"]?.GetValue<string>());
        Assert.Equal(
            "reasoning.encrypted_content",
            body["include"]?.AsArray()[0]?.GetValue<string>());
        Assert.Equal(16, body["max_output_tokens"]?.GetValue<int>());
    }

    [Fact]
    public async Task Header_Suppression_And_Override()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(CompletedEvent()), headers: SseHeaders());

        var options = new OpenAiResponsesOptions
        {
            Fetch = fetch.Fetch,
            Headers = new Dictionary<string, string?>
            {
                ["authorization"] = "Bearer alt-key",
                ["x-custom"] = "1",
                ["user-agent"] = null,
            },
        };

        await CollectAsync(OpenAiResponses.Stream(CreateModel(), CreateContext(), options));

        var request = Assert.Single(fetch.Requests);
        Assert.Equal("Bearer alt-key", request.Headers["authorization"]);
        Assert.Equal("1", request.Headers["x-custom"]);
        // null 抑制默认头：User-Agent（identity 头）被去掉。
        Assert.False(request.Headers.ContainsKey("user-agent"));
    }

    [Fact]
    public async Task StreamSimple_Forwards_Hooks()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            Sse(
                CreatedEvent(),
                ItemAdded(0, (JsonObject)JsonNode.Parse(MessageItem("msg_1"))!),
                TextDelta(0, "ok"),
                ItemDone(0, (JsonObject)JsonNode.Parse(MessageItem(
                    "msg_1", "completed", new JsonArray((JsonObject)JsonNode.Parse(OutputTextContent("ok"))!)))!),
                CompletedEvent()),
            headers: SseHeaders());

        JsonNode? payloadSeen = null;
        ProviderResponse? responseSeen = null;
        var streamEventCount = 0;
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
                Interlocked.Increment(ref streamEventCount);
                return Task.CompletedTask;
            },
        };

        var events = await CollectAsync(OpenAiResponses.StreamSimple(
            CreateModel(), CreateContext(), options));

        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "text_end", "done" },
            EventTypes(events));
        Assert.NotNull(payloadSeen);
        Assert.NotNull(responseSeen);
        Assert.Equal(200, responseSeen.Status);
        Assert.Equal(5, streamEventCount);

        var body = Assert.IsType<JsonObject>(
            JsonNode.Parse(Encoding.UTF8.GetString(Assert.Single(fetch.Requests).Body.Span)));
        Assert.Equal("simple", body["extra"]?.GetValue<string>());
        Assert.Equal(256, body["max_output_tokens"]?.GetValue<int>());
    }
}
