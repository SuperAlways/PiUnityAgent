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
using PiUnityAgent.Ai.Tests.Fakes;
using PiUnityAgent.Ai.Types;
using Xunit;

namespace PiUnityAgent.Ai.Tests.Api;

/// <summary>
/// Anthropic Messages 协议适配器请求形状验收（S5 Task 2 简报矩阵）：
/// URL/头（x-api-key、Bearer OAuth、anthropic-version、anthropic-beta、identity、null 抑制）、
/// body（model/max_tokens/messages/system/tools/thinking/cache_control）、
/// system/user/assistant/toolResult 转换、thinking 签名回放、tool_use id 归一化、
/// strict 工具、Fetch 缺失终态、hooks 时序。SSE 事件解析归 Task 3。
/// </summary>
public class AnthropicMessagesTests
{
    private const string DefaultBaseUrl = "https://api.test.example";

    private static Model CreateModel(string? baseUrl = null, JsonObject? compat = null)
        => new()
        {
            Id = "claude-test",
            Name = "Claude Test",
            Api = "anthropic-messages",
            Provider = "anthropic",
            BaseUrl = baseUrl ?? DefaultBaseUrl,
            Reasoning = true,
            Input = new[] { ModelInputModality.Text, ModelInputModality.Image },
            Compat = compat,
        };

    private static TranscriptContext Context(params Message[] messages)
        => new() { Messages = messages };

    private static SystemMessage SystemMsg(string text, IReadOnlyList<Tool>? toolsAdded = null)
        => new()
        {
            Content = MessageContent.From(text),
            ToolsAdded = toolsAdded,
            Timestamp = 1,
        };

    private static UserMessage UserMsg(string text)
        => new() { Timestamp = 2, Content = MessageContent.From(text) };

    private static Tool Tool(string name = "get_weather", bool constrained = false)
        => new()
        {
            Name = name,
            Description = $"The {name} tool",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["city"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("city"),
            },
            ConstrainedSampling = constrained
                ? new ConstrainedSampling.Config(new ConstrainedSamplingConfig.JsonSchema(StrictMode.Require))
                : null,
        };

    private static AnthropicOptions CreateOptions(MockFetch fetch, AnthropicOptions? preset = null)
        => (preset ?? new AnthropicOptions()) with { ApiKey = "test-key", Fetch = fetch.Fetch };

    private static IReadOnlyDictionary<string, string?> SseHeaders()
        => new Dictionary<string, string?> { ["content-type"] = "text/event-stream" };

    private static string Sse(params string[] dataPayloads)
        => string.Concat(dataPayloads.Select(payload => $"data: {payload}\n\n"));

    // ------------------------------------------------------------------
    // Anthropic 命名 SSE 事件构造（真实线路是 event: 行 + data 行；Task 3 按 event 字段分派）
    // ------------------------------------------------------------------

    private static string SseNamed(string eventName, JsonObject data)
        => $"event: {eventName}\ndata: {data.ToJsonString()}\n\n";

    private static string AnthropicSse(params string[] events) => string.Concat(events);

