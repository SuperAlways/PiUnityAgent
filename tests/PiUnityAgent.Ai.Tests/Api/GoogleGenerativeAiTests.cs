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
/// Google Generative AI（Gemini）协议适配器验收（S5 Task 4 简报矩阵）：
/// URL/头（x-goog-api-key、identity）、body 顶层字段（contents/systemInstruction/generationConfig/tools/toolConfig）、
/// thinkingConfig（budget/level/禁用回退）、消息转换（model/user parts、functionCall 回放、
/// toolResult→functionResponse：output/error 键、连续合并单 user turn、Gemini 3 多模态 parts 内嵌 + 显式 id、
/// <3 独立图片 turn）、
/// 响应流（text/thought 切换、functionCall、usage、finishReason）、错误/取消终态、
/// 流前重试、hooks、StreamSimple 思考档位。全走 MockFetch，无网无进程环境。
/// </summary>
public class GoogleGenerativeAiTests
{
    private const string DefaultBaseUrl = "https://generativelanguage.test/v1beta";

    /// <summary>budget 型思考模型（pi 模型表：gemini-2.5-flash，无 thinkingLevelMap）。</summary>
    private static Model CreateBudgetModel(string id = "gemini-2.5-flash", string? baseUrl = null)
        => new()
        {
            Id = id,
            Name = id,
            Api = "google-generative-ai",
            Provider = "google",
            BaseUrl = baseUrl ?? DefaultBaseUrl,
            Reasoning = true,
            Input = new[] { ModelInputModality.Text, ModelInputModality.Image },
            MaxTokens = 65536,
            ContextWindow = 1048576,
            Cost = new ModelCost(3, 15, 0.3, 0.6),
        };

