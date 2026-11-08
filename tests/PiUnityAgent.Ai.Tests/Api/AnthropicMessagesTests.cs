using System;
using System.Collections.Generic;
using System.Linq;
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

        Assert.Equal(new[] { "error" }, events.Select(@event => @event.Type));
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
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with { MaxRetries = 1, MaxRetryDelayMs = 1 }));

        Assert.Equal(2, fetch.Requests.Count);
        Assert.Equal(new[] { "error" }, events.Select(@event => @event.Type));
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
    public async Task OnResponse_Fires_After_Response_Before_Terminal_Error()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(), headers: SseHeaders());
        var onResponseCount = 0;
        var events = await CollectAsync(AnthropicMessages.Stream(
            CreateModel(),
            Context(UserMsg("hi")),
            CreateOptions(fetch) with
            {
                OnResponse = (_, _) =>
                {
                    Interlocked.Increment(ref onResponseCount);
                    return Task.CompletedTask;
                },
            }));

        Assert.Equal(1, Volatile.Read(ref onResponseCount));
        // Task 2 边界：请求已建立（可被 MockFetch 观测），SSE 状态机归 Task 3，这里终态 Error。
        Assert.Equal(new[] { "error" }, events.Select(@event => @event.Type));
        Assert.Contains("Task 3", Assert.IsType<AssistantMessageEvent.Error>(events[^1]).Message.ErrorMessage, StringComparison.Ordinal);
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
}