    private static string MessageStart(
        string responseId = "msg_1",
        string? responseModel = null,
        JsonObject? usage = null)
        => SseNamed("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = responseId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = responseModel ?? "claude-test",
                ["usage"] = usage ?? new JsonObject { ["input_tokens"] = 4, ["output_tokens"] = 1 },
            },
        });

    private static string ContentBlockStart(int index, JsonObject block)
        => SseNamed("content_block_start", new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = index,
            ["content_block"] = block,
        });

    private static string ContentBlockDelta(int index, JsonObject delta)
        => SseNamed("content_block_delta", new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = index,
            ["delta"] = delta,
        });

    private static string TextDelta(int index, string text)
        => ContentBlockDelta(index, new JsonObject { ["type"] = "text_delta", ["text"] = text });

    private static string ThinkingDelta(int index, string thinking)
        => ContentBlockDelta(index, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinking });

    private static string SignatureDelta(int index, string signature)
        => ContentBlockDelta(index, new JsonObject { ["type"] = "signature_delta", ["signature"] = signature });

    private static string JsonDelta(int index, string partialJson)
        => ContentBlockDelta(index, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = partialJson });

    private static string ContentBlockStop(int index)
        => SseNamed("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index });

    private static string MessageDelta(string? stopReason, JsonObject? usage = null, JsonObject? stopDetails = null)
    {
        var delta = new JsonObject();
        if (stopReason is not null) delta["stop_reason"] = stopReason;
        if (stopDetails is not null) delta["stop_details"] = stopDetails;
        var @event = new JsonObject { ["type"] = "message_delta", ["delta"] = delta };
        if (usage is not null) @event["usage"] = usage;
        return SseNamed("message_delta", @event);
    }

    private static string MessageStop()
        => SseNamed("message_stop", new JsonObject { ["type"] = "message_stop" });

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

    private static JsonObject Body(MockFetch fetch, int index = 0)
        => (JsonNode.Parse(fetch.Requests[index].Body.ToArray()) as JsonObject)!;

    // ------------------------------------------------------------------
    // 请求形状：URL / 头 / body 顶层字段
    // ------------------------------------------------------------------

    [Fact]
    public async Task Request_Uses_Anthropic_Headers_And_Tool_Blocks()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var context = Context(SystemMsg("You are helpful.", toolsAdded: new[] { Tool() }), UserMsg("hi"));
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), context, CreateOptions(fetch, new AnthropicOptions
            {
                MaxTokens = 64,
                ThinkingEnabled = true,
                ThinkingBudgetTokens = 1024,
            })));

        var request = fetch.Requests[0];
        Assert.EndsWith("/v1/messages", request.Url);
        Assert.True(request.Headers.ContainsKey("x-api-key"));
        Assert.Equal("test-key", request.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"]);

        var body = Body(fetch);
        Assert.Equal("claude-test", body["model"]!.GetValue<string>());
        Assert.Equal(64, body["max_tokens"]!.GetValue<int>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.NotNull(body["tools"]);
        Assert.NotNull(body["thinking"]);
        Assert.Equal("enabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(1024, body["thinking"]!["budget_tokens"]!.GetValue<int>());
        // system 折叠进 system 字段，不在 messages 里。
        Assert.NotNull(body["system"]);
        Assert.Equal("You are helpful.", body["system"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.DoesNotContain(body["messages"]!.AsArray(), message => message!["role"]!.GetValue<string>() == "system");
    }

    [Fact]
    public async Task BaseUrl_Trailing_Slash_Is_Trimmed()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel("https://api.minimax.io/anthropic/"), Context(UserMsg("hi")), CreateOptions(fetch)));
        Assert.Equal("https://api.minimax.io/anthropic/v1/messages", fetch.Requests[0].Url);
    }

    [Fact]
    public async Task OAuth_Token_Uses_Bearer_And_Beta_Headers_With_Claude_Code_System()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(SystemMsg("Be brief."), UserMsg("hi")),
            CreateOptions(fetch) with { ApiKey = "sk-ant-oat01-example" }));

        var request = fetch.Requests[0];
        Assert.Equal("Bearer sk-ant-oat01-example", request.Headers["authorization"]);
        Assert.False(request.Headers.ContainsKey("x-api-key"));
        // OAuth：Claude Code beta + oauth beta 必带；body 不携带 betas（SDK 把它转成头）。
        Assert.Equal("claude-code-20250219,oauth-2025-04-20", request.Headers["anthropic-beta"]);
        var body = Body(fetch);
        Assert.Null(body["betas"]);
        var system = body["system"]!.AsArray();
        Assert.Equal(
            "You are Claude Code, Anthropic's official CLI for Claude.",
            system[0]!["text"]!.GetValue<string>());
        Assert.Equal(2, system.Count);
        Assert.Equal("Be brief.", system[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Headers_Respect_Model_And_Request_Overrides_With_Null_Suppression()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var model = CreateModel() with
        {
            Headers = new Dictionary<string, string> { ["anthropic-version"] = "2099-01-01" },
        };
        var events = await CollectAsync(AnthropicMessages.Stream(
            model,
            Context(UserMsg("hi")),
            CreateOptions(fetch) with
            {
                Headers = new Dictionary<string, string?>
                {
                    ["anthropic-version"] = null, // null 抑制：连 model 头一起删。
                    ["x-custom"] = "1",
                },
            }));

        // 空 SSE 体 → start 推出后流终止于 Error（pi：start 在消费前推）。
        Assert.Equal(new[] { "start", "error" }, events.Select(@event => @event.Type));
        Assert.False(fetch.Requests[0].Headers.ContainsKey("anthropic-version"));
        Assert.Equal("1", fetch.Requests[0].Headers["x-custom"]);
    }

    [Fact]
    public async Task Identity_Headers_Applied_And_Overridable()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var identity = new ClientIdentity
        {
            UserAgent = UserAgentMode.Custom("custom-agent/1.0"),
            Attribution = null,
        };
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with { Identity = identity }));
        Assert.Equal("custom-agent/1.0", fetch.Requests[0].Headers["user-agent"]);
    }

    // ------------------------------------------------------------------
    // thinking / max_tokens 关系
    // ------------------------------------------------------------------

    [Fact]
    public async Task Thinking_Budget_Defaults_To_1024_And_Temperature_Dropped_When_Thinking()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with { MaxTokens = 256, ThinkingEnabled = true, Temperature = 0.5 }));

        var body = Body(fetch);
        Assert.Equal("enabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(1024, body["thinking"]!["budget_tokens"]!.GetValue<int>());
        Assert.Null(body["temperature"]);
    }

    [Fact]
    public async Task Thinking_Explicitly_Disabled_Sends_Disabled_And_Keeps_Temperature()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with { ThinkingEnabled = false, Temperature = 0.7 }));

        var body = Body(fetch);
        Assert.Equal("disabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(0.7, body["temperature"]!.GetValue<double>());
    }

    [Fact]
    public async Task Force_Adaptive_Thinking_Compat_Uses_Adaptive()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(compat: new JsonObject { ["forceAdaptiveThinking"] = true }),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with
            {
                ThinkingEnabled = true,
                Effort = ThinkingLevel.High,
                ThinkingDisplay = AnthropicThinkingDisplay.Omitted,
            }));

        var body = Body(fetch);
        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("omitted", body["thinking"]!["display"]!.GetValue<string>());
        Assert.Equal("high", body["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task MaxTokens_Falls_Back_To_Model_Cap()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(CreateModel(), Context(UserMsg("hi")), CreateOptions(fetch)));
        Assert.Equal(16384, Body(fetch)["max_tokens"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------
    // 消息转换
    // ------------------------------------------------------------------

    [Fact]
    public async Task User_Text_And_Image_Blocks_Convert()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var user = new UserMessage
        {
            Timestamp = 2,
            Content = MessageContent.From(new ContentBlock[]
            {
                new TextContent { Text = "look" },
                new ImageContent { Data = "aGk=", MimeType = "image/png" },
                new TextContent { Text = "   " },
            }),
        };
        _ = await CollectAsync(AnthropicMessages.Stream(CreateModel(), Context(user), CreateOptions(fetch)));

        var content = Body(fetch)["messages"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("look", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("image", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("base64", content[1]!["source"]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", content[1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal("aGk=", content[1]!["source"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Assistant_Thinking_Signature_And_ToolUse_Replay()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var assistant = new AssistantMessage
        {
            Content = new ContentBlock[]
            {
                new ThinkingContent { Thinking = "pondering", ThinkingSignature = "sig-1" },
                new TextContent { Text = "answer" },
                new ToolCall { Id = "toolu_1", Name = "get_weather", Arguments = new JsonObject { ["city"] = "SF" } },
            },
            Api = "anthropic-messages",
            Provider = "anthropic",
            Model = "claude-test",
            Usage = new Usage(),
            StopReason = StopReason.ToolUse,
            Timestamp = 3,
        };
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi"), assistant), CreateOptions(fetch)));

        var messages = Body(fetch)["messages"]!.AsArray();
        // transformMessages 第二遍：孤立 tool call 由合成 toolResult 补尾。
        Assert.Equal(3, messages.Count);
        var assistantBody = messages[1]!;
        Assert.Equal("assistant", assistantBody["role"]!.GetValue<string>());
        var synthetic = messages[2]!["content"]!.AsArray()[0]!;
        Assert.Equal("No result provided", synthetic["content"]!.GetValue<string>());
        Assert.True(synthetic["is_error"]!.GetValue<bool>());
        var blocks = assistantBody["content"]!.AsArray();
        Assert.Equal(3, blocks.Count);
        Assert.Equal("thinking", blocks[0]!["type"]!.GetValue<string>());
        Assert.Equal("pondering", blocks[0]!["thinking"]!.GetValue<string>());
        Assert.Equal("sig-1", blocks[0]!["signature"]!.GetValue<string>());
        Assert.Equal("text", blocks[1]!["type"]!.GetValue<string>());
        Assert.Equal("tool_use", blocks[2]!["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", blocks[2]!["id"]!.GetValue<string>());
        Assert.Equal("SF", blocks[2]!["input"]!["city"]!.GetValue<string>());
    }

    [Fact]
    public async Task Thinking_Without_Signature_Becomes_Text_Unless_Compat_Allows_Empty()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var assistant = new AssistantMessage
        {
            Content = new ContentBlock[]
            {
                new ThinkingContent { Thinking = "loose thinking" },
                new TextContent { Text = "answer" },
            },
            Api = "anthropic-messages",
            Provider = "anthropic",
            Model = "claude-test",
            Usage = new Usage(),
            StopReason = StopReason.Stop,
            Timestamp = 3,
        };
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(assistant), CreateOptions(fetch)));
        var blocks = Body(fetch)["messages"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Equal("text", blocks[0]!["type"]!.GetValue<string>());
        Assert.Equal("loose thinking", blocks[0]!["text"]!.GetValue<string>());

        var fetch2 = new MockFetch();
        fetch2.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(compat: new JsonObject { ["allowEmptySignature"] = true }),
            Context(assistant),
            CreateOptions(fetch2)));
        var blocks2 = Body(fetch2)["messages"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Equal("thinking", blocks2[0]!["type"]!.GetValue<string>());
        Assert.Equal("", blocks2[0]!["signature"]!.GetValue<string>());
    }

    [Fact]
    public async Task Redacted_Thinking_Replays_As_Opaque_Data()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var assistant = new AssistantMessage
        {
            Content = new ContentBlock[]
            {
                new ThinkingContent { Thinking = "", ThinkingSignature = "encrypted-payload", Redacted = true },
                new TextContent { Text = "ok" },
            },
            Api = "anthropic-messages",
            Provider = "anthropic",
            Model = "claude-test",
            Usage = new Usage(),
            StopReason = StopReason.Stop,
            Timestamp = 3,
        };
        _ = await CollectAsync(AnthropicMessages.Stream(CreateModel(), Context(assistant), CreateOptions(fetch)));
        var blocks = Body(fetch)["messages"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Equal("redacted_thinking", blocks[0]!["type"]!.GetValue<string>());
        Assert.Equal("encrypted-payload", blocks[0]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tool_Results_Group_Into_User_Message_With_IsError()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var result = new ToolResultMessage
        {
            ToolCallId = "toolu_1",
            ToolName = "get_weather",
            Content = new ContentBlock[] { new TextContent { Text = "sunny" } },
            IsError = false,
            Timestamp = 4,
        };
        var result2 = new ToolResultMessage
        {
            ToolCallId = "toolu_2",
            ToolName = "get_time",
            Content = new ContentBlock[] { new TextContent { Text = "boom" } },
            IsError = true,
            Timestamp = 5,
        };
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(result, result2), CreateOptions(fetch)));

        var messages = Body(fetch)["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        var first = messages[0]!["content"]!.AsArray();
        Assert.Equal("tool_result", first[0]!["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", first[0]!["tool_use_id"]!.GetValue<string>());
        Assert.Equal("sunny", first[0]!["content"]!.GetValue<string>());
        Assert.False(first[0]!["is_error"]!.GetValue<bool>());
        Assert.True(first[1]!["is_error"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ToolCall_Id_Is_Normalized_For_Cross_Model_Replay()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        const string weirdId = "call_01|item_02#weird~id";
        var assistant = new AssistantMessage
        {
            Content = new ContentBlock[] { new ToolCall { Id = weirdId, Name = "get_weather" } },
            Api = "openai-completions",
            Provider = "openai",
            Model = "gpt-x",
            Usage = new Usage(),
            StopReason = StopReason.ToolUse,
            Timestamp = 3,
        };
        var result = new ToolResultMessage
        {
            ToolCallId = weirdId,
            ToolName = "get_weather",
            Content = new ContentBlock[] { new TextContent { Text = "done" } },
            IsError = false,
            Timestamp = 4,
        };
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(assistant, result), CreateOptions(fetch)));

        var messages = Body(fetch)["messages"]!.AsArray();
        var toolUseId = messages[0]!["content"]!.AsArray()[0]!["id"]!.GetValue<string>();
        var toolResultId = messages[1]!["content"]!.AsArray()[0]!["tool_use_id"]!.GetValue<string>();
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", toolUseId);
        Assert.DoesNotContain("|", toolUseId);
        Assert.Equal(toolUseId, toolResultId);
    }

    [Fact]
    public async Task Error_Tool_Result_Images_Are_Split_After_Results()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var result = new ToolResultMessage
        {
            ToolCallId = "toolu_1",
            ToolName = "render",
            Content = new ContentBlock[]
            {
                new ImageContent { Data = "aGk=", MimeType = "image/png" },
            },
            IsError = true,
            Timestamp = 4,
        };
        _ = await CollectAsync(AnthropicMessages.Stream(CreateModel(), Context(result), CreateOptions(fetch)));

        var content = Body(fetch)["messages"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Equal("tool_result", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("(image attached after the tool results)", content[0]!["content"]!.GetValue<string>());
        Assert.Equal("text", content[1]!["type"]!.GetValue<string>());
        Assert.Contains("toolu_1", content[1]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("image", content[2]!["type"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    // 工具定义
    // ------------------------------------------------------------------

    [Fact]
    public async Task Tools_Default_Non_Strict_With_Cache_Control_On_Last()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(SystemMsg("s", toolsAdded: new[] { Tool(), Tool("other") })),
            CreateOptions(fetch)));

        var tools = Body(fetch)["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        Assert.Equal("get_weather", tools[0]!["name"]!.GetValue<string>());
        Assert.Null(tools[0]!["strict"]);
        Assert.True(tools[0]!["eager_input_streaming"]!.GetValue<bool>());
        Assert.Equal("object", tools[0]!["input_schema"]!["type"]!.GetValue<string>());
        Assert.Equal("city", tools[0]!["input_schema"]!["required"]!.AsArray()[0]!.GetValue<string>());
        Assert.NotNull(tools[1]!["cache_control"]);
        Assert.Null(tools[0]!["cache_control"]);
    }

    [Fact]
    public async Task Strict_Tools_Only_When_Compat_Supports_And_Schema_Compiles()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var strictTool = Tool("strict_tool") with
        {
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["q"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("q"),
            },
            ConstrainedSampling = new ConstrainedSampling.Config(
                new ConstrainedSamplingConfig.JsonSchema(StrictMode.Require)),
        };
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(compat: new JsonObject { ["supportsStrictTools"] = true }),
            Context(SystemMsg("s", toolsAdded: new[] { strictTool })),
            CreateOptions(fetch)));

        var tool = Body(fetch)["tools"]!.AsArray()[0]!;
        Assert.True(tool["strict"]!.GetValue<bool>());
        Assert.Equal("object", tool["input_schema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Strict_Tool_With_Anthropic_Unsupported_Keyword_Falls_Back_To_Non_Strict()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var preferTool = Tool("prefer_tool") with
        {
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    // minimum 是 Anthropic strict 不收的关键字：prefer 应回落非 strict。
                    ["n"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 },
                },
                ["required"] = new JsonArray("n"),
            },
            ConstrainedSampling = new ConstrainedSampling.Config(
                new ConstrainedSamplingConfig.JsonSchema(StrictMode.Prefer)),
        };
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(compat: new JsonObject { ["supportsStrictTools"] = true }),
            Context(SystemMsg("s", toolsAdded: new[] { preferTool })),
            CreateOptions(fetch)));

        var tool = Body(fetch)["tools"]!.AsArray()[0]!;
        Assert.Null(tool["strict"]);
    }

    [Fact]
    public async Task Strict_Required_Tool_Without_Compat_Support_Fails_As_Terminal_Error()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(SystemMsg("s", toolsAdded: new[] { Tool("strict_tool", constrained: true) })),
            CreateOptions(fetch)));
        Assert.Equal(new[] { "error" }, events.Select(@event => @event.Type));
        Assert.Empty(fetch.Requests);
    }

    // ------------------------------------------------------------------
    // cache_control（D6）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cache_Control_Lands_On_System_Last_Tool_And_Last_Message()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(SystemMsg("s", toolsAdded: new[] { Tool() }), UserMsg("hi")),
            CreateOptions(fetch)));

        var body = Body(fetch);
        Assert.Equal("ephemeral", body["system"]!.AsArray()[0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("ephemeral", body["tools"]!.AsArray()[^1]!["cache_control"]!["type"]!.GetValue<string>());
        var lastMessage = body["messages"]!.AsArray()[^1]!;
        Assert.Equal("ephemeral", lastMessage["content"]!.AsArray()[^1]!["cache_control"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cache_Retention_None_Drops_Cache_Control()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(SystemMsg("s", toolsAdded: new[] { Tool() }), UserMsg("hi")),
            CreateOptions(fetch) with { CacheRetention = CacheRetention.None }));

        var body = Body(fetch);
        Assert.Null(body["system"]!.AsArray()[0]!["cache_control"]);
        Assert.Null(body["tools"]!.AsArray()[^1]!["cache_control"]);
    }

    [Fact]
    public async Task Long_Cache_Retention_Adds_Ttl_1h()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with { CacheRetention = CacheRetention.Long }));
        Assert.Equal("1h", Body(fetch)["messages"]!.AsArray()[^1]!["content"]!.AsArray()[^1]!
            ["cache_control"]!["ttl"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    // 请求生命周期：Fetch 缺失 / HTTP 错误 / retry / 超时 / hooks
    // ------------------------------------------------------------------

    [Fact]
    public async Task Missing_Fetch_Terminal_Error_Without_Throwing()
    {
        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), new AnthropicOptions { ApiKey = "k" }));
        Assert.Equal(new[] { "error" }, events.Select(@event => @event.Type));
        Assert.Equal(StopReason.Error, Assert.IsType<AssistantMessageEvent.Error>(events[^1]).Reason);
    }

    [Fact]
    public async Task Missing_Api_Key_Terminal_Error_Without_Request()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), new AnthropicOptions { Fetch = fetch.Fetch }));
        Assert.Equal(new[] { "error" }, events.Select(@event => @event.Type));
        Assert.Empty(fetch.Requests);
    }

    [Fact]
    public async Task Http_Error_Is_Terminal_Error_Without_Throwing()
    {
        var fetch = new MockFetch();
        fetch.EnqueueException(new FetchException(FetchErrorKind.Protocol, "429 slow down")
        {
            StatusCode = 429,
        });
        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch)));

        Assert.Single(fetch.Requests);
        Assert.Equal(new[] { "error" }, events.Select(@event => @event.Type));
        Assert.Contains("429", Assert.IsType<AssistantMessageEvent.Error>(events[^1]).Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retryable_Http_Error_Is_Retried_Only_Before_Request_Establishment()
    {
        var fetch = new MockFetch();
        fetch.EnqueueException(new FetchException(FetchErrorKind.Protocol, "429 slow down")
        {
            StatusCode = 429,
        });
        fetch.EnqueueText(AnthropicSse(MessageStart(), MessageDelta("end_turn"), MessageStop()), headers: SseHeaders());
        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with { MaxRetries = 1, MaxRetryDelayMs = 1 }));

        // 429 在流建立前命中 ProviderRetry → 第二次尝试成功，SSE 被真正消费到 done。
        Assert.Equal(2, fetch.Requests.Count);
        Assert.Equal(new[] { "start", "done" }, events.Select(@event => @event.Type));
        Assert.Equal(StopReason.Stop, Assert.IsType<AssistantMessageEvent.Done>(events[^1]).Reason);
    }

    [Fact]
    public async Task Timeout_Ms_Maps_To_All_FetchOptions()
    {
        var fetch = new MockFetch();
        FetchOptions? captured = null;
        fetch.Enqueue((_, options, _) =>
        {
            captured = options;
            var headers = new FetchHeaders();
            headers["content-type"] = "text/event-stream";
            return Task.FromResult<IFetchResponse>(
                new MockFetchResponse(200, System.Text.Encoding.UTF8.GetBytes(Sse()), headers));
        });
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with { TimeoutMs = 1234 }));

        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), captured!.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), captured.FirstByteTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), captured.IdleTimeout);
    }

    [Fact]
    public async Task OnPayload_Transforms_And_Stream_Field_Is_Forced()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with
            {
                OnPayload = (payload, _) => Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["model"] = "rewired",
                    ["max_tokens"] = 8,
                }),
            }));

        var body = Body(fetch);
        Assert.Equal("rewired", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OnResponse_Fires_After_Response_Before_Stream_Events()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(MessageStart(), MessageDelta("end_turn"), MessageStop()), headers: SseHeaders());
        var onResponseCount = 0;
        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch, new AnthropicOptions
            {
                OnResponse = (_, _) =>
                {
                    Interlocked.Increment(ref onResponseCount);
                    return Task.CompletedTask;
                },
            })));

        // OnResponse 恰 1 次、在流事件消费之前（start 之后才推事件）。
        Assert.Equal(1, Volatile.Read(ref onResponseCount));
        Assert.Equal(new[] { "start", "done" }, events.Select(@event => @event.Type));
        Assert.Equal(StopReason.Stop, Assert.IsType<AssistantMessageEvent.Done>(events[^1]).Reason);
    }

    // ------------------------------------------------------------------
    // StreamSimple
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamSimple_Without_Reasoning_Disables_Thinking()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.StreamSimple(CreateModel(), Context(UserMsg("hi")), new SimpleStreamOptions
        {
            ApiKey = "k",
            Fetch = fetch.Fetch,
        }));
        Assert.Equal("disabled", Body(fetch)["thinking"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task StreamSimple_With_Reasoning_Enables_Budget_Thinking_And_Clamps_MaxTokens()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.StreamSimple(CreateModel(), Context(UserMsg("hi")), new SimpleStreamOptions
        {
            ApiKey = "k",
            Fetch = fetch.Fetch,
            MaxTokens = 4096,
            Reasoning = ThinkingLevel.Medium,
        }));

        var body = Body(fetch);
        Assert.Equal("enabled", body["thinking"]!["type"]!.GetValue<string>());
        // adjustMaxTokensForThinking：maxTokens = min(4096+8192, 16384) = 12288；
        // 预算 = min(8192, max(0, 12288-1024)) = 8192。
        Assert.Equal(8192, body["thinking"]!["budget_tokens"]!.GetValue<int>());
        Assert.Equal(12288, body["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task StreamSimple_With_Reasoning_On_Adaptive_Model_Uses_Effort()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        _ = await CollectAsync(AnthropicMessages.StreamSimple(
            CreateModel(compat: new JsonObject { ["forceAdaptiveThinking"] = true }),
            Context(UserMsg("hi")),
            new SimpleStreamOptions { ApiKey = "k", Fetch = fetch.Fetch, Reasoning = ThinkingLevel.Low }));

        var body = Body(fetch);
        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("low", body["output_config"]!["effort"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    // providerThinkingLevel（pi anthropic-messages.ts:1326；Task 3 记入 output 重放）
    // ------------------------------------------------------------------

    [Fact]
    public void BuildParams_Records_Provider_Thinking_Level_Only_In_Effort_Message_Mode()
    {
        var model = CreateModel();
        var context = Context(UserMsg("hi"));

        // effort-message 模式生效（supportsMidConvoEffort 且未禁 thinking）：activeEffort 记入，
        // 且末尾确实挂着同一 effort 的 system 标记（证明模式在效）。
        var active = AnthropicMessages.BuildParamsForRequest(
            model, context, isOAuthToken: false,
            new AnthropicOptions { ThinkingEnabled = true, Effort = ThinkingLevel.XHigh },
            new AnthropicMessagesCompat { SupportsMidConvoEffort = true });
        Assert.Equal("xhigh", active.ProviderThinkingLevel);
        var effortMarker = active.Params["messages"]!.AsArray()[^1]!;
        Assert.Equal("system", effortMarker["role"]!.GetValue<string>());
        Assert.Equal("xhigh", effortMarker["output_config"]!["effort"]!.GetValue<string>());

        // 模式生效但未指定 effort：activeEffort 回落 high。
        var defaulted = AnthropicMessages.BuildParamsForRequest(
            model, context, isOAuthToken: false, null,
            new AnthropicMessagesCompat { SupportsMidConvoEffort = true });
        Assert.Equal("high", defaulted.ProviderThinkingLevel);

        // 模式未生效（默认 compat 无 supportsMidConvoEffort）：null。
        var inactive = AnthropicMessages.BuildParamsForRequest(
            model, context, isOAuthToken: false, null, new AnthropicMessagesCompat());
        Assert.Null(inactive.ProviderThinkingLevel);

        // 模式因 thinking 禁用而失效（effortMessages = supportsMidConvoEffort && !thinkingDisabled）：null。
        var disabled = AnthropicMessages.BuildParamsForRequest(
            model, context, isOAuthToken: false,
            new AnthropicOptions { ThinkingEnabled = false },
            new AnthropicMessagesCompat { SupportsMidConvoEffort = true });
        Assert.Null(disabled.ProviderThinkingLevel);
    }

    // ------------------------------------------------------------------
    // SSE 状态机（Task 3）：命名事件 → 统一流事件 / usage / 终态
    // ------------------------------------------------------------------

    [Fact]
    public async Task Text_Stream()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            MessageStart(),
            ContentBlockStart(0, new JsonObject { ["type"] = "text", ["text"] = "" }),
            TextDelta(0, "Hello"),
            TextDelta(0, " world"),
            ContentBlockStop(0),
            MessageDelta("end_turn", new JsonObject { ["output_tokens"] = 4 }),
            MessageStop()), headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "text_delta", "text_end", "done" },
            EventTypes(events));

        var textStart = Assert.IsType<AssistantMessageEvent.TextStart>(events[1]);
        Assert.Equal(0, textStart.ContentIndex);
        Assert.Equal("Hello", Assert.IsType<AssistantMessageEvent.TextDelta>(events[2]).Delta);
        var textEnd = Assert.IsType<AssistantMessageEvent.TextEnd>(events[4]);
        Assert.Equal(0, textEnd.ContentIndex);
        Assert.Equal("Hello world", textEnd.Content);

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.Stop, done.Reason);
        Assert.Equal("Hello world", Assert.IsType<TextContent>(done.Message.Content[0]).Text);
        Assert.Equal("msg_1", done.Message.ResponseId);
        // message_delta 的 output_tokens 覆盖 message_start 初值；total 按分量合计（Anthropic 不报 total）。
        Assert.Equal(4, done.Message.Usage.Output);
        Assert.Equal(8, done.Message.Usage.TotalTokens);
    }

    [Fact]
    public async Task Thinking_And_Signature()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            MessageStart(),
            ContentBlockStart(0, new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" }),
            ThinkingDelta(0, "ponder"),
            SignatureDelta(0, "sig-9"),
            ContentBlockStop(0),
            MessageDelta("end_turn"),
            MessageStop()), headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        // signature_delta 只回填块上的签名，不推流事件（pi 同）。
        Assert.Equal(
            new[] { "start", "thinking_start", "thinking_delta", "thinking_end", "done" },
            EventTypes(events));

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        var thinking = Assert.IsType<ThinkingContent>(done.Message.Content[0]);
        Assert.Equal("ponder", thinking.Thinking);
        Assert.Equal("sig-9", thinking.ThinkingSignature);
        Assert.Null(thinking.Redacted);
    }

    [Fact]
    public async Task Redacted_Thinking_Starts_Thinking_Block_With_Opaque_Data()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            MessageStart(),
            ContentBlockStart(0, new JsonObject { ["type"] = "redacted_thinking", ["data"] = "encrypted-payload" }),
            ContentBlockStop(0),
            MessageDelta("end_turn"),
            MessageStop()), headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        Assert.Equal(
            new[] { "start", "thinking_start", "thinking_end", "done" },
            EventTypes(events));
        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        var thinking = Assert.IsType<ThinkingContent>(done.Message.Content[0]);
        Assert.True(thinking.Redacted);
        Assert.Equal("[Reasoning redacted]", thinking.Thinking);
        Assert.Equal("encrypted-payload", thinking.ThinkingSignature);
    }

    [Fact]
    public async Task Tool_Use_Input_Json_Delta()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            MessageStart(),
            ContentBlockStart(0, new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = "toolu_1",
                ["name"] = "get_weather",
                ["input"] = new JsonObject(),
            }),
            JsonDelta(0, "{\"city\""),
            JsonDelta(0, ":\"SF\"}"),
            ContentBlockStop(0),
            MessageDelta("tool_use"),
            MessageStop()), headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        Assert.Equal(
            new[] { "start", "toolcall_start", "toolcall_delta", "toolcall_delta", "toolcall_end", "done" },
            EventTypes(events));

        var end = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(events[^2]);
        Assert.Equal(0, end.ContentIndex);
        Assert.Equal("toolu_1", end.ToolCall.Id);
        Assert.Equal("get_weather", end.ToolCall.Name);
        Assert.Null(end.ToolCall.ArgumentsError);
        Assert.Equal("SF", end.ToolCall.Arguments["city"]?.GetValue<string>());

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.ToolUse, done.Reason);
        Assert.Equal("tool_use", done.Message.RawStopReason);
    }

    [Fact]
    public async Task Tool_Invalid_Argument_Json_Lands_ArgumentsError()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            MessageStart(),
            ContentBlockStart(0, new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = "toolu_1",
                ["name"] = "get_weather",
                ["input"] = new JsonObject(),
            }),
            JsonDelta(0, "{\"city\":"),
            ContentBlockStop(0),
            MessageDelta("tool_use"),
            MessageStop()), headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        // pi parseToolCallArguments：非法参数 JSON 不失败流，落 argumentsError（含已收文本）。
        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        var toolCall = Assert.IsType<ToolCall>(done.Message.Content[0]);
        Assert.NotNull(toolCall.ArgumentsError);
        Assert.Contains("Received arguments", toolCall.ArgumentsError, StringComparison.Ordinal);
        Assert.Equal(StopReason.ToolUse, done.Reason);
    }

    [Fact]
    public async Task Usage_Cache_Read_Write_And_One_Hour()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            MessageStart(usage: new JsonObject
            {
                ["input_tokens"] = 100,
                ["output_tokens"] = 2,
                ["cache_read_input_tokens"] = 20,
                ["cache_creation_input_tokens"] = 5,
                ["cache_creation"] = new JsonObject { ["ephemeral_1h_input_tokens"] = 2 },
            }),
            MessageDelta("end_turn", new JsonObject { ["output_tokens"] = 10 }),
            MessageStop()), headers: SseHeaders());

        var model = CreateModel() with { Cost = new ModelCost(3, 15, 0.3, 0.6) };
        var events = await CollectAsync(AnthropicMessages.Stream(model, Context(UserMsg("hi")), CreateOptions(fetch)));

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        var usage = done.Message.Usage;
        Assert.Equal(100, usage.Input);
        Assert.Equal(10, usage.Output);
        Assert.Equal(20, usage.CacheRead);
        Assert.Equal(5, usage.CacheWrite);
        Assert.Equal(2, usage.CacheWrite1h);
        Assert.Equal(135, usage.TotalTokens);

        // 1h 写入按 2×input 价、short 写入按 cacheWrite 价（pi calculateCost 同式）。
        Assert.Equal(3.0 / 1_000_000 * 100, usage.Cost.Input, 12);
        Assert.Equal(15.0 / 1_000_000 * 10, usage.Cost.Output, 12);
        Assert.Equal(0.3 / 1_000_000 * 20, usage.Cost.CacheRead, 12);
        Assert.Equal((0.6 * 3 + 3.0 * 2 * 2) / 1_000_000, usage.Cost.CacheWrite, 12);
    }

    [Fact]
    public async Task Fallback_Model_Response_Switches_Cost_Source()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            MessageStart(responseId: "msg_fb", responseModel: "claude-fallback"),
            MessageDelta("end_turn"),
            MessageStop()), headers: SseHeaders());

        var model = CreateModel(compat: new JsonObject
        {
            ["allowedFallbackModels"] = new JsonArray(new JsonObject
            {
                ["provider"] = "anthropic",
                ["model"] = "claude-fallback",
                ["cost"] = new JsonObject { ["input"] = 6.0, ["output"] = 30.0, ["cacheRead"] = 0.6, ["cacheWrite"] = 1.2 },
            }),
        }) with { Cost = new ModelCost(3, 15, 0.3, 0.6) };
        var events = await CollectAsync(AnthropicMessages.Stream(model, Context(UserMsg("hi")), CreateOptions(fetch)));

        // 服务端 fallback 模型接手：计价改用 allowedFallbackModels 里该模型的 cost（pi usageModel）。
        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal("claude-fallback", done.Message.ResponseModel);
        Assert.Equal(6.0 / 1_000_000 * done.Message.Usage.Input, done.Message.Usage.Cost.Input, 12);
    }

    [Fact]
    public async Task Stop_Reason_Map()
    {
        // max_tokens → Length。
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(MessageStart(), MessageDelta("max_tokens"), MessageStop()), headers: SseHeaders());
        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(fetch)));
        Assert.Equal(StopReason.Length, Assert.IsType<AssistantMessageEvent.Done>(events[^1]).Reason);

        // refusal → 终态 Error，错误消息取 stop_details.explanation。
        var refusalFetch = new MockFetch();
        refusalFetch.EnqueueText(AnthropicSse(
            MessageStart(),
            MessageDelta("refusal", stopDetails: new JsonObject { ["explanation"] = "cannot help with that" }),
            MessageStop()), headers: SseHeaders());
        var refusalEvents = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(refusalFetch)));
        var refusalError = Assert.IsType<AssistantMessageEvent.Error>(refusalEvents[^1]);
        Assert.Equal(StopReason.Error, refusalError.Reason);
        Assert.Equal("cannot help with that", refusalError.Message.ErrorMessage);

        // sensitive → 终态 Error。
        var sensitiveFetch = new MockFetch();
        sensitiveFetch.EnqueueText(
            AnthropicSse(MessageStart(), MessageDelta("sensitive"), MessageStop()), headers: SseHeaders());
        var sensitiveEvents = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(sensitiveFetch)));
        Assert.Equal(
            "Provider stopped with: sensitive",
            Assert.IsType<AssistantMessageEvent.Error>(sensitiveEvents[^1]).Message.ErrorMessage);
    }

    [Fact]
    public async Task Missing_Stop_Is_Error()
    {
        // message_stop 到了但没带 stop_reason：pending 判错，partial 文本保留。
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            MessageStart(),
            ContentBlockStart(0, new JsonObject { ["type"] = "text", ["text"] = "" }),
            TextDelta(0, "partial"),
            ContentBlockStop(0),
            MessageStop()), headers: SseHeaders());
        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(fetch)));
        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("ended without a stop reason", error.Message.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("partial", Assert.IsType<TextContent>(error.Message.Content[0]).Text);

        // message_start 后流被截断（无 message_stop）：截断判错，partial 同样保留。
        var truncated = new MockFetch();
        truncated.EnqueueText(AnthropicSse(
            MessageStart(),
            ContentBlockStart(0, new JsonObject { ["type"] = "text", ["text"] = "" }),
            TextDelta(0, "partial")), headers: SseHeaders());
        var truncatedEvents = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(truncated)));
        var truncatedError = Assert.IsType<AssistantMessageEvent.Error>(truncatedEvents[^1]);
        Assert.Contains("ended before message_stop", truncatedError.Message.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("partial", Assert.IsType<TextContent>(truncatedError.Message.Content[0]).Text);
    }

    [Fact]
    public async Task Sse_Error_Event_Is_Terminal_Error()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n",
            headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        // 命名 error 事件 = 服务端中途故障：start 已推，终态 Error。
        Assert.Equal(new[] { "start", "error" }, EventTypes(events));
        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("Overloaded", error.Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ping_And_Unknown_Named_Events_Are_Ignored()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            "event: ping\ndata: {\"type\":\"ping\"}\n\n",
            "event: something_new\ndata: {\"type\":\"something_new\"}\n\n",
            MessageStart(),
            "event: ping\ndata: {\"type\":\"ping\"}\n\n",
            MessageDelta("end_turn"),
            MessageStop()), headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        // ping / 未知命名事件照 pi 静默跳过，不影响事件序列。
        Assert.Equal(new[] { "start", "done" }, EventTypes(events));
        Assert.Equal(StopReason.Stop, Assert.IsType<AssistantMessageEvent.Done>(events[^1]).Reason);
    }

    [Fact]
    public async Task Cancel_Is_Aborted_Keeps_Partial()
    {
        var cts = new CancellationTokenSource();
        var fetch = new MockFetch();
        var head = AnthropicSse(
            MessageStart(),
            ContentBlockStart(0, new JsonObject { ["type"] = "text", ["text"] = "" }),
            TextDelta(0, "partial"));
        fetch.Enqueue((_, _, _) => Task.FromResult<IFetchResponse>(new TestFetchResponse(
            200,
            new FetchHeaders(new Dictionary<string, string?> { ["content-type"] = "text/event-stream" }),
            new CancelBlockingStream(head, cts))));

        var options = CreateOptions(fetch) with { CancellationToken = cts.Token };
        var stream = AnthropicMessages.Stream(CreateModel(), Context(UserMsg("hi")), options);

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
    public async Task Stream_Failure_After_Start_Is_Terminal_Without_Resend()
    {
        var fetch = new MockFetch();
        // 流建立后中途截断：绝不重发（哪怕带 prefer strict 工具、MaxRetries=3）。
        fetch.EnqueueText(AnthropicSse(MessageStart(), MessageDelta("end_turn")), headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(compat: new JsonObject { ["supportsStrictTools"] = true }),
            Context(SystemMsg("s", toolsAdded: new[] { StrictPreferTool() })),
            CreateOptions(fetch) with { MaxRetries = 3 }));

        Assert.Single(fetch.Requests);
        Assert.Equal(new[] { "start", "error" }, EventTypes(events));
    }

    [Fact]
    public async Task OAuth_Tool_Name_Is_Mapped_Back_To_Declared_Casing()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            MessageStart(),
            ContentBlockStart(0, new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = "toolu_1",
                ["name"] = "get_weather",
                ["input"] = new JsonObject(),
            }),
            ContentBlockStop(0),
            MessageDelta("tool_use"),
            MessageStop()), headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(SystemMsg("s", toolsAdded: new[] { Tool("Get_Weather") })),
            CreateOptions(fetch) with { ApiKey = "sk-ant-oat01-example" }));

        // Claude Code 隐身模式：响应阶段的 tool_use 名按声明工具表还原原名（pi fromClaudeCodeName）。
        var end = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(events[^2]);
        Assert.Equal("Get_Weather", end.ToolCall.Name);
    }

    // ------------------------------------------------------------------
    // strict 工具 400 降级重发（只在流建立前）
    // ------------------------------------------------------------------

    private static Tool StrictPreferTool(string name = "strict_tool")
        => Tool(name) with
        {
            ConstrainedSampling = new ConstrainedSampling.Config(
                new ConstrainedSamplingConfig.JsonSchema(StrictMode.Prefer)),
        };

    [Fact]
    public async Task Strict_Tool_400_Downgrade_Resends_Without_Strict()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\","
            + "\"message\":\"Too many strict tools (3), max is 2\"}}",
            status: 400,
            headers: new Dictionary<string, string?> { ["content-type"] = "application/json" });
        fetch.EnqueueText(AnthropicSse(MessageStart(), MessageDelta("end_turn"), MessageStop()), headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(compat: new JsonObject { ["supportsStrictTools"] = true }),
            Context(SystemMsg("s", toolsAdded: new[] { StrictPreferTool() })),
            CreateOptions(fetch)));

        // 第一次 strict 被拒 → 关掉 prefer 工具的 strict 原样重发一次。
        Assert.Equal(2, fetch.Requests.Count);
        Assert.True(Body(fetch, 0)["tools"]!.AsArray()[0]!["strict"]!.GetValue<bool>());
        Assert.Null(Body(fetch, 1)["tools"]!.AsArray()[0]!["strict"]);
        Assert.Equal(new[] { "start", "done" }, EventTypes(events));

        var diagnostic = Assert.Single(Assert.IsType<AssistantMessageEvent.Done>(events[^1]).Message.Diagnostics!);
        Assert.Equal("anthropic_strict_tools_fallback", diagnostic.Type);
        Assert.Equal("strict_tool_limits_exceeded", diagnostic.Details!["reason"]!.GetValue<string>());
        Assert.Contains("Too many strict tools", diagnostic.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strict_400_Without_Strict_Limit_Message_Is_Terminal_Error()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"bad schema\"}}",
            status: 400,
            headers: new Dictionary<string, string?> { ["content-type"] = "application/json" });

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(compat: new JsonObject { ["supportsStrictTools"] = true }),
            Context(SystemMsg("s", toolsAdded: new[] { StrictPreferTool() })),
            CreateOptions(fetch) with { MaxRetries = 3 }));

        // 非 strict 容量消息的 400 不降级：单请求终态 Error。
        Assert.Single(fetch.Requests);
        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("bad schema", error.Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_Thinking_Level_Is_Recorded_In_Output_Snapshot()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(MessageStart(), MessageDelta("end_turn"), MessageStop()), headers: SseHeaders());

        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(compat: new JsonObject { ["supportsMidConvoEffort"] = true }),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with { ThinkingEnabled = true, Effort = ThinkingLevel.XHigh }));

        // pi stream:654：effort-message 模式生效时把 buildParams 的 providerThinkingLevel 记到回复快照，
        // 下一回合 ConvertMessagesCore 靠它重放历史 effort 标记。
        Assert.Equal(
            "xhigh",
            Assert.IsType<AssistantMessageEvent.Done>(events[^1]).Message.ProviderThinkingLevel);
    }

    [Fact]
    public async Task Hooks_And_Identity_Headers()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(AnthropicSse(
            MessageStart(),
            ContentBlockStart(0, new JsonObject { ["type"] = "text", ["text"] = "" }),
            TextDelta(0, "ok"),
            ContentBlockStop(0),
            MessageDelta("end_turn"),
            MessageStop()), headers: SseHeaders());

        JsonObject? payloadSeen = null;
        var responseCount = 0;
        var providerEvents = new List<string>();
        var options = CreateOptions(fetch, new AnthropicOptions
        {
            MaxTokens = 64,
            OnPayload = (payload, _) =>
            {
                payloadSeen = payload?.DeepClone() as JsonObject;
                return Task.FromResult<JsonNode?>(null);
            },
            OnResponse = (_, _) =>
            {
                Interlocked.Increment(ref responseCount);
                return Task.CompletedTask;
            },
            OnProviderStreamEvent = (@event, _) =>
            {
                providerEvents.Add(@event["type"]!.GetValue<string>());
                return Task.CompletedTask;
            },
        }) with
        {
            Identity = new ClientIdentity
            {
                UserAgent = UserAgentMode.Custom("custom-agent/1.0"),
                Attribution = null,
            },
        };

        var events = await CollectAsync(AnthropicMessages.Stream(CreateModel(), Context(UserMsg("hi")), options));

        Assert.Equal("custom-agent/1.0", fetch.Requests[0].Headers["user-agent"]);
        Assert.NotNull(payloadSeen);
        Assert.True(payloadSeen!["stream"]!.GetValue<bool>());
        Assert.Equal(1, Volatile.Read(ref responseCount));
        // 每条原始命名 SSE 事件都透传 OnProviderStreamEvent（pi onProviderStreamEvent）。
        Assert.Equal(
            new[]
            {
                "message_start", "content_block_start", "content_block_delta",
                "content_block_stop", "message_delta", "message_stop",
            },
            providerEvents);
        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "text_end", "done" },
            EventTypes(events));
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
}