    /// <summary>level 型思考模型（pi 模型表：gemini-3-flash-preview，off/xhigh/max 显式映射 null）。</summary>
    private static Model CreateLevelModel(string id = "gemini-3-flash-preview")
        => CreateBudgetModel(id) with
        {
            ThinkingLevelMap = new Dictionary<ModelThinkingLevel, string?>
            {
                [ModelThinkingLevel.Off] = null,
                [ModelThinkingLevel.Minimal] = "minimal",
                [ModelThinkingLevel.Low] = "low",
                [ModelThinkingLevel.Medium] = "medium",
                [ModelThinkingLevel.High] = "high",
                [ModelThinkingLevel.XHigh] = null,
                [ModelThinkingLevel.Max] = null,
            },
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

    private static Tool Tool(string name = "get_weather", ConstrainedSampling? constrained = null)
        => new()
        {
            Name = name,
            Description = $"The {name} tool",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["city"] = new JsonObject { ["type"] = "string", ["const"] = "SF" },
                },
                ["required"] = new JsonArray("city"),
            },
            ConstrainedSampling = constrained,
        };

    private static GoogleOptions CreateOptions(MockFetch fetch, GoogleOptions? preset = null)
        => (preset ?? new GoogleOptions()) with { ApiKey = "test-key", Fetch = fetch.Fetch };

    private static IReadOnlyDictionary<string, string?> SseHeaders()
        => new Dictionary<string, string?> { ["content-type"] = "text/event-stream" };

    // ------------------------------------------------------------------
    // Gemini SSE chunk 构造（data-only 事件；真实线路无 event: 行）
    // ------------------------------------------------------------------

    private static string Sse(params JsonObject[] chunks)
        => string.Concat(chunks.Select(chunk => $"data: {chunk.ToJsonString()}\n\n"));

    private static JsonObject Chunk(
        JsonObject? content = null,
        string? finishReason = null,
        JsonObject? usage = null,
        string? responseId = null)
    {
        var chunk = new JsonObject();
        if (responseId is not null) chunk["responseId"] = responseId;
        if (content is not null || finishReason is not null)
        {
            var candidate = new JsonObject { ["role"] = "model" };
            if (content is not null) candidate["content"] = content;
            if (finishReason is not null) candidate["finishReason"] = finishReason;
            chunk["candidates"] = new JsonArray(candidate);
        }

        if (usage is not null) chunk["usageMetadata"] = usage;
        return chunk;
    }

    private static JsonObject Parts(params JsonObject[] parts)
        => new() { ["parts"] = new JsonArray(parts), ["role"] = "model" };

    private static JsonObject TextPart(string text, string? thoughtSignature = null)
    {
        var part = new JsonObject { ["text"] = text };
        if (thoughtSignature is not null) part["thoughtSignature"] = thoughtSignature;
        return part;
    }

    private static JsonObject ThoughtPart(string text, string? thoughtSignature = null)
    {
        var part = new JsonObject { ["text"] = text, ["thought"] = true };
        if (thoughtSignature is not null) part["thoughtSignature"] = thoughtSignature;
        return part;
    }

    private static JsonObject FunctionCallPart(
        string name, JsonObject? args = null, string? id = null, string? thoughtSignature = null)
    {
        var functionCall = new JsonObject { ["name"] = name };
        if (args is not null) functionCall["args"] = args;
        if (id is not null) functionCall["id"] = id;
        var part = new JsonObject { ["functionCall"] = functionCall };
        if (thoughtSignature is not null) part["thoughtSignature"] = thoughtSignature;
        return part;
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

    private static JsonObject Body(MockFetch fetch, int index = 0)
        => (JsonNode.Parse(fetch.Requests[index].Body.ToArray()) as JsonObject)!;

    // ------------------------------------------------------------------
    // 请求形状：URL / 头 / body 顶层字段
    // ------------------------------------------------------------------

    [Fact]
    public async Task Request_Url_Headers_And_Top_Level_Body()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var context = Context(SystemMsg("You are helpful.", toolsAdded: new[] { Tool() }), UserMsg("hi"));
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), context, CreateOptions(fetch, new GoogleOptions
            {
                MaxTokens = 256,
                Temperature = 0.5,
                ToolChoice = "auto",
            })));

        var request = fetch.Requests[0];
        Assert.Equal("POST", request.Method);
        Assert.EndsWith("/models/gemini-2.5-flash:streamGenerateContent?alt=sse", request.Url);
        // @google/genai SDK：apiKey 经 x-goog-api-key 头发送。
        Assert.Equal("test-key", request.Headers["x-goog-api-key"]);
        // identity 默认头（UA）在。
        Assert.True(request.Headers.ContainsKey("user-agent"));

        var body = Body(fetch);
        // GenerateContentRequest 顶层字段：contents/systemInstruction/tools/toolConfig；
        // thinkingConfig 在 generationConfig 内（TS 经 SDK 展开即此形状）。
        var contents = body["contents"]!.AsArray();
        Assert.Equal("user", contents[0]!["role"]!.GetValue<string>());
        Assert.Equal("hi", contents[0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("You are helpful.", body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.DoesNotContain(contents, content => content!["role"]!.GetValue<string>() == "system");
        Assert.NotNull(body["tools"]);
        Assert.Equal("AUTO", body["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>());
        var generationConfig = body["generationConfig"]!;
        Assert.Equal(256, generationConfig["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(0.5, generationConfig["temperature"]!.GetValue<double>());
        // SSE 由 query 参数声明，body 不带 stream 标记。
        Assert.Null(body["stream"]);
        // pi Google buildParams 不消费 samplingParams（只有 OpenAI 家族用）；无 thinking 选项时不写 thinkingConfig。
        Assert.Null(generationConfig["thinkingConfig"]);
    }

    [Fact]
    public async Task BaseUrl_Trailing_Slash_Is_Trimmed_And_Default_Applied()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(baseUrl: "https://generativelanguage.test/v1beta/"),
            Context(UserMsg("hi")),
            CreateOptions(fetch)));
        Assert.Equal(
            "https://generativelanguage.test/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse",
            fetch.Requests[0].Url);
    }

    [Fact]
    public async Task No_MaxTokens_Option_Sends_No_MaxOutputTokens()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), CreateOptions(fetch)));
        // pi buildParams：只在 options.maxTokens 显式给定时写 maxOutputTokens（无 model 兜底）。
        Assert.Null(Body(fetch)["generationConfig"]!["maxOutputTokens"]);
    }

    [Fact]
    public async Task User_Image_Block_Converts_To_InlineData()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var user = new UserMessage
        {
            Timestamp = 2,
            Content = MessageContent.From(new ContentBlock[]
            {
                new TextContent { Text = "look" },
                new ImageContent { Data = "aGk=", MimeType = "image/png" },
            }),
        };
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(user), CreateOptions(fetch)));

        var parts = Body(fetch)["contents"]!.AsArray()[0]!["parts"]!.AsArray();
        Assert.Equal(2, parts.Count);
        Assert.Equal("look", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("image/png", parts[1]!["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.Equal("aGk=", parts[1]!["inlineData"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Assistant_Thinking_Signature_And_FunctionCall_Replay()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var assistant = new AssistantMessage
        {
            Content = new ContentBlock[]
            {
                new ThinkingContent { Thinking = "pondering", ThinkingSignature = "c2ln" },
                new TextContent { Text = "answer" },
                new ToolCall
                {
                    Id = "call_1",
                    Name = "get_weather",
                    Arguments = new JsonObject { ["city"] = "SF" },
                    ThoughtSignature = "dG9vbA==",
                },
            },
            Api = "google-generative-ai",
            Provider = "google",
            Model = "gemini-3-flash-preview",
            Usage = new Usage(),
            StopReason = StopReason.ToolUse,
            Timestamp = 3,
        };
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateLevelModel(), Context(assistant, UserMsg("go")), CreateOptions(fetch)));

        var modelTurn = Body(fetch)["contents"]!.AsArray()[0]!;
        Assert.Equal("model", modelTurn["role"]!.GetValue<string>());
        var parts = modelTurn["parts"]!.AsArray();
        // thinking 同模型回放为 thought part + 签名。
        Assert.True(parts[0]!["thought"]!.GetValue<bool>());
        Assert.Equal("pondering", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("c2ln", parts[0]!["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("answer", parts[1]!["text"]!.GetValue<string>());
        // Gemini 3 要求 functionCall 显式 id。
        var functionCall = parts[2]!["functionCall"]!;
        Assert.Equal("get_weather", functionCall["name"]!.GetValue<string>());
        Assert.Equal("SF", functionCall["args"]!["city"]!.GetValue<string>());
        Assert.Equal("call_1", functionCall["id"]!.GetValue<string>());
        Assert.Equal("dG9vbA==", parts[2]!["thoughtSignature"]!.GetValue<string>());
    }

    [Fact]
    public async Task Budget_Model_Omits_FunctionCall_Id()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var assistant = new AssistantMessage
        {
            Content = new ContentBlock[]
            {
                new ToolCall { Id = "call_1", Name = "get_weather", Arguments = new JsonObject() },
            },
            Api = "google-generative-ai",
            Provider = "google",
            Model = "gemini-2.5-flash",
            Usage = new Usage(),
            StopReason = StopReason.ToolUse,
            Timestamp = 3,
        };
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(assistant), CreateOptions(fetch)));

        var functionCall = Body(fetch)["contents"]!.AsArray()[0]!["parts"]![0]!["functionCall"]!;
        // requiresToolCallId：gemini 2.x 不要求显式 id（pi 同）。
        Assert.Null(functionCall["id"]);
    }

    // ------------------------------------------------------------------
    // thinkingConfig：budget / level / 禁用回退
    // ------------------------------------------------------------------

    [Fact]
    public async Task Thinking_Wires_Budget_Level_And_Disabled()
    {
        // budget 型：thinkingBudget 透传（-1 动态、0 禁用语义照 pi）。
        var budgetFetch = new MockFetch();
        budgetFetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")),
            CreateOptions(budgetFetch, new GoogleOptions
            {
                Thinking = new GoogleThinkingOptions { Enabled = true, BudgetTokens = 2048 },
            })));
        var budgetConfig = Body(budgetFetch)["generationConfig"]!["thinkingConfig"]!;
        Assert.True(budgetConfig["includeThoughts"]!.GetValue<bool>());
        Assert.Equal(2048, budgetConfig["thinkingBudget"]!.GetValue<int>());
        Assert.Null(budgetConfig["thinkingLevel"]);

        // level 型：pi ThinkingLevel → Google 离散档位（大写 wire 值）。
        var levelFetch = new MockFetch();
        levelFetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateLevelModel(), Context(UserMsg("hi")),
            CreateOptions(levelFetch, new GoogleOptions
            {
                Thinking = new GoogleThinkingOptions { Enabled = true, Level = ThinkingLevel.High },
            })));
        var levelConfig = Body(levelFetch)["generationConfig"]!["thinkingConfig"]!;
        Assert.True(levelConfig["includeThoughts"]!.GetValue<bool>());
        Assert.Equal("HIGH", levelConfig["thinkingLevel"]!.GetValue<string>());
        Assert.Null(levelConfig["thinkingBudget"]);

        // budget 优先级低于 level（pi：level !== undefined 优先）。
        var bothFetch = new MockFetch();
        bothFetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateLevelModel(), Context(UserMsg("hi")),
            CreateOptions(bothFetch, new GoogleOptions
            {
                Thinking = new GoogleThinkingOptions
                {
                    Enabled = true,
                    Level = ThinkingLevel.Low,
                    BudgetTokens = 99,
                },
            })));
        Assert.Equal("LOW", Body(bothFetch)["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>());
    }

    [Fact]
    public async Task Thinking_Disabled_Sends_Zero_Budget_Or_Fallback_Level()
    {
        // budget 型：禁用 = thinkingBudget 0。
        var budgetFetch = new MockFetch();
        budgetFetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")),
            CreateOptions(budgetFetch, new GoogleOptions
            {
                Thinking = new GoogleThinkingOptions { Enabled = false },
            })));
        Assert.Equal(0, Body(budgetFetch)["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());

        // level 型且 off 被显式映射 null：回退到 clamp 后最近的可用档位（minimal）。
        var levelFetch = new MockFetch();
        levelFetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateLevelModel(), Context(UserMsg("hi")),
            CreateOptions(levelFetch, new GoogleOptions
            {
                Thinking = new GoogleThinkingOptions { Enabled = false },
            })));
        Assert.Equal(
            "MINIMAL",
            Body(levelFetch)["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>());

        // 非推理模型不写 thinkingConfig。
        var nonReasoningFetch = new MockFetch();
        nonReasoningFetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel() with { Reasoning = false }, Context(UserMsg("hi")),
            CreateOptions(nonReasoningFetch, new GoogleOptions
            {
                Thinking = new GoogleThinkingOptions { Enabled = true, BudgetTokens = 512 },
            })));
        Assert.Null(Body(nonReasoningFetch)["generationConfig"]!["thinkingConfig"]);
    }

    // ------------------------------------------------------------------
    // 工具声明 / toolConfig 模式
    // ------------------------------------------------------------------

    [Fact]
    public async Task Tool_Declaration_Rewrites_Const_As_Enum()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(SystemMsg("s", toolsAdded: new[] { Tool() }), UserMsg("hi")),
            CreateOptions(fetch) with { ToolChoice = "auto" }));

        var declaration = Body(fetch)["tools"]!.AsArray()[0]!["functionDeclarations"]!.AsArray()[0]!;
        Assert.Equal("get_weather", declaration["name"]!.GetValue<string>());
        var property = declaration["parametersJsonSchema"]!["properties"]!["city"]!;
        // Gemini 在 parametersJsonSchema 里忽略 const：改写成单值 enum（pi rewriteConstAsEnum）。
        Assert.Null(property["const"]);
        Assert.Equal("SF", property["enum"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolChoice_Maps_To_FunctionCallingConfig_Mode()
    {
        async Task<JsonObject> ModeFor(string? toolChoice, Model? model = null)
        {
            var fetch = new MockFetch();
            fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
            _ = await CollectAsync(GoogleGenerativeAi.Stream(
                model ?? CreateBudgetModel(),
                Context(SystemMsg("s", toolsAdded: new[] { Tool() }), UserMsg("hi")),
                CreateOptions(fetch) with { ToolChoice = toolChoice }));
            return (Body(fetch)["toolConfig"]!["functionCallingConfig"] as JsonObject)!;
        }

        Assert.Equal("AUTO", (await ModeFor("auto"))["mode"]!.GetValue<string>());
        Assert.Equal("NONE", (await ModeFor("none"))["mode"]!.GetValue<string>());
        Assert.Equal("ANY", (await ModeFor("any"))["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Strict_Tools_Require_Validated_Mode_Only_On_Gemini3()
    {
        // Prefer：Gemini 3 可编译 → strict 采样生效；Gemini 2.x 不支持 → 回落 null（不抛）。
        var preferTool = Tool(constrained: new ConstrainedSampling.Config(
            new ConstrainedSamplingConfig.JsonSchema(StrictMode.Prefer)));
        var context = Context(SystemMsg("s", toolsAdded: new[] { preferTool }), UserMsg("hi"));

        // Gemini 3+：strict 采样 → VALIDATED。
        var v3Fetch = new MockFetch();
        v3Fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(CreateLevelModel(), context, CreateOptions(v3Fetch)));
        Assert.Equal(
            "VALIDATED",
            Body(v3Fetch)["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>());

        // Gemini 2.x：不支持 strict → 无 toolChoice 时直接不带 toolConfig。
        var v2Fetch = new MockFetch();
        v2Fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(CreateBudgetModel(), context, CreateOptions(v2Fetch)));
        Assert.Null(Body(v2Fetch)["toolConfig"]);
    }

    [Fact]
    public async Task ToolResult_Output_Uses_FunctionResponse()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var result = new ToolResultMessage
        {
            ToolCallId = "call_1",
            ToolName = "get_weather",
            Content = new ContentBlock[] { new TextContent { Text = "sunny" } },
            IsError = false,
            Timestamp = 4,
        };
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(result), CreateOptions(fetch)));

        // 成功结果走 functionResponse，文本落 response.output（SDK 约定键）。
        var contents = Body(fetch)["contents"]!.AsArray();
        Assert.Single(contents);
        Assert.Equal("user", contents[0]!["role"]!.GetValue<string>());
        var functionResponse = contents[0]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("get_weather", functionResponse["name"]!.GetValue<string>());
        Assert.Equal("sunny", functionResponse["response"]!["output"]!.GetValue<string>());
        Assert.Null(functionResponse["response"]!["error"]);
        // Gemini 2.x 不要求显式 id。
        Assert.Null(functionResponse["id"]);
    }

    [Fact]
    public async Task ToolResult_Error_Uses_FunctionResponse_Error()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var result = new ToolResultMessage
        {
            ToolCallId = "call_1",
            ToolName = "get_weather",
            Content = new ContentBlock[] { new TextContent { Text = "city not found" } },
            IsError = true,
            Timestamp = 4,
        };
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(result), CreateOptions(fetch)));

        // IsError=true → 文本落 response.error，不带 output 键。
        var functionResponse = Body(fetch)["contents"]!.AsArray()[0]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("get_weather", functionResponse["name"]!.GetValue<string>());
        Assert.Equal("city not found", functionResponse["response"]!["error"]!.GetValue<string>());
        Assert.Null(functionResponse["response"]!["output"]);
    }

    [Fact]
    public async Task Consecutive_ToolResults_Merge_Into_Single_User_Turn()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var result = new ToolResultMessage
        {
            ToolCallId = "call_1",
            ToolName = "get_weather",
            Content = new ContentBlock[] { new TextContent { Text = "sunny" } },
            IsError = false,
            Timestamp = 4,
        };
        var result2 = new ToolResultMessage
        {
            ToolCallId = "call_2",
            ToolName = "get_time",
            Content = new ContentBlock[] { new TextContent { Text = "boom" } },
            IsError = true,
            Timestamp = 5,
        };
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(result, result2), CreateOptions(fetch)));

        // pi 合并规则：连续 toolResult 收进单个 user turn（Cloud Code Assist 约定）。
        var contents = Body(fetch)["contents"]!.AsArray();
        Assert.Single(contents);
        Assert.Equal("user", contents[0]!["role"]!.GetValue<string>());
        var parts = contents[0]!["parts"]!.AsArray();
        Assert.Equal(2, parts.Count);
        var first = parts[0]!["functionResponse"]!;
        Assert.Equal("get_weather", first["name"]!.GetValue<string>());
        Assert.Equal("sunny", first["response"]!["output"]!.GetValue<string>());
        var second = parts[1]!["functionResponse"]!;
        Assert.Equal("get_time", second["name"]!.GetValue<string>());
        Assert.Equal("boom", second["response"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Gemini3_ToolResult_Image_Uses_Multimodal_Parts()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var result = new ToolResultMessage
        {
            ToolCallId = "call_img",
            ToolName = "render",
            Content = new ContentBlock[]
            {
                new TextContent { Text = "rendered" },
                new ImageContent { Data = "aGk=", MimeType = "image/png" },
            },
            IsError = false,
            Timestamp = 4,
        };
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateLevelModel(), Context(result), CreateOptions(fetch)));

        // Gemini 3+ 支持多模态 functionResponse：inlineData parts 内嵌进 functionResponse，
        // 且 requiresToolCallId → 显式 id 回传。
        var contents = Body(fetch)["contents"]!.AsArray();
        Assert.Single(contents);
        var functionResponse = contents[0]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("render", functionResponse["name"]!.GetValue<string>());
        Assert.Equal("rendered", functionResponse["response"]!["output"]!.GetValue<string>());
        Assert.Equal("call_img", functionResponse["id"]!.GetValue<string>());
        var imageParts = functionResponse["parts"]!.AsArray();
        Assert.Single(imageParts);
        Assert.Equal("image/png", imageParts[0]!["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.Equal("aGk=", imageParts[0]!["inlineData"]!["data"]!.GetValue<string>());

        // 对照 Gemini 2.x：不支持多模态 functionResponse → 图片拆独立 user turn（pi 同）。
        var v2Fetch = new MockFetch();
        v2Fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(result), CreateOptions(v2Fetch)));
        var v2Contents = Body(v2Fetch)["contents"]!.AsArray();
        Assert.Equal(2, v2Contents.Count);
        var v2Response = v2Contents[0]!["parts"]![0]!["functionResponse"]!;
        // 文本仍在 response.output（"(see attached image)" 只在纯图片结果时出现）；图片不进 functionResponse.parts。
        Assert.Equal("rendered", v2Response["response"]!["output"]!.GetValue<string>());
        Assert.Null(v2Response["parts"]);
        Assert.Null(v2Response["id"]);
        var imageTurnParts = v2Contents[1]!["parts"]!.AsArray();
        Assert.Equal("Tool result image:", imageTurnParts[0]!["text"]!.GetValue<string>());
        Assert.Equal("image/png", imageTurnParts[1]!["inlineData"]!["mimeType"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    // 响应流：text / thought 切换 / functionCall
    // ------------------------------------------------------------------

    [Fact]
    public async Task Text_Stream_Keeps_First_ResponseId()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(
            Chunk(Parts(TextPart("Hello")), responseId: "resp_1"),
            Chunk(Parts(TextPart(" world")), responseId: "resp_2"),
            Chunk(Parts(), finishReason: "STOP", usage: UsageMeta(3, 2))),
            headers: SseHeaders());

        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "text_delta", "text_end", "done" },
            EventTypes(events));
        Assert.Equal("Hello", Assert.IsType<AssistantMessageEvent.TextDelta>(events[2]).Delta);
        Assert.Equal(" world", Assert.IsType<AssistantMessageEvent.TextDelta>(events[3]).Delta);
        var textEnd = Assert.IsType<AssistantMessageEvent.TextEnd>(events[4]);
        Assert.Equal(0, textEnd.ContentIndex);
        Assert.Equal("Hello world", textEnd.Content);

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.Stop, done.Reason);
        // responseId 取首个非空（pi output.responseId ||= chunk.responseId）。
        Assert.Equal("resp_1", done.Message.ResponseId);
        Assert.Equal("gemini-2.5-flash", done.Message.Model);
        Assert.Equal("google-generative-ai", done.Message.Api);
    }

    [Fact]
    public async Task Thought_Text_Switch_Emits_Ends_And_Retains_Signatures()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(
            Chunk(Parts(ThoughtPart("ponder ", "c2lnMQ=="))),
            Chunk(Parts(ThoughtPart("more"))),
            Chunk(Parts(TextPart("answer", "dGV4dA=="))),
            Chunk(Parts(TextPart("!"))),
            Chunk(Parts(), finishReason: "STOP")),
            headers: SseHeaders());

        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        Assert.Equal(
            new[]
            {
                "start",
                "thinking_start", "thinking_delta", "thinking_delta", "thinking_end",
                "text_start", "text_delta", "text_delta", "text_end",
                "done",
            },
            EventTypes(events));
        Assert.Equal(
            "ponder more",
            Assert.IsType<AssistantMessageEvent.ThinkingEnd>(events[4]).Content);
        Assert.Equal("answer!", Assert.IsType<AssistantMessageEvent.TextEnd>(events[8]).Content);

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        // thoughtSignature 保留块内首个非空值（后续 delta 省略不覆盖，pi retainThoughtSignature）。
        Assert.Equal("c2lnMQ==", Assert.IsType<ThinkingContent>(done.Message.Content[0]).ThinkingSignature);
        Assert.Equal("dGV4dA==", Assert.IsType<TextContent>(done.Message.Content[1]).TextSignature);
    }

    [Fact]
    public async Task FunctionCall_Closes_Block_And_Upgrade_Stop_To_ToolUse()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(
            Chunk(Parts(TextPart("checking"))),
            Chunk(Parts(FunctionCallPart(
                "get_weather",
                new JsonObject { ["city"] = "SF" },
                id: "call_abc",
                thoughtSignature: "dG9vbA=="))),
            Chunk(Parts(), finishReason: "STOP")),
            headers: SseHeaders());

        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        Assert.Equal(
            new[]
            {
                "start",
                "text_start", "text_delta", "text_end",
                "toolcall_start", "toolcall_delta", "toolcall_end",
                "done",
            },
            EventTypes(events));

        var end = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(events[6]);
        Assert.Equal(1, end.ContentIndex);
        Assert.Equal("call_abc", end.ToolCall.Id);
        Assert.Equal("get_weather", end.ToolCall.Name);
        Assert.Equal("SF", end.ToolCall.Arguments["city"]!.GetValue<string>());
        Assert.Equal("dG9vbA==", end.ToolCall.ThoughtSignature);
        Assert.Equal("{\"city\":\"SF\"}", Assert.IsType<AssistantMessageEvent.ToolCallDelta>(events[5]).Delta);

        // finishReason STOP 但有 toolCall：升级为 toolUse（pi 同）。
        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.ToolUse, done.Reason);
        Assert.Equal("STOP", done.Message.RawStopReason);
    }

    [Fact]
    public async Task FunctionCall_Generates_Id_And_Regenerates_Duplicates()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(
            Chunk(Parts(
                FunctionCallPart("get_weather", new JsonObject { ["city"] = "SF" }),
                FunctionCallPart("get_weather", new JsonObject { ["city"] = "NYC" }, id: "call_dup"),
                FunctionCallPart("get_weather", id: "call_dup"))),
            Chunk(Parts(), finishReason: "STOP")),
            headers: SseHeaders());

        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        var first = Assert.IsType<ToolCall>(done.Message.Content[0]);
        var second = Assert.IsType<ToolCall>(done.Message.Content[1]);
        var third = Assert.IsType<ToolCall>(done.Message.Content[2]);

        // 缺 id → 生成 `<name>_<ts>_<counter>`；重复 id → 重新生成（pi 同）。
        Assert.StartsWith("get_weather_", first.Id, StringComparison.Ordinal);
        Assert.Equal("call_dup", second.Id);
        Assert.StartsWith("get_weather_", third.Id, StringComparison.Ordinal);
        Assert.NotEqual(second.Id, third.Id);
        Assert.NotEqual(first.Id, third.Id);
        Assert.Equal("SF", first.Arguments["city"]!.GetValue<string>());
        // 缺 args → 空 JsonObject。
        Assert.Empty(third.Arguments);
    }

    [Fact]
    public async Task Usage_Metadata_Maps_Tokens_And_Cost()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(
            Chunk(Parts(TextPart("answer")), finishReason: "STOP", usage: UsageMeta(10, 5, cached: 3, thoughts: 2, total: 17))),
            headers: SseHeaders());

        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        var usage = done.Message.Usage;
        Assert.Equal(7, usage.Input);          // promptTokenCount - cachedContentTokenCount
        Assert.Equal(7, usage.Output);         // candidatesTokenCount + thoughtsTokenCount
        Assert.Equal(3, usage.CacheRead);
        Assert.Equal(0, usage.CacheWrite);
        Assert.Equal(2, usage.Reasoning);      // thoughtsTokenCount 子集
        Assert.Equal(17, usage.TotalTokens);   // 直接用 totalTokenCount（非全桶求和）

        // 成本按目录价计算（pi calculateCost）。
        Assert.Equal(3.0 / 1_000_000 * 7, usage.Cost.Input, 12);
        Assert.Equal(15.0 / 1_000_000 * 7, usage.Cost.Output, 12);
        Assert.Equal(0.3 / 1_000_000 * 3, usage.Cost.CacheRead, 12);
        Assert.Equal(
            3.0 / 1_000_000 * 7 + 15.0 / 1_000_000 * 7 + 0.3 / 1_000_000 * 3,
            usage.Cost.Total, 12);
    }

    private static JsonObject UsageMeta(int prompt, int candidates, int cached = 0, int thoughts = 0, int? total = null)
        => new()
        {
            ["promptTokenCount"] = prompt,
            ["candidatesTokenCount"] = candidates,
            ["cachedContentTokenCount"] = cached,
            ["thoughtsTokenCount"] = thoughts,
            ["totalTokenCount"] = total ?? prompt + candidates,
        };

    // ------------------------------------------------------------------
    // finishReason 映射与终态
    // ------------------------------------------------------------------

    [Fact]
    public async Task FinishReason_Maps_Stop_Length_And_Error()
    {
        async Task<AssistantMessageEvent> LastEventFor(string finishReason)
        {
            var fetch = new MockFetch();
            fetch.EnqueueText(Sse(
                Chunk(Parts(TextPart("x")), finishReason: finishReason)),
                headers: SseHeaders());
            var events = await CollectAsync(GoogleGenerativeAi.Stream(
                CreateBudgetModel(), Context(UserMsg("hi")), CreateOptions(fetch)));
            return events[^1];
        }

        Assert.Equal(
            StopReason.Stop,
            Assert.IsType<AssistantMessageEvent.Done>(await LastEventFor("STOP")).Reason);
        Assert.Equal(
            StopReason.Length,
            Assert.IsType<AssistantMessageEvent.Done>(await LastEventFor("MAX_TOKENS")).Reason);

        // 安全类 finishReason → 终态 Error（partial 保留）。
        var safety = Assert.IsType<AssistantMessageEvent.Error>(await LastEventFor("SAFETY"));
        Assert.Equal(StopReason.Error, safety.Reason);
        Assert.Equal("Provider stopped with: SAFETY", safety.Message.ErrorMessage);
        Assert.Equal("SAFETY", safety.Message.RawStopReason);
        Assert.Equal("x", Assert.IsType<TextContent>(safety.Message.Content[0]).Text);

        // 未知 finishReason → Unhandled（pi mapStopReason default 抛错）。
        var unknown = Assert.IsType<AssistantMessageEvent.Error>(await LastEventFor("SOMETHING_NEW"));
        Assert.Contains("Unhandled stop reason", unknown.Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_Finish_Reason_Is_Error_With_Partial_Kept()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(Parts(TextPart("partial")))), headers: SseHeaders());

        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("ended without a finish reason", error.Message.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("partial", Assert.IsType<TextContent>(error.Message.Content[0]).Text);
    }

    // ------------------------------------------------------------------
    // 错误 / 重试 / 取消 / 缺配置
    // ------------------------------------------------------------------

    [Fact]
    public async Task Http_Error_Is_Terminal_Error()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(
            "{\"error\":{\"code\":400,\"message\":\"API key not valid\"}}",
            status: 400,
            headers: new Dictionary<string, string?> { ["content-type"] = "application/json" });

        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), CreateOptions(fetch)));

        // 非 2xx = 终态 Error（默认不重试），错误体进 errorMessage。
        Assert.Single(fetch.Requests);
        Assert.Equal(new[] { "error" }, EventTypes(events));
        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("API key not valid", error.Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retryable_Error_Is_Retried_Only_Before_Stream()
    {
        var fetch = new MockFetch();
        fetch.EnqueueException(new FetchException(FetchErrorKind.Protocol, "429 slow down")
        {
            StatusCode = 429,
            Headers = new FetchHeaders(new Dictionary<string, string?> { ["retry-after-ms"] = "0" }),
        });
        fetch.EnqueueText(Sse(Chunk(Parts(TextPart("ok")), finishReason: "STOP")), headers: SseHeaders());

        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")),
            CreateOptions(fetch) with { MaxRetries = 1 }));

        // 429 在流建立前命中 ProviderRetry → 第二次尝试成功。
        Assert.Equal(2, fetch.Requests.Count);
        Assert.Equal(new[] { "start", "text_start", "text_delta", "text_end", "done" }, EventTypes(events));
        Assert.Equal(StopReason.Stop, Assert.IsType<AssistantMessageEvent.Done>(events[^1]).Reason);
    }

    [Fact]
    public async Task Stream_Failure_After_Start_Is_Terminal_Without_Resend()
    {
        var fetch = new MockFetch();
        var head = Sse(Chunk(Parts(TextPart("partial"))));
        fetch.Enqueue((_, _, _) => Task.FromResult<IFetchResponse>(new TestFetchResponse(
            200,
            new FetchHeaders(new Dictionary<string, string?> { ["content-type"] = "text/event-stream" }),
            new FailAfterHeadStream(head))));

        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")),
            CreateOptions(fetch) with { MaxRetries = 3 }));

        // 流建立后中途断开：绝不重发（哪怕 MaxRetries=3）。
        Assert.Single(fetch.Requests);
        Assert.Equal(new[] { "start", "text_start", "text_delta", "error" }, EventTypes(events));
        Assert.Equal(StopReason.Error, Assert.IsType<AssistantMessageEvent.Error>(events[^1]).Reason);
    }

    [Fact]
    public async Task Cancel_Mid_Stream_Is_Aborted_Keeps_Partial()
    {
        var cts = new CancellationTokenSource();
        var fetch = new MockFetch();
        var head = Sse(Chunk(Parts(TextPart("partial"))));
        fetch.Enqueue((_, _, _) => Task.FromResult<IFetchResponse>(new TestFetchResponse(
            200,
            new FetchHeaders(new Dictionary<string, string?> { ["content-type"] = "text/event-stream" }),
            new CancelBlockingStream(head, cts))));

        var options = CreateOptions(fetch) with { CancellationToken = cts.Token };
        var stream = GoogleGenerativeAi.Stream(CreateBudgetModel(), Context(UserMsg("hi")), options);

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
    public async Task Missing_Fetch_Is_Terminal_Error()
    {
        var fetch = new MockFetch();
        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), new GoogleOptions { ApiKey = "k" }));

        Assert.Empty(fetch.Requests);
        Assert.Equal(new[] { "error" }, EventTypes(events));
        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("fetch transport", error.Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_Api_Key_Is_Terminal_Error_Without_Request()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), new GoogleOptions { Fetch = fetch.Fetch }));

        Assert.Empty(fetch.Requests);
        Assert.Equal(new[] { "error" }, EventTypes(events));
        Assert.Contains(
            "No API key for provider: google",
            Assert.IsType<AssistantMessageEvent.Error>(events[^1]).Message.ErrorMessage,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // hooks / 超时映射
    // ------------------------------------------------------------------

    [Fact]
    public async Task Hooks_Fire_In_Order_And_Payload_Transform_Lands_In_Request()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(
            Chunk(Parts(TextPart("ok")), finishReason: "STOP", responseId: "resp_1")),
            headers: SseHeaders());

        var providerEvents = new List<string?>();
        var responseCount = 0;
        var options = CreateOptions(fetch, new GoogleOptions
        {
            OnPayload = (payload, model) =>
            {
                Assert.Equal("gemini-2.5-flash", model.Id);
                var body = Assert.IsType<JsonObject>(payload);
                body["generationConfig"]!["candidateCount"] = 1;
                return Task.FromResult<JsonNode?>(body);
            },
            OnResponse = (response, _) =>
            {
                Interlocked.Increment(ref responseCount);
                Assert.Equal(200, response.Status);
                return Task.CompletedTask;
            },
            OnProviderStreamEvent = (@event, _) =>
            {
                providerEvents.Add(@event["responseId"]?.GetValue<string>());
                return Task.CompletedTask;
            },
        });

        var events = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), options));

        // OnPayload 改写后的 payload 进请求体。
        Assert.Equal(1, Body(fetch)["generationConfig"]!["candidateCount"]!.GetValue<int>());
        Assert.Equal(1, Volatile.Read(ref responseCount));
        // 每条原始 chunk 透传 OnProviderStreamEvent（pi onProviderStreamEvent）。
        Assert.Equal(new[] { "resp_1" }, providerEvents);
        Assert.Equal(
            new[] { "start", "text_start", "text_delta", "text_end", "done" },
            EventTypes(events));
    }

    [Fact]
    public async Task TimeoutMs_Maps_To_All_FetchOptions()
    {
        var fetch = new MockFetch();
        FetchOptions? captured = null;
        fetch.Enqueue((_, options, _) =>
        {
            captured = options;
            return Task.FromResult<IFetchResponse>(new MockFetchResponse(
                200,
                Encoding.UTF8.GetBytes(Sse(Chunk(finishReason: "STOP"))),
                new FetchHeaders(new Dictionary<string, string?> { ["content-type"] = "text/event-stream" })));
        });
        _ = await CollectAsync(GoogleGenerativeAi.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")),
            CreateOptions(fetch) with { TimeoutMs = 1234 }));

        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), captured!.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), captured.FirstByteTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), captured.IdleTimeout);
    }

    // ------------------------------------------------------------------
    // StreamSimple（pi streamSimple：budget 表 / level 模型 / 禁用）
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamSimple_Without_Reasoning_Disables_Thinking()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.StreamSimple(
            CreateBudgetModel(), Context(UserMsg("hi")), new SimpleStreamOptions
            {
                ApiKey = "k",
                Fetch = fetch.Fetch,
            }));
        Assert.Equal(0, Body(fetch)["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
    }

    [Fact]
    public async Task StreamSimple_Budget_Model_Uses_Budget_Table()
    {
        async Task<int?> BudgetFor(string modelId, ThinkingLevel reasoning, ThinkingBudgets? budgets = null)
        {
            var fetch = new MockFetch();
            fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
            _ = await CollectAsync(GoogleGenerativeAi.StreamSimple(
                CreateBudgetModel(modelId), Context(UserMsg("hi")), new SimpleStreamOptions
                {
                    ApiKey = "k",
                    Fetch = fetch.Fetch,
                    Reasoning = reasoning,
                    ThinkingBudgets = budgets,
                }));
            return Body(fetch)["generationConfig"]!["thinkingConfig"]?["thinkingBudget"]?.GetValue<int>();
        }

        // getGoogleBudget 表：2.5-pro / 2.5-flash / 2.5-flash-lite（lite 必须先于 flash 判定）。
        Assert.Equal(32768, await BudgetFor("gemini-2.5-pro", ThinkingLevel.High));
        Assert.Equal(128, await BudgetFor("gemini-2.5-pro", ThinkingLevel.Minimal));
        Assert.Equal(24576, await BudgetFor("gemini-2.5-flash", ThinkingLevel.High));
        Assert.Equal(512, await BudgetFor("gemini-2.5-flash-lite", ThinkingLevel.Minimal));
        // 无表模型 → -1 动态预算。
        Assert.Equal(-1, await BudgetFor("gemini-1.5-pro", ThinkingLevel.High));
        // 自定义预算覆盖模型表。
        Assert.Equal(77, await BudgetFor(
            "gemini-2.5-flash", ThinkingLevel.High, new ThinkingBudgets { High = 77 }));
    }

    [Fact]
    public async Task StreamSimple_Level_Model_Uses_ThinkingLevel()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.StreamSimple(
            CreateLevelModel(), Context(UserMsg("hi")), new SimpleStreamOptions
            {
                ApiKey = "k",
                Fetch = fetch.Fetch,
                Reasoning = ThinkingLevel.Medium,
            }));
        var config = Body(fetch)["generationConfig"]!["thinkingConfig"]!;
        Assert.True(config["includeThoughts"]!.GetValue<bool>());
        Assert.Equal("MEDIUM", config["thinkingLevel"]!.GetValue<string>());

        // xhigh/max 不在 level 模型可用表（映射 null）：clamp 向下取到 high（pi clampThinkingLevel 同）。
        var clampedFetch = new MockFetch();
        clampedFetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        _ = await CollectAsync(GoogleGenerativeAi.StreamSimple(
            CreateLevelModel(), Context(UserMsg("hi")), new SimpleStreamOptions
            {
                ApiKey = "k",
                Fetch = clampedFetch.Fetch,
                Reasoning = ThinkingLevel.Max,
            }));
        Assert.Equal(
            "HIGH",
            Body(clampedFetch)["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>());
    }

    [Fact]
    public async Task StreamSimple_Passes_ToolChoice_And_Hooks()
    {
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var providerEvents = 0;
        _ = await CollectAsync(GoogleGenerativeAi.StreamSimple(
            CreateBudgetModel(),
            Context(SystemMsg("s", toolsAdded: new[] { Tool() }), UserMsg("hi")),
            new SimpleStreamOptions
            {
                ApiKey = "k",
                Fetch = fetch.Fetch,
                ToolChoice = ToolChoice.None,
                OnProviderStreamEvent = (_, _) =>
                {
                    Interlocked.Increment(ref providerEvents);
                    return Task.CompletedTask;
                },
            }));

        Assert.Equal("NONE", Body(fetch)["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>());
        Assert.True(Volatile.Read(ref providerEvents) > 0);
    }

    [Fact]
    public async Task StreamSimple_Missing_Api_Key_Is_Terminal_Error()
    {
        var fetch = new MockFetch();
        var events = await CollectAsync(GoogleGenerativeAi.StreamSimple(
            CreateBudgetModel(), Context(UserMsg("hi")), new SimpleStreamOptions
            {
                Fetch = fetch.Fetch,
            }));

        Assert.Empty(fetch.Requests);
        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("No API key", error.Message.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streams_Exposes_Stream_And_StreamSimple()
    {
        var streams = GoogleGenerativeAi.Streams();
        var fetch = new MockFetch();
        fetch.EnqueueText(Sse(Chunk(finishReason: "STOP")), headers: SseHeaders());
        var events = await CollectAsync(streams.Stream(
            CreateBudgetModel(), Context(UserMsg("hi")), CreateOptions(fetch)));
        Assert.Equal(StopReason.Stop, Assert.IsType<AssistantMessageEvent.Done>(events[^1]).Reason);
        Assert.NotNull(streams.StreamSimple);
    }

    // ------------------------------------------------------------------
    // 测试替身（与 AnthropicMessagesTests 同款）
    // ------------------------------------------------------------------

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

    /// <summary>读取时先按行回放一段固定内容、随后抛 IO 异常的流（流中断模拟）。</summary>
    private sealed class FailAfterHeadStream : Stream
    {
        private readonly MemoryStream _head;

        public FailAfterHeadStream(string head)
        {
            _head = new MemoryStream(Encoding.UTF8.GetBytes(head));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_head.Position < _head.Length)
            {
                return await _head.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            throw new IOException("connection reset by peer");
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
