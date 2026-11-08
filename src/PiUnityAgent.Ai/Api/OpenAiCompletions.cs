using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Api;

/// <summary>
/// OpenAI Chat Completions 兼容矩阵（探测缺省值 + <c>Model.Compat</c> 显式覆盖的解析结果）。
/// 对应 pi <c>ResolvedOpenAICompletionsCompat</c> / <c>getCompat</c>（openai-completions.ts 1587–1727）。
/// </summary>
internal sealed record OpenAiCompletionsCompat
{
    public bool SupportsStore { get; init; } = true;

    public bool SupportsDeveloperRole { get; init; }

    public bool SupportsReasoningEffort { get; init; } = true;

    public bool SupportsUsageInStreaming { get; init; } = true;

    public bool SupportsFinishReason { get; init; } = true;

    /// <summary><c>max_tokens</c> 或 <c>max_completion_tokens</c>。对应 pi <c>maxTokensField</c>。</summary>
    public string MaxTokensField { get; init; } = "max_completion_tokens";

    public bool RequiresToolResultName { get; init; }

    public bool RequiresAssistantAfterToolResult { get; init; }

    public bool RequiresThinkingAsText { get; init; }

    public bool RequiresReasoningContentOnAssistantMessages { get; init; }

    /// <summary>openai / openrouter / deepseek / together / baseten / zai / qwen / qwen-chat-template / chat-template / string-thinking / ant-ling。</summary>
    public string ThinkingFormat { get; init; } = "openai";

    public bool ZaiToolStream { get; init; }

    public bool SupportsThinkingTokenBudget { get; init; }

    /// <summary>顶层思考预算字段名。对应 pi <c>thinkingTokenBudgetField</c>。</summary>
    public string? ThinkingTokenBudgetField { get; init; }

    public bool SupportsStrictMode { get; init; }

    public bool SupportsOpenAIGrammarTools { get; init; }

    public bool SupportsMidConvoSystemMessages { get; init; }

    public bool SupportsMidConvoToolAdditions { get; init; }

    /// <summary><c>anthropic</c> 时在 system / 最后一条消息 / 最后一个工具上打 cache_control。</summary>
    public string? CacheControlFormat { get; init; }

    public bool SendSessionAffinityHeaders { get; init; }

    public string? SessionAffinityFormat { get; init; }

    public bool SupportsLongCacheRetention { get; init; } = true;

    public JsonObject? ChatTemplateKwargs { get; init; }

    public JsonObject? ChatTemplateArgs { get; init; }

    public JsonNode? VllmPriority { get; init; }
}

/// <summary>
/// OpenAI Chat Completions 协议适配器（pi <c>api/openai-completions.ts</c> 的 C# 移植）：
/// compat 探测矩阵、消息/工具转换（工具 ID 归一化、reasoning 回放、grammar 自定义工具）、
/// SSE delta 聚合（reasoning 先于 text）、usage/成本、finish_reason 语义、
/// 首字节前重试与错误/取消终态编码。
/// 通讯走 <see cref="FetchFunction"/> 端口（pi 里由 OpenAI SDK 承担）；失败一律编码为流内终态，不抛出。
/// </summary>
public static class OpenAiCompletions
{
    private const string DefaultThinkingTokenBudgetField = "thinking_token_budget";

    private static readonly string[] StreamReasoningFields = { "reasoning_content", "reasoning", "reasoning_text" };

    private static readonly string[] ReasoningFields = { "reasoning", "reasoning_content", "reasoning_text" };

    private static readonly string[] UnsupportedStrictSchemaKeys =
    {
        "$ref",
        "$defs",
        "definitions",
        "allOf",
        "oneOf",
        "patternProperties",
        "dependentSchemas",
        "dependencies",
        "unevaluatedProperties",
        "propertyNames",
        "contains",
        "prefixItems",
        "not",
        "if",
        "then",
        "else",
    };

    private static readonly Regex UnpairedSurrogateRegex = new(
        @"[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]",
        RegexOptions.Compiled);

    private const int MaxProviderErrorBodyChars = 4000;
    private const int OpenAiPromptCacheKeyMaxLength = 64;
    private const int MinAnswerTokens = 1024;
    private const char GrammarFormatLark = 'l';
    private const char GrammarFormatRegex = 'r';

    // ---------------------------------------------------------------------
    // 公共入口
    // ---------------------------------------------------------------------

    public static ProviderStreams Streams() => new()
    {
        Stream = Run,
        StreamSimple = StreamSimple,
    };

    public static IAssistantMessageEventStream Stream(
        Model model,
        TranscriptContext context,
        OpenAiCompletionsOptions? options = null)
        => Run(model, context, options);

    public static IAssistantMessageEventStream StreamSimple(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options = null)
        => Run(model, context, BuildSimpleOptions(model, context, options));

    // ---------------------------------------------------------------------
    // stream（pi openai-completions.ts 310–717 的 IIFE 形态）
    // ---------------------------------------------------------------------

    private static IAssistantMessageEventStream Run(Model model, TranscriptContext context, StreamOptions? options)
    {
        var stream = new AssistantMessageEventStream();
        var compat = GetCompat(model);
        var normalizedContext = ResolveTranscript(context, compat.SupportsMidConvoSystemMessages);

        _ = RunCoreAsync(stream, model, normalizedContext, options, compat);
        return stream;
    }

    private static async Task RunCoreAsync(
        AssistantMessageEventStream stream,
        Model model,
        TranscriptContext normalizedContext,
        StreamOptions? options,
        OpenAiCompletionsCompat compat)
    {
        var output = new MutableOutput(model);
        var blocks = new List<StreamBlock>();
        List<JsonObject>? streamedReasoningDetails = null;
        try
        {
            var apiKey = GetClientApiKey(model.Provider, options?.ApiKey, options?.Headers);
            var grammarToolInputProperties = CreateGrammarToolInputProperties(
                GetDeclaredTools(normalizedContext.Messages),
                compat.SupportsOpenAIGrammarTools);
            var cacheRetention = ResolveCacheRetention(options?.CacheRetention, options?.Env);
            var cacheSessionId = cacheRetention == CacheRetention.None ? null : options?.SessionId;

            JsonNode payload = BuildParams(model, normalizedContext, options, compat, cacheRetention, grammarToolInputProperties);
            if (options?.OnPayload is { } onPayload)
            {
                var next = await onPayload(payload, model).ConfigureAwait(false);
                if (next is not null)
                {
                    payload = next;
                }
            }

            var fetch = options?.Fetch;
            if (fetch is null)
            {
                throw new InvalidOperationException(
                    "No fetch transport configured; set options.Fetch to enable provider requests.");
            }

            var signal = options?.CancellationToken ?? default;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(signal);
            var fetchOptions = BuildFetchOptions(options?.TimeoutMs);
            var request = BuildRequest(model, normalizedContext, options, payload, apiKey, cacheSessionId, compat);

            var response = await ProviderRetry.RunAsync(
                async requestToken =>
                {
                    var attempt = await fetch(request, fetchOptions, requestToken).ConfigureAwait(false);
                    if (attempt.StatusCode is < 200 or >= 300)
                    {
                        // pi：SDK 在 retryProviderRequest 内对非 2xx 抛错，因此 408/409/429/5xx 会被重试。
                        // ProviderRetry 只识别 FetchException，故这里抛携带 status/headers/body 的 FetchException。
                        string errorBody;
                        try
                        {
                            errorBody = await ReadBodyAsync(attempt.Body, requestToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            await attempt.DisposeAsync().ConfigureAwait(false);
                        }

                        throw new FetchException(
                            FetchErrorKind.Protocol,
                            errorBody.Trim().Length > 0
                                ? $"{attempt.StatusCode}: {errorBody}"
                                : $"HTTP {attempt.StatusCode} (no body)")
                        {
                            StatusCode = attempt.StatusCode,
                            Headers = attempt.Headers,
                            ErrorBody = errorBody,
                        };
                    }

                    return attempt;
                },
                new ProviderRetryOptions
                {
                    MaxRetries = options?.MaxRetries ?? 0,
                    MaxRetryDelayMs = options?.MaxRetryDelayMs ?? ProviderRetry.DefaultMaxRetryDelayMs,
                },
                lifetime.Token).ConfigureAwait(false);

            await using var responseScope = response;
            if (options?.OnResponse is { } onResponse)
            {
                await onResponse(
                    new ProviderResponse(response.StatusCode, HeadersToRecord(response.Headers)),
                    model).ConfigureAwait(false);
            }

            stream.Push(new AssistantMessageEvent.Start(output.Snapshot(blocks)));

            var textBlock = (TextStreamBlock?)null;
            var thinkingBlock = (ThinkingStreamBlock?)null;
            var hasFinishReason = false;
            var toolCallBlocksByIndex = new Dictionary<int, ToolCallStreamBlock>();
            var toolCallBlocksById = new Dictionary<string, ToolCallStreamBlock>();

            void ApplyStreamedReasoningDetails(ThinkingStreamBlock block)
            {
                if (streamedReasoningDetails is not null)
                {
                    block.ThinkingSignature = SerializeReasoningDetails(streamedReasoningDetails);
                }
            }

            int ContentIndex(StreamBlock block) => blocks.IndexOf(block);

            TextStreamBlock EnsureTextBlock()
            {
                if (textBlock is null)
                {
                    textBlock = new TextStreamBlock();
                    blocks.Add(textBlock);
                    stream.Push(new AssistantMessageEvent.TextStart(ContentIndex(textBlock), output.Snapshot(blocks)));
                }

                return textBlock;
            }

            ThinkingStreamBlock EnsureThinkingBlock(string thinkingSignature)
            {
                if (thinkingBlock is null)
                {
                    thinkingBlock = new ThinkingStreamBlock { ThinkingSignature = thinkingSignature };
                    blocks.Add(thinkingBlock);
                    stream.Push(new AssistantMessageEvent.ThinkingStart(
                        ContentIndex(thinkingBlock), output.Snapshot(blocks)));
                }

                return thinkingBlock;
            }

            // 返回 (block, input)；块状态机见 pi stream 485–540。
            (ToolCallStreamBlock Block, ToolCallInput Input) EnsureToolCallBlock(JsonObject toolCall)
            {
                var streamIndex = JsonNum(toolCall, "index") is { } rawIndex ? (int)rawIndex : (int?)null;
                var function = JsonObj(toolCall, "function");
                var custom = JsonObj(toolCall, "custom");
                var name = JsonStr(function, "name") ?? JsonStr(custom, "name") ?? "";
                var id = JsonStr(toolCall, "id") ?? "";

                ToolCallStreamBlock? block = null;
                if (streamIndex is { } byIndex)
                {
                    toolCallBlocksByIndex.TryGetValue(byIndex, out block);
                }

                if (block is null && id.Length > 0)
                {
                    toolCallBlocksById.TryGetValue(id, out block);
                }

                if (block is null)
                {
                    // 注：fallback 的 "input" 属性只有在模型编造未知工具时才会走到，至少有地方暂存。
                    string? customInputProperty = null;
                    if (custom is not null && function is null)
                    {
                        customInputProperty = grammarToolInputProperties.TryGetValue(name, out var property)
                            ? property
                            : "input";
                    }

                    block = new ToolCallStreamBlock
                    {
                        Id = id,
                        Name = name,
                        Arguments = customInputProperty is null
                            ? new JsonObject()
                            : new JsonObject { [customInputProperty] = "" },
                    };
                    var input = customInputProperty is not null
                        ? ToolCallInput.Custom(customInputProperty, new GrammarToolInputJsonBuffer())
                        : ToolCallInput.Json(new ToolCallArguments(), null);
                    block.Input = input;
                    if (streamIndex is { } newIndex)
                    {
                        toolCallBlocksByIndex[newIndex] = block;
                    }

                    if (id.Length > 0)
                    {
                        toolCallBlocksById[id] = block;
                    }

                    blocks.Add(block);
                    stream.Push(new AssistantMessageEvent.ToolCallStart(
                        ContentIndex(block), output.Snapshot(blocks)));
                }

                var state = block.Input;
                if (streamIndex is { } currentIndex && state.StreamIndex is null)
                {
                    state.StreamIndex = currentIndex;
                    toolCallBlocksByIndex[currentIndex] = block;
                }

                if (id.Length > 0)
                {
                    toolCallBlocksById[id] = block;
                }

                if (block.Name.Length == 0 && name.Length > 0)
                {
                    block.Name = name;
                }

                if (custom is not null && function is null && state.Kind != ToolCallInputKind.Custom)
                {
                    var customInputProperty = grammarToolInputProperties.TryGetValue(block.Name, out var property)
                        ? property
                        : "input";
                    block.Arguments = new JsonObject { [customInputProperty] = "" };
                    block.Input = ToolCallInput.Custom(customInputProperty, new GrammarToolInputJsonBuffer());
                }

                return (block, block.Input);
            }

            void FinishBlock(StreamBlock block)
            {
                var contentIndex = ContentIndex(block);
                if (contentIndex == -1)
                {
                    return;
                }

                switch (block)
                {
                    case TextStreamBlock text:
                        stream.Push(new AssistantMessageEvent.TextEnd(
                            contentIndex, text.Text.ToString(), output.Snapshot(blocks)));
                        break;
                    case ThinkingStreamBlock thinking:
                        ApplyStreamedReasoningDetails(thinking);
                        stream.Push(new AssistantMessageEvent.ThinkingEnd(
                            contentIndex, thinking.Thinking.ToString(), output.Snapshot(blocks)));
                        break;
                    case ToolCallStreamBlock toolCall:
                    {
                        var input = toolCall.Input;
                        if (input.Kind == ToolCallInputKind.Custom)
                        {
                            var delta = CloseGrammarToolInputJson(input.Buffer!, input.Property!, null);
                            if (delta is not null)
                            {
                                toolCall.Arguments = new JsonObject { [input.Property!] = input.Buffer!.Input };
                                stream.Push(new AssistantMessageEvent.ToolCallDelta(
                                    contentIndex, delta, output.Snapshot(blocks)));
                            }
                        }
                        else
                        {
                            // pi parseToolCallArguments：非法 JSON 不失败流，落 argumentsError。
                            try
                            {
                                toolCall.Arguments = input.Arguments!.Finish();
                            }
                            catch (JsonException argumentsError)
                            {
                                toolCall.Arguments = new JsonObject();
                                toolCall.ArgumentsError =
                                    $"{argumentsError.Message}\n\nReceived arguments:\n{input.Arguments!.Text}";
                            }
                        }

                        stream.Push(new AssistantMessageEvent.ToolCallEnd(
                            contentIndex, toolCall.ToToolCall(), output.Snapshot(blocks)));
                        break;
                    }
                }
            }

            await foreach (var sse in SseEventReader.ReadAsync(response.Body, lifetime.Token).ConfigureAwait(false))
            {
                // OpenAI SDK 吞掉 [DONE]；data 为空的事件也不产 chunk。
                if (sse.Data == "[DONE]") continue;
                if (string.IsNullOrWhiteSpace(sse.Data)) continue;
                if (JsonNode.Parse(sse.Data) is not JsonObject chunk)
                {
                    throw new InvalidOperationException("OpenAI SSE data is not a JSON object");
                }

                if (options?.OnProviderStreamEvent is { } observer)
                {
                    await observer(chunk, model).ConfigureAwait(false);
                }

                // chunk.id 是本次 chat completion 的唯一标识；同一流内所有 chunk 共享。
                if (string.IsNullOrEmpty(output.ResponseId))
                {
                    output.ResponseId = JsonStr(chunk, "id");
                }

                var responseModel = JsonStr(chunk, "model");
                if (responseModel is { Length: > 0 } && responseModel != model.Id && string.IsNullOrEmpty(output.ResponseModel))
                {
                    output.ResponseModel = responseModel;
                }

                var chunkUsage = JsonObj(chunk, "usage");
                if (chunkUsage is not null)
                {
                    output.Usage = ParseChunkUsage(chunkUsage, model);
                }

                if (chunk["choices"] is not JsonArray { Count: > 0 } choices
                    || choices[0] is not JsonObject choice)
                {
                    continue;
                }

                // 回退：部分 provider（如 Moonshot）把 usage 放在 choice.usage。
                if (chunkUsage is null && JsonObj(choice, "usage") is { } choiceUsage)
                {
                    output.Usage = ParseChunkUsage(choiceUsage, model);
                }

                if (JsonStr(choice, "finish_reason") is { Length: > 0 } finishReason)
                {
                    output.RawStopReason = finishReason;
                    var mapped = MapStopReasonDetailed(finishReason);
                    output.StopReason = mapped.StopReason;
                    if (mapped.ErrorMessage is not null)
                    {
                        output.ErrorMessage = mapped.ErrorMessage;
                    }

                    hasFinishReason = true;
                }

                if (JsonObj(choice, "delta") is not { } delta) continue;

                // reasoning 先于 content：LiteLLM 等代理把 reasoning 和答案合进同一 delta，
                // thinking 块必须先建。reasoning_content（llama.cpp）/ reasoning（其他兼容端）
                // / reasoning_text 三字段取第一个非空，避免重复（chutes.ai 两字段同文）。
                string? foundReasoningField = null;
                string? foundReasoningDelta = null;
                foreach (var field in StreamReasoningFields)
                {
                    if (JsonStr(delta, field) is { Length: > 0 } reasoningDelta)
                    {
                        foundReasoningField = field;
                        foundReasoningDelta = reasoningDelta;
                        break;
                    }
                }

                if (foundReasoningField is not null && foundReasoningDelta is not null)
                {
                    var thinkingSignature = model.Provider == "opencode-go" && foundReasoningField == "reasoning"
                        ? "reasoning_content"
                        : foundReasoningField;
                    var block = EnsureThinkingBlock(thinkingSignature);
                    block.Thinking.Append(foundReasoningDelta);
                    stream.Push(new AssistantMessageEvent.ThinkingDelta(
                        ContentIndex(block), foundReasoningDelta, output.Snapshot(blocks)));
                }

                if (delta["reasoning_details"] is JsonArray reasoningDetails)
                {
                    foreach (var detail in reasoningDetails)
                    {
                        if (detail is not JsonObject reasoningDetail || !IsOpenAIReasoningDetail(reasoningDetail))
                        {
                            continue;
                        }

                        EnsureThinkingBlock("");
                        streamedReasoningDetails ??= new List<JsonObject>();
                        AppendOpenAIReasoningDetail(streamedReasoningDetails, reasoningDetail);
                    }
                }

                if (JsonStr(delta, "content") is { Length: > 0 } contentDelta)
                {
                    var block = EnsureTextBlock();
                    block.Text.Append(contentDelta);
                    stream.Push(new AssistantMessageEvent.TextDelta(
                        ContentIndex(block), contentDelta, output.Snapshot(blocks)));
                }

                if (delta["tool_calls"] is JsonArray toolCalls)
                {
                    foreach (var node in toolCalls)
                    {
                        if (node is not JsonObject toolCallDelta) continue;
                        var (block, input) = EnsureToolCallBlock(toolCallDelta);
                        var id = JsonStr(toolCallDelta, "id");
                        if (block.Id.Length == 0 && !string.IsNullOrEmpty(id))
                        {
                            block.Id = id!;
                            toolCallBlocksById[id!] = block;
                        }

                        var function = JsonObj(toolCallDelta, "function");
                        var custom = JsonObj(toolCallDelta, "custom");
                        var name = JsonStr(function, "name") ?? JsonStr(custom, "name");
                        if (block.Name.Length == 0 && !string.IsNullOrEmpty(name))
                        {
                            block.Name = name!;
                        }

                        var toolDelta = "";
                        if (JsonStr(function, "arguments") is { Length: > 0 } argumentsDelta
                            && input.Kind == ToolCallInputKind.Json)
                        {
                            toolDelta = argumentsDelta;
                            input.Arguments!.Append(argumentsDelta);
                        }
                        else if (JsonStr(custom, "input") is { Length: > 0 } inputDelta
                                 && input.Kind == ToolCallInputKind.Custom)
                        {
                            toolDelta = AppendGrammarToolInputJsonDelta(input.Buffer!, input.Property!, inputDelta)
                                ?? "";
                            block.Arguments = new JsonObject { [input.Property!] = input.Buffer!.Input };
                        }

                        stream.Push(new AssistantMessageEvent.ToolCallDelta(
                            ContentIndex(block), toolDelta, output.Snapshot(blocks)));
                    }
                }
            }

            foreach (var block in blocks)
            {
                FinishBlock(block);
            }

            if (signal.IsCancellationRequested)
            {
                throw new OperationCanceledException("Request was aborted", signal);
            }

            if (output.StopReason == StopReason.Aborted)
            {
                throw new OperationCanceledException("Request was aborted", signal);
            }

            if (!hasFinishReason && !compat.SupportsFinishReason)
            {
                output.StopReason = blocks.Any(block => block is ToolCallStreamBlock)
                    ? StopReason.ToolUse
                    : StopReason.Stop;
            }

            if (output.StopReason == StopReason.Error)
            {
                throw new InvalidOperationException(
                    output.ErrorMessage ?? "Provider returned an error stop reason");
            }

            if ((compat.SupportsFinishReason && !hasFinishReason) || output.StopReason == StopReason.Pending)
            {
                throw new InvalidOperationException("Stream ended without finish_reason");
            }

            var final = output.Snapshot(blocks);
            stream.Push(new AssistantMessageEvent.Done(output.StopReason, final));
            stream.End(final);
        }
        catch (Exception error)
        {
            // pi 696–709：thinking 块补写 reasoning_details 回放数据，错误编码为终态。
            if (streamedReasoningDetails is not null)
            {
                foreach (var block in blocks)
                {
                    if (block is ThinkingStreamBlock thinking)
                    {
                        thinking.ThinkingSignature = SerializeReasoningDetails(streamedReasoningDetails);
                    }
                }
            }

            var signal = options?.CancellationToken ?? default;
            var aborted = signal.IsCancellationRequested;
            output.StopReason = aborted ? StopReason.Aborted : StopReason.Error;
            // pi 的中止错误显式为 Error("Request was aborted")；取消令牌触发的 OCE 归一到同一文案。
            output.ErrorMessage = aborted && error is OperationCanceledException
                ? "Request was aborted"
                : FormatProviderError(error);
            var failed = output.Snapshot(blocks);
            stream.Push(new AssistantMessageEvent.Error(output.StopReason, failed));
            stream.End(failed);
        }
    }

    // ---------------------------------------------------------------------
    // 请求构造（pi createClient 739–784 + headers 合并）
    // ---------------------------------------------------------------------

    private static FetchOptions? BuildFetchOptions(int? timeoutMs)
    {
        if (timeoutMs is not { } timeout)
        {
            return null;
        }

        var span = TimeSpan.FromMilliseconds(timeout);
        return new FetchOptions
        {
            ConnectTimeout = span,
            FirstByteTimeout = span,
            IdleTimeout = span,
        };
    }

    private static FetchRequest BuildRequest(
        Model model,
        TranscriptContext context,
        StreamOptions? options,
        JsonNode payload,
        string apiKey,
        string? cacheSessionId,
        OpenAiCompletionsCompat compat)
    {
        var headers = new FetchHeaders();

        // pi createClient：identity 头在最下，model.headers 之上，copilot 动态头随后。
        foreach (var entry in ClientIdentityHelpers.GetIdentityHeaders(
            new ClientIdentityTarget(model.Provider, model.BaseUrl), options?.Identity))
        {
            headers[entry.Key] = entry.Value;
        }

        if (model.Headers is not null)
        {
            foreach (var entry in model.Headers)
            {
                if (entry.Value is null) headers.Remove(entry.Key);
                else headers[entry.Key] = entry.Value;
            }
        }

        if (model.Provider == "github-copilot")
        {
            headers["X-Initiator"] = InferCopilotInitiator(context.Messages);
            headers["Openai-Intent"] = "conversation-edits";
            if (HasCopilotVisionInput(context.Messages))
            {
                headers["Copilot-Vision-Request"] = "true";
            }
        }

        if (cacheSessionId is not null && compat.SendSessionAffinityHeaders)
        {
            if (compat.SessionAffinityFormat == "openrouter")
            {
                headers["x-session-id"] = cacheSessionId;
            }
            else
            {
                if (compat.SessionAffinityFormat == "openai")
                {
                    headers["session_id"] = cacheSessionId;
                }

                headers["x-client-request-id"] = cacheSessionId;
                headers["x-session-affinity"] = cacheSessionId;
            }
        }

        // SDK 等价默认头；options.Headers 最后合并以便覆盖默认（null 抑制）。
        headers["authorization"] = $"Bearer {apiKey}";
        headers["content-type"] = "application/json";
        headers["accept"] = "text/event-stream";

        if (options?.Headers is not null)
        {
            foreach (var entry in options.Headers)
            {
                if (entry.Value is null) headers.Remove(entry.Key);
                else headers[entry.Key] = entry.Value;
            }
        }

        var url = $"{model.BaseUrl.TrimEnd('/')}/chat/completions";
        return new FetchRequest("POST", url, headers, Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

    private static IReadOnlyDictionary<string, string> HeadersToRecord(FetchHeaders headers)
    {
        var record = new Dictionary<string, string>();
        foreach (var entry in headers)
        {
            if (entry.Value is { } value)
            {
                record[entry.Key] = value;
            }
        }

        return record;
    }

    private static async Task<string> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        // netstandard2.1 的 ReadToEndAsync 无 CancellationToken 重载；错误体读取后随响应一起中止。
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new StreamReader(body, Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static string InferCopilotInitiator(IReadOnlyList<Message> messages)
    {
        var last = messages.Count > 0 ? messages[^1] : null;
        return last is not null && last.Role != "user" ? "agent" : "user";
    }

    private static bool HasCopilotVisionInput(IReadOnlyList<Message> messages)
        => messages.Any(message => message is UserMessage or ToolResultMessage
            && message switch
            {
                UserMessage user => user.Content.ToBlocks().OfType<ImageContent>().Any(),
                ToolResultMessage tool => tool.Content.OfType<ImageContent>().Any(),
                _ => false,
            });

    // ---------------------------------------------------------------------
    // buildParams（pi 785–996）
    // ---------------------------------------------------------------------

    internal static JsonObject BuildParams(
        Model model,
        TranscriptContext context,
        StreamOptions? options,
        OpenAiCompletionsCompat compat,
        CacheRetention cacheRetention,
        IReadOnlyDictionary<string, string> grammarToolInputProperties)
    {
        var completionsOptions = options as OpenAiCompletionsOptions;
        var reasoningEffort = completionsOptions?.ReasoningEffort;
        var thinkingBudgets = completionsOptions?.ThinkingBudgets;
        var toolChoice = completionsOptions?.ToolChoice;

        var transcriptToolSupport = compat.SupportsMidConvoSystemMessages && compat.SupportsMidConvoToolAdditions
            ? TranscriptToolSupport.Additions
            : TranscriptToolSupport.None;
        var transcriptTools = ResolveTranscriptTools(context.Messages, transcriptToolSupport);
        var messages = ConvertMessages(model, context, compat, grammarToolInputProperties);
        var cacheControl = GetCompatCacheControl(compat, cacheRetention);

        var parameters = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = messages,
            ["stream"] = true,
        };

        var usePromptCacheKey = (model.BaseUrl.Contains("api.openai.com", StringComparison.Ordinal)
                && cacheRetention != CacheRetention.None)
            || (cacheRetention == CacheRetention.Long && compat.SupportsLongCacheRetention);
        if (usePromptCacheKey)
        {
            var promptCacheKey = ClampOpenAIPromptCacheKey(options?.SessionId);
            if (promptCacheKey is not null)
            {
                parameters["prompt_cache_key"] = promptCacheKey;
            }
        }

        if (cacheRetention == CacheRetention.Long && compat.SupportsLongCacheRetention)
        {
            parameters["prompt_cache_retention"] = "24h";
        }

        if (compat.SupportsUsageInStreaming)
        {
            parameters["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (compat.SupportsStore)
        {
            parameters["store"] = false;
        }

        if (options?.MaxTokens is { } maxTokens && maxTokens > 0)
        {
            if (compat.MaxTokensField == "max_tokens")
            {
                // OpenAI 已弃用，但部分 OpenAI-compatible provider 只收 max_tokens。
                parameters["max_tokens"] = maxTokens;
            }
            else
            {
                parameters["max_completion_tokens"] = maxTokens;
            }
        }

        if (options?.Temperature is { } temperature)
        {
            parameters["temperature"] = temperature;
        }

        JsonArray? tools = null;
        if (transcriptTools.RequestTools.Count > 0)
        {
            tools = ConvertTools(transcriptTools.RequestTools, compat);
            parameters["tools"] = tools;
            if (compat.ZaiToolStream)
            {
                parameters["tool_stream"] = true;
            }
        }
        else if (HasToolHistory(context.Messages))
        {
            // Anthropic（经 LiteLLM/代理）在会话含 tool_calls/tool 结果时要求 tools 字段存在。
            parameters["tools"] = new JsonArray();
        }

        if (cacheControl is not null)
        {
            ApplyAnthropicCacheControl(messages, tools, cacheControl);
        }

        if (toolChoice is not null)
        {
            parameters["tool_choice"] = toolChoice.DeepClone();
        }

        if (compat.VllmPriority is { } priority)
        {
            parameters["priority"] = priority.DeepClone();
        }

        var thinkingTokenBudgetField = ResolveThinkingTokenBudgetField(compat);
        var thinkingBudget = ResolveClampedThinkingBudget(model, reasoningEffort, thinkingBudgets, parameters);

        if (compat.ThinkingFormat == "zai" && model.Reasoning)
        {
            parameters["thinking"] = reasoningEffort is not null
                ? new JsonObject { ["type"] = "enabled", ["clear_thinking"] = false }
                : new JsonObject { ["type"] = "disabled" };
            if (reasoningEffort is not null && compat.SupportsReasoningEffort)
            {
                var effort = MappedOrRequested(model, reasoningEffort.Value);
                if (effort is not null)
                {
                    parameters["reasoning_effort"] = effort;
                }
            }
        }
        else if (compat.ThinkingFormat == "qwen" && model.Reasoning)
        {
            parameters["enable_thinking"] = reasoningEffort is not null;
            if (reasoningEffort is not null && compat.SupportsReasoningEffort)
            {
                parameters["reasoning_effort"] = MappedCoalesce(model, reasoningEffort.Value);
            }
        }
        else if (compat.ThinkingFormat == "qwen-chat-template" && model.Reasoning)
        {
            parameters["chat_template_kwargs"] = new JsonObject
            {
                ["enable_thinking"] = reasoningEffort is not null,
                ["preserve_thinking"] = true,
            };
        }
        else if (compat.ThinkingFormat == "chat-template" && model.Reasoning)
        {
            var chatTemplateValues = BuildChatTemplateValues(
                model, reasoningEffort, compat.ChatTemplateKwargs, thinkingBudget);
            if (chatTemplateValues is not null)
            {
                parameters["chat_template_kwargs"] = chatTemplateValues;
            }
        }
        else if (compat.ThinkingFormat == "baseten" && model.Reasoning)
        {
            var chatTemplateArgs = BuildChatTemplateValues(
                model, reasoningEffort, compat.ChatTemplateArgs, thinkingBudget);
            if (chatTemplateArgs is not null)
            {
                parameters["chat_template_args"] = chatTemplateArgs;
            }

            if (compat.SupportsReasoningEffort)
            {
                var effort = reasoningEffort is { } requested
                    ? MappedOrRequested(model, requested)
                    : OffMapping(model);
                if (effort is not null)
                {
                    parameters["reasoning_effort"] = effort;
                }
            }
        }
        else if (compat.ThinkingFormat == "deepseek" && model.Reasoning)
        {
            if (reasoningEffort is not null)
            {
                parameters["thinking"] = new JsonObject { ["type"] = "enabled" };
            }
            else if (!OffExplicitlyDisabled(model))
            {
                parameters["thinking"] = new JsonObject { ["type"] = "disabled" };
            }

            if (reasoningEffort is not null && compat.SupportsReasoningEffort)
            {
                parameters["reasoning_effort"] = MappedCoalesce(model, reasoningEffort.Value);
            }
        }
        else if (compat.ThinkingFormat == "openrouter" && model.Reasoning)
        {
            // OpenRouter 用嵌套 reasoning 对象在 provider 间归一 reasoning。
            if (reasoningEffort is { } openRouterRequested)
            {
                parameters["reasoning"] = new JsonObject
                {
                    ["effort"] = MappedCoalesce(model, openRouterRequested),
                };
            }
            else if (!OffExplicitlyDisabled(model))
            {
                parameters["reasoning"] = new JsonObject { ["effort"] = OffMapping(model) ?? "none" };
            }
        }
        else if (compat.ThinkingFormat == "ant-ling" && model.Reasoning && reasoningEffort is { } antLingRequested)
        {
            var effort = MappedOnly(model, antLingRequested);
            if (effort is not null)
            {
                parameters["reasoning"] = new JsonObject { ["effort"] = effort };
            }
        }
        else if (compat.ThinkingFormat == "together" && model.Reasoning)
        {
            parameters["reasoning"] = new JsonObject { ["enabled"] = reasoningEffort is not null };
            if (reasoningEffort is not null && compat.SupportsReasoningEffort)
            {
                parameters["reasoning_effort"] = MappedCoalesce(model, reasoningEffort.Value);
            }
        }
        else if (compat.ThinkingFormat == "string-thinking" && model.Reasoning)
        {
            if (reasoningEffort is { } stringRequested)
            {
                parameters["thinking"] = MappedCoalesce(model, stringRequested);
            }
            else if (!OffExplicitlyDisabled(model))
            {
                parameters["thinking"] = OffMapping(model) ?? "none";
            }
        }
        else if (reasoningEffort is not null && model.Reasoning && compat.SupportsReasoningEffort)
        {
            // OpenAI 风格 reasoning_effort。
            parameters["reasoning_effort"] = MappedCoalesce(model, reasoningEffort.Value);
        }
        else if (reasoningEffort is null && model.Reasoning && compat.SupportsReasoningEffort)
        {
            if (OffMapping(model) is { } offValue)
            {
                parameters["reasoning_effort"] = offValue;
            }
        }

        if (thinkingTokenBudgetField is not null && thinkingBudget is { } budget)
        {
            parameters[thinkingTokenBudgetField] = budget;
        }

        // OpenRouter / Vercel AI Gateway 路由偏好（读取原始 model.compat，pi 同样直读）。
        if (model.Compat is not null)
        {
            if (model.Compat.TryGetPropertyValue("openRouterRouting", out var openRouterNode)
                && openRouterNode is JsonObject openRouterRouting)
            {
                parameters["provider"] = openRouterRouting.DeepClone();
            }

            if (model.Compat.TryGetPropertyValue("vercelGatewayRouting", out var gatewayNode)
                && gatewayNode is JsonObject gatewayRouting)
            {
                var only = gatewayRouting["only"] as JsonArray;
                var order = gatewayRouting["order"] as JsonArray;
                if (only is not null || order is not null)
                {
                    var gatewayOptions = new JsonObject();
                    if (only is not null) gatewayOptions["only"] = only.DeepClone();
                    if (order is not null) gatewayOptions["order"] = order.DeepClone();
                    parameters["providerOptions"] = new JsonObject { ["gateway"] = gatewayOptions };
                }
            }
        }

        // 最后合并：模型/请求级采样参数覆盖具名请求字段。
        var samplingLevel = reasoningEffort is { } samplingEffort
            ? ToModelThinkingLevel(samplingEffort)
            : ModelThinkingLevel.Off;
        if (ResolveSamplingParams(model, samplingLevel, options?.SamplingParams) is { } samplingParams)
        {
            foreach (var entry in samplingParams)
            {
                parameters[entry.Key] = entry.Value?.DeepClone();
            }
        }

        return parameters;
    }

    private static string? ResolveThinkingTokenBudgetField(OpenAiCompletionsCompat compat)
        => compat.ThinkingTokenBudgetField
            ?? (compat.SupportsThinkingTokenBudget ? DefaultThinkingTokenBudgetField : null);

    private static int? ResolveClampedThinkingBudget(
        Model model,
        ThinkingLevel? reasoningEffort,
        ThinkingBudgets? thinkingBudgets,
        JsonObject parameters)
    {
        if (reasoningEffort is null || !model.Reasoning) return null;
        var ceiling = JsonNum(parameters, "max_tokens")
            ?? JsonNum(parameters, "max_completion_tokens")
            ?? model.MaxTokens;
        var budget = ClampThinkingBudgetToAnswerRoom(
            ThinkingBudgetForLevel(reasoningEffort.Value, thinkingBudgets),
            (int)ceiling);
        return budget > 0 ? budget : null;
    }

    private static JsonObject? BuildChatTemplateValues(
        Model model,
        ThinkingLevel? reasoningEffort,
        JsonObject? values,
        int? thinkingBudget)
    {
        if (values is null || values.Count == 0) return null;
        var resolved = new JsonObject();

        foreach (var entry in values)
        {
            var resolvedValue = ResolveChatTemplateKwargValue(model, reasoningEffort, entry.Value, thinkingBudget);
            if (resolvedValue is not null)
            {
                resolved[entry.Key] = resolvedValue;
            }
        }

        return resolved.Count > 0 ? resolved : null;
    }

    private static JsonNode? ResolveChatTemplateKwargValue(
        Model model,
        ThinkingLevel? reasoningEffort,
        JsonNode? value,
        int? thinkingBudget)
    {
        if (value is not JsonObject config)
        {
            return value?.DeepClone();
        }

        if (reasoningEffort is null && JsonBool(config, "omitWhenOff") == true)
        {
            return null;
        }

        var variable = JsonStr(config, "$var");
        if (variable == "thinking.enabled")
        {
            return reasoningEffort is not null;
        }

        if (variable == "thinking.budget")
        {
            return thinkingBudget is { } budget ? JsonValue.Create(budget) : null;
        }

        var level = reasoningEffort is { } effort ? ToModelThinkingLevel(effort) : ModelThinkingLevel.Off;
        string? mappedValue = null;
        var hasMapping = model.ThinkingLevelMap is { } map && map.TryGetValue(level, out mappedValue);
        if (!hasMapping)
        {
            return reasoningEffort is not null ? ToThinkingLevelWire(reasoningEffort.Value) : null;
        }

        return mappedValue;
    }

    private static string? OffMapping(Model model)
        => model.ThinkingLevelMap is { } map && map.TryGetValue(ModelThinkingLevel.Off, out var off)
            ? off
            : null;

    private static bool OffExplicitlyDisabled(Model model)
        => model.ThinkingLevelMap is { } map
            && map.TryGetValue(ModelThinkingLevel.Off, out var off)
            && off is null;

    /// <summary>pi：key 缺失时回落请求档位；key 存在但值为 null 时不发参数（返回 null）。</summary>
    private static string? MappedOrRequested(Model model, ThinkingLevel level)
    {
        if (model.ThinkingLevelMap is not { } map) return ToThinkingLevelWire(level);
        return map.TryGetValue(ToModelThinkingLevel(level), out var mapped) ? mapped : ToThinkingLevelWire(level);
    }

    private static string? MappedOnly(Model model, ThinkingLevel level)
    {
        if (model.ThinkingLevelMap is not { } map) return null;
        return map.TryGetValue(ToModelThinkingLevel(level), out var mapped) ? mapped : null;
    }

    private static string MappedCoalesce(Model model, ThinkingLevel level)
    {
        if (model.ThinkingLevelMap is { } map && map.TryGetValue(ToModelThinkingLevel(level), out var mapped))
        {
            return mapped ?? ToThinkingLevelWire(level);
        }

        return ToThinkingLevelWire(level);
    }

    // ---------------------------------------------------------------------
    // cache control（pi 1062–1176）
    // ---------------------------------------------------------------------

    private static JsonObject? GetCompatCacheControl(OpenAiCompletionsCompat compat, CacheRetention cacheRetention)
    {
        if (compat.CacheControlFormat != "anthropic" || cacheRetention == CacheRetention.None)
        {
            return null;
        }

        var cacheControl = new JsonObject { ["type"] = "ephemeral" };
        if (cacheRetention == CacheRetention.Long && compat.SupportsLongCacheRetention)
        {
            cacheControl["ttl"] = "1h";
        }

        return cacheControl;
    }

    private static void ApplyAnthropicCacheControl(JsonArray messages, JsonArray? tools, JsonObject cacheControl)
    {
        AddCacheControlToSystemPrompt(messages, cacheControl);
        AddCacheControlToLastTool(tools, cacheControl);
        AddCacheControlToLastConversationMessage(messages, cacheControl);
    }

    private static void AddCacheControlToSystemPrompt(JsonArray messages, JsonObject cacheControl)
    {
        foreach (var node in messages)
        {
            if (node is not JsonObject message) continue;
            var role = JsonStr(message, "role");
            if (role is not ("system" or "developer")) continue;
            AddCacheControlToTextContent(message, cacheControl);
            return;
        }
    }

    private static void AddCacheControlToLastConversationMessage(JsonArray messages, JsonObject cacheControl)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index] is not JsonObject message) continue;
            var role = JsonStr(message, "role");
            if (role is not ("user" or "assistant" or "tool")) continue;
            if (AddCacheControlToTextContent(message, cacheControl)) return;
        }
    }

    private static void AddCacheControlToLastTool(JsonArray? tools, JsonObject cacheControl)
    {
        if (tools is not { Count: > 0 }) return;
        if (tools[^1] is JsonObject lastTool)
        {
            lastTool["cache_control"] = cacheControl.DeepClone();
        }
    }

    private static bool AddCacheControlToTextContent(JsonObject message, JsonObject cacheControl)
    {
        var content = message["content"];
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (text.Length == 0) return false;
            message["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["cache_control"] = cacheControl.DeepClone(),
            });
            return true;
        }

        if (content is not JsonArray parts) return false;
        for (var index = parts.Count - 1; index >= 0; index--)
        {
            if (parts[index] is JsonObject part && JsonStr(part, "type") == "text")
            {
                part["cache_control"] = cacheControl.DeepClone();
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------------
    // convertMessages（pi 1178–1470）+ transform-messages.ts
    // ---------------------------------------------------------------------

    internal static JsonArray ConvertMessages(
        Model model,
        TranscriptContext context,
        OpenAiCompletionsCompat compat,
        IReadOnlyDictionary<string, string> grammarToolInputProperties)
    {
        var normalizedContext = ResolveTranscript(context, compat.SupportsMidConvoSystemMessages);
        var parameters = new JsonArray();
        var transformedMessages = TransformMessages(
            normalizedContext.Messages, model, id => NormalizeToolCallId(model, id));
        var transcriptTools = ResolveTranscriptTools(
            normalizedContext.Messages,
            compat.SupportsMidConvoSystemMessages && compat.SupportsMidConvoToolAdditions
                ? TranscriptToolSupport.Additions
                : TranscriptToolSupport.None);
        var instructionRole = model.Reasoning && compat.SupportsDeveloperRole ? "developer" : "system";

        string? lastRole = null;
        var systemIndex = 0;

        for (var i = 0; i < transformedMessages.Count; i++)
        {
            var msg = transformedMessages[i];

            // 部分 provider 不允许 toolResult 后直接跟 user：插入合成 assistant 桥接。
            if (compat.RequiresAssistantAfterToolResult && lastRole == "toolResult" && msg is UserMessage)
            {
                parameters.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "I have processed the tool results.",
                });
            }

            switch (msg)
            {
                case SystemMessage systemMessage:
                {
                    // transformMessages 保持系统消息顺序，序号与 transcript 一致。
                    var addedTools = systemIndex++ >= transcriptTools.FoldedSystemMessages
                        ? systemMessage.ToolsAdded ?? Array.Empty<Tool>()
                        : Array.Empty<Tool>();
                    if (addedTools.Count > 0)
                    {
                        parameters.Add(new JsonObject
                        {
                            ["role"] = "system",
                            ["tools"] = ConvertTools(addedTools, compat),
                        });
                    }

                    var text = i == 0
                        ? GetSystemMessageText(systemMessage)
                        : RenderSystemMessageUpdate(systemMessage);
                    if (text.Length > 0)
                    {
                        parameters.Add(new JsonObject
                        {
                            ["role"] = instructionRole,
                            ["content"] = SanitizeSurrogates(text),
                        });
                    }

                    lastRole = "system";
                    continue;
                }

                case UserMessage userMessage:
                {
                    if (userMessage.Content is MessageContent.Text textContent)
                    {
                        parameters.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = SanitizeSurrogates(textContent.Value),
                        });
                        lastRole = "user";
                        continue;
                    }

                    var content = new JsonArray();
                    foreach (var item in userMessage.Content.ToBlocks())
                    {
                        switch (item)
                        {
                            case TextContent text when text.Text.Length > 0:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = SanitizeSurrogates(text.Text),
                                });
                                break;
                            case ImageContent image:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject
                                    {
                                        ["url"] = $"data:{image.MimeType};base64,{image.Data}",
                                    },
                                });
                                break;
                        }
                    }

                    if (content.Count == 0) continue;
                    parameters.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = content,
                    });
                    lastRole = "user";
                    continue;
                }

                case AssistantMessage assistant:
                {
                    var assistantMessage = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = compat.RequiresAssistantAfterToolResult ? "" : null,
                    };

                    var assistantTextParts = new List<JsonObject>();
                    foreach (var block in assistant.Content.OfType<TextContent>())
                    {
                        if (block.Text.Trim().Length == 0) continue;
                        assistantTextParts.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = SanitizeSurrogates(block.Text),
                        });
                    }

                    var assistantText = string.Concat(assistantTextParts.Select(part => JsonStr(part, "text") ?? ""));
                    var thinkingBlocks = assistant.Content.OfType<ThinkingContent>().ToList();
                    var toolCalls = assistant.Content.OfType<ToolCall>().ToList();

                    List<JsonObject>? signedReasoningDetails = null;
                    foreach (var block in thinkingBlocks)
                    {
                        signedReasoningDetails = ParseOpenAIReasoningDetails(block.ThinkingSignature);
                        if (signedReasoningDetails is not null) break;
                    }

                    var legacyReasoningDetails = toolCalls
                        .Select(call => ParseLegacyEncryptedReasoningDetail(call.ThoughtSignature))
                        .Where(detail => detail is not null)
                        .Select(detail => detail!)
                        .ToList();
                    var preservedReasoningDetails = signedReasoningDetails
                        ?? (legacyReasoningDetails.Count > 0 ? legacyReasoningDetails : null);

                    var nonEmptyThinking = thinkingBlocks
                        .Where(block => block.Thinking.Trim().Length > 0)
                        .ToList();
                    if (nonEmptyThinking.Count > 0)
                    {
                        if (compat.RequiresThinkingAsText)
                        {
                            // thinking 块转纯文本（无标签，避免模型模仿）。
                            var thinkingText = SanitizeSurrogates(
                                string.Join("\n\n", nonEmptyThinking.Select(block => block.Thinking)));
                            var content = new JsonArray(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = thinkingText,
                            });
                            foreach (var part in assistantTextParts) content.Add(part);
                            assistantMessage["content"] = content;
                        }
                        else
                        {
                            // assistant content 恒为纯字符串（OpenAI 标准格式）；数组形式是非标准的，
                            // 会让部分模型（DeepSeek V3.2 via NVIDIA NIM）逐字镜像内容块结构。
                            if (assistantText.Length > 0)
                            {
                                assistantMessage["content"] = assistantText;
                            }

                            if (preservedReasoningDetails is null)
                            {
                                // 首个 thinking 块的 signature（llama.cpp + gpt-oss）。
                                var signature = nonEmptyThinking[0].ThinkingSignature;
                                if (model.Provider == "opencode-go" && signature == "reasoning")
                                {
                                    signature = "reasoning_content";
                                }

                                if (signature is not null && ReasoningFields.Contains(signature, StringComparer.Ordinal))
                                {
                                    assistantMessage[signature] = string.Join(
                                        "\n", nonEmptyThinking.Select(block => block.Thinking));
                                }
                            }
                        }
                    }
                    else if (assistantText.Length > 0)
                    {
                        assistantMessage["content"] = assistantText;
                    }

                    if (toolCalls.Count > 0)
                    {
                        var toolCallArray = new JsonArray();
                        foreach (var toolCall in toolCalls)
                        {
                            var customInputProperty = grammarToolInputProperties.TryGetValue(
                                toolCall.Name, out var property)
                                ? property
                                : null;
                            if (customInputProperty is not null)
                            {
                                toolCallArray.Add(new JsonObject
                                {
                                    ["id"] = toolCall.Id,
                                    ["type"] = "custom",
                                    ["custom"] = new JsonObject
                                    {
                                        ["name"] = toolCall.Name,
                                        ["input"] = SanitizeSurrogates(
                                            GetGrammarToolInput(toolCall.Name, toolCall.Arguments, customInputProperty)),
                                    },
                                });
                            }
                            else
                            {
                                toolCallArray.Add(new JsonObject
                                {
                                    ["id"] = toolCall.Id,
                                    ["type"] = "function",
                                    ["function"] = new JsonObject
                                    {
                                        ["name"] = toolCall.Name,
                                        ["arguments"] = toolCall.Arguments.ToJsonString(),
                                    },
                                });
                            }
                        }

                        assistantMessage["tool_calls"] = toolCallArray;
                    }

                    if (preservedReasoningDetails is not null)
                    {
                        assistantMessage["reasoning_details"] = new JsonArray(
                            preservedReasoningDetails.Select(detail => (JsonNode)detail.DeepClone()).ToArray());
                    }

                    if (compat.RequiresReasoningContentOnAssistantMessages
                        && model.Reasoning
                        && !assistantMessage.ContainsKey("reasoning_content"))
                    {
                        assistantMessage["reasoning_content"] = "";
                    }

                    var contentNode = assistantMessage["content"];
                    var hasContent = contentNode switch
                    {
                        JsonValue value when value.TryGetValue<string>(out var text) => text.Length > 0,
                        JsonArray array => array.Count > 0,
                        _ => false,
                    };
                    if (!hasContent && assistantMessage["tool_calls"] is not JsonArray { Count: > 0 })
                    {
                        continue;
                    }

                    parameters.Add(assistantMessage);
                    lastRole = "assistant";
                    continue;
                }

                case ToolResultMessage:
                {
                    var imageBlocks = new JsonArray();
                    var j = i;
                    for (; j < transformedMessages.Count && transformedMessages[j] is ToolResultMessage; j++)
                    {
                        var toolMessage = (ToolResultMessage)transformedMessages[j];
                        var textResult = string.Join(
                            "\n", toolMessage.Content.OfType<TextContent>().Select(block => block.Text));
                        var hasImages = toolMessage.Content.Any(block => block is ImageContent);

                        // 有图无文给占位；空输出给占位（部分 provider 要求非空）。
                        var hasText = textResult.Trim().Length > 0;
                        var toolResultText = hasText
                            ? textResult
                            : hasImages
                                ? "(see attached image)"
                                : "(no tool output)";
                        var toolResultMessage = new JsonObject
                        {
                            ["role"] = "tool",
                            ["content"] = SanitizeSurrogates(toolResultText),
                            ["tool_call_id"] = toolMessage.ToolCallId,
                        };
                        if (compat.RequiresToolResultName && toolMessage.ToolName.Length > 0)
                        {
                            toolResultMessage["name"] = toolMessage.ToolName;
                        }

                        parameters.Add(toolResultMessage);

                        if (hasImages && model.Input.Contains(ModelInputModality.Image))
                        {
                            foreach (var image in toolMessage.Content.OfType<ImageContent>())
                            {
                                imageBlocks.Add(new JsonObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject
                                    {
                                        ["url"] = $"data:{image.MimeType};base64,{image.Data}",
                                    },
                                });
                            }
                        }
                    }

                    i = j - 1;

                    if (imageBlocks.Count > 0)
                    {
                        if (compat.RequiresAssistantAfterToolResult)
                        {
                            parameters.Add(new JsonObject
                            {
                                ["role"] = "assistant",
                                ["content"] = "I have processed the tool results.",
                            });
                        }

                        var content = new JsonArray(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Attached image(s) from tool result:",
                        });
                        foreach (var image in imageBlocks) content.Add(image!.DeepClone()!);
                        parameters.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = content,
                        });
                        lastRole = "user";
                    }
                    else
                    {
                        lastRole = "toolResult";
                    }

                    continue;
                }

                default:
                    lastRole = msg.Role;
                    continue;
            }
        }

        return parameters;
    }

    private static string NormalizeToolCallId(Model model, string id)
    {
        if (id.Contains('|'))
        {
            // OpenAI Responses 的 {call_id}|{item_id} 形状：清洗成合法字符并截到 40 字符；
            // item 级唯一性保留（同 turn 多 tool call 可能共享 call_id）。
            var separatorIndex = id.IndexOf('|');
            var callId = SanitizeIdPart(id[..separatorIndex]);
            var itemId = SanitizeIdPart(id[(separatorIndex + 1)..]);
            var combinedId = itemId.Length > 0 ? $"{callId}_{itemId}" : callId;
            if (combinedId.Length <= 40) return combinedId;

            var hash = ShortHash(id);
            if (hash.Length > 8) hash = hash[..8];
            var prefixLength = Math.Max(1, 40 - hash.Length - 1);
            var prefix = callId.Length > prefixLength ? callId[..prefixLength] : callId;
            return $"{prefix}_{hash}";
        }

        if (model.Provider == "openai" && id.Length > 40) return id[..40];
        return id;
    }

    private static string SanitizeIdPart(string value)
        => new(value.Select(ch =>
            (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || char.IsDigit(ch) || ch is '_' or '-'
                ? ch
                : '_').ToArray());

    /// <summary>pi utils/hash.ts shortHash：32 位确定性散列，base36 拼接。</summary>
    private static string ShortHash(string value)
    {
        uint h1 = 0xdeadbeef;
        uint h2 = 0x41c6ce57;
        foreach (var ch in value)
        {
            var code = (uint)ch;
            h1 = Imul(h1 ^ code, 2654435761);
            h2 = Imul(h2 ^ code, 1597334677);
        }

        h1 = Imul(h1 ^ (h1 >> 16), 2246822507) ^ Imul(h2 ^ (h2 >> 13), 3266489909);
        h2 = Imul(h2 ^ (h2 >> 16), 2246822507) ^ Imul(h1 ^ (h1 >> 13), 3266489909);
        return ToBase36(h2) + ToBase36(h1);
    }

    private static uint Imul(uint a, uint b)
    {
        // Math.imul：32 位截断乘法。
        var product = (ulong)a * b;
        return unchecked((uint)product);
    }

    private static string ToBase36(uint value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0) return "0";
        var buffer = new char[13];
        var index = buffer.Length;
        while (value > 0)
        {
            buffer[--index] = digits[(int)(value % 36)];
            value /= 36;
        }

        return new string(buffer, index, buffer.Length - index);
    }

    /// <summary>
    /// pi transform-messages.ts：不支持的图片降级占位、跨模型 thinking/签名剥离、
    /// 工具调用 ID 归一化映射、孤立 tool call 补合成结果、errored/aborted assistant 跳过、
    /// tool call 与结果之间的 system 消息后置。
    /// </summary>
    private static IReadOnlyList<Message> TransformMessages(
        IReadOnlyList<Message> messages,
        Model model,
        Func<string, string> normalizeToolCallId)
    {
        var toolCallIdMap = new Dictionary<string, string>();
        var imageAwareMessages = DowngradeUnsupportedImages(messages, model);
        var isVisionModel = model.Input.Contains(ModelInputModality.Image);

        var transformed = new List<Message>(imageAwareMessages.Count);
        foreach (var msg in imageAwareMessages)
        {
            switch (msg)
            {
                case SystemMessage or UserMessage:
                    transformed.Add(msg);
                    break;

                case ToolResultMessage toolResult:
                {
                    if (toolCallIdMap.TryGetValue(toolResult.ToolCallId, out var normalizedId)
                        && normalizedId != toolResult.ToolCallId)
                    {
                        transformed.Add(toolResult with { ToolCallId = normalizedId });
                    }
                    else
                    {
                        transformed.Add(toolResult);
                    }

                    break;
                }

                case AssistantMessage assistant:
                {
                    var isSameModel = assistant.Provider == model.Provider
                        && assistant.Api == model.Api
                        && assistant.Model == model.Id;
                    var content = new List<ContentBlock>();
                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case ThinkingContent thinking:
                                // 其他模型的 thinking 丢弃：签名/加密 reasoning 只对产出模型有效。
                                if (!isSameModel) continue;
                                if (thinking.Redacted == true || thinking.ThinkingSignature is not null) content.Add(thinking);
                                else if (!string.IsNullOrWhiteSpace(thinking.Thinking)) content.Add(thinking);
                                break;

                            case TextContent text:
                                content.Add(isSameModel ? text : new TextContent { Text = text.Text });
                                break;

                            case ToolCall toolCall:
                            {
                                var normalizedToolCall = toolCall;
                                if (!isSameModel && toolCall.ThoughtSignature is not null)
                                {
                                    normalizedToolCall = toolCall with { ThoughtSignature = null };
                                }

                                if (!isSameModel)
                                {
                                    var normalizedCallId = normalizeToolCallId(toolCall.Id);
                                    if (normalizedCallId != toolCall.Id)
                                    {
                                        toolCallIdMap[toolCall.Id] = normalizedCallId;
                                        normalizedToolCall = normalizedToolCall with { Id = normalizedCallId };
                                    }
                                }

                                content.Add(normalizedToolCall);
                                break;
                            }

                            default:
                                content.Add(block);
                                break;
                        }
                    }

                    transformed.Add(assistant with { Content = content });
                    break;
                }

                default:
                    transformed.Add(msg);
                    break;
            }
        }

        // 第二遍：为孤立 tool call 补合成结果；errored/aborted assistant 整条跳过。
        var result = new List<Message>(transformed.Count);
        List<ToolCall> pendingToolCalls = new();
        HashSet<string> existingToolResultIds = new();
        List<Message> heldSystemMessages = new();

        void ClosePendingToolCalls()
        {
            if (pendingToolCalls.Count > 0)
            {
                foreach (var toolCall in pendingToolCalls)
                {
                    if (!existingToolResultIds.Contains(toolCall.Id))
                    {
                        result.Add(new ToolResultMessage
                        {
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            Content = new ContentBlock[] { new TextContent { Text = "No result provided" } },
                            IsError = true,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        });
                    }
                }

                pendingToolCalls = new List<ToolCall>();
                existingToolResultIds = new HashSet<string>();
            }

            result.AddRange(heldSystemMessages);
            heldSystemMessages.Clear();
        }

        foreach (var msg in transformed)
        {
            switch (msg)
            {
                case AssistantMessage assistant:
                    ClosePendingToolCalls();
                    if (assistant.StopReason is StopReason.Error or StopReason.Aborted)
                    {
                        continue;
                    }

                    var toolCalls = assistant.Content.OfType<ToolCall>().ToList();
                    if (toolCalls.Count > 0)
                    {
                        pendingToolCalls = toolCalls;
                        existingToolResultIds = new HashSet<string>();
                    }

                    result.Add(assistant);
                    break;

                case ToolResultMessage toolResult:
                    existingToolResultIds.Add(toolResult.ToolCallId);
                    result.Add(toolResult);
                    break;

                case SystemMessage systemMessage:
                    if (pendingToolCalls.Count > 0) heldSystemMessages.Add(systemMessage);
                    else result.Add(systemMessage);
                    break;

                case UserMessage:
                    // 新的 user turn 打断 tool 流程：先补孤立调用的合成结果。
                    ClosePendingToolCalls();
                    result.Add(msg);
                    break;

                default:
                    result.Add(msg);
                    break;
            }
        }

        ClosePendingToolCalls();
        return result;
    }

    private static IReadOnlyList<Message> DowngradeUnsupportedImages(IReadOnlyList<Message> messages, Model model)
    {
        if (model.Input.Contains(ModelInputModality.Image)) return messages;
        return messages.Select(msg => msg switch
        {
            UserMessage user => user with
            {
                Content = ReplaceImagesWithPlaceholder(
                    user.Content, "(image omitted: model does not support images)"),
            },
            ToolResultMessage tool => tool with
            {
                Content = ReplaceImagesWithPlaceholder(
                    MessageContent.From(tool.Content), "(tool image omitted: model does not support images)")
                    .ToBlocks(),
            },
            _ => msg,
        }).ToList();
    }

    private static MessageContent ReplaceImagesWithPlaceholder(MessageContent content, string placeholder)
    {
        var blocks = content.ToBlocks();
        if (!blocks.Any(block => block is ImageContent)) return content;

        var result = new List<ContentBlock>();
        var previousWasPlaceholder = false;
        foreach (var block in blocks)
        {
            if (block is ImageContent)
            {
                if (!previousWasPlaceholder)
                {
                    result.Add(new TextContent { Text = placeholder });
                }

                previousWasPlaceholder = true;
                continue;
            }

            result.Add(block);
            previousWasPlaceholder = block is TextContent text && text.Text == placeholder;
        }

        return MessageContent.From(result);
    }

    private static string GetSystemMessageText(SystemMessage message)
    {
        var parts = new List<string> { ContentText(message.Content) };
        if (message.Sections is not null)
        {
            foreach (var text in message.Sections.Values)
            {
                if (text is not null) parts.Add(text);
            }
        }

        return string.Join("\n\n", parts.Where(part => part.Length > 0));
    }

    private static string RenderSystemMessageUpdate(SystemMessage message)
    {
        var parts = new List<string>();
        var text = ContentText(message.Content);
        if (text.Length > 0) parts.Add(text);
        if (message.Sections is not null)
        {
            foreach (var entry in message.Sections)
            {
                parts.Add(entry.Value is null
                    ? $"Removed system prompt section \"{entry.Key}\"."
                    : $"Updated system prompt section \"{entry.Key}\":\n\n{entry.Value}");
            }
        }

        return string.Join("\n\n", parts);
    }

    private static string ContentText(MessageContent content)
    {
        if (content is MessageContent.Text text) return text.Value;
        return string.Join("\n", content.ToBlocks().OfType<TextContent>().Select(block => block.Text));
    }

    private static string SanitizeSurrogates(string text) => UnpairedSurrogateRegex.Replace(text, string.Empty);

    // ---------------------------------------------------------------------
    // convertTools（pi 1471–1507）+ constrained-sampling.ts
    // ---------------------------------------------------------------------

    internal static JsonArray ConvertTools(IReadOnlyList<Tool> tools, OpenAiCompletionsCompat compat)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            if (ResolveGrammarConstrainedSampling(tool, compat.SupportsOpenAIGrammarTools) is { } grammar)
            {
                result.Add(new JsonObject
                {
                    ["type"] = "custom",
                    ["custom"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["format"] = new JsonObject
                        {
                            ["type"] = "grammar",
                            ["grammar"] = new JsonObject
                            {
                                ["syntax"] = grammar.Format == GrammarFormatLark ? "lark" : "regex",
                                ["definition"] = grammar.Definition,
                            },
                        },
                    },
                });
                continue;
            }

            var strict = ResolveJsonSchemaStrictSampling(tool, compat.SupportsStrictMode);
            var function = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = GetJsonSchemaToolParameters(tool, strict),
            };
            if (compat.SupportsStrictMode)
            {
                // strict 字段仅在 provider 支持时携带；部分 provider 拒绝未知字段。
                function["strict"] = strict ?? false;
            }

            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = function,
            });
        }

        return result;
    }

    private readonly record struct GrammarConstrainedSampling(char Format, string Definition, string InputProperty);

    private static GrammarConstrainedSampling? ResolveGrammarConstrainedSampling(
        Tool tool, bool supportsOpenAIGrammarTools)
    {
        if (tool.ConstrainedSampling is not ConstrainedSampling.Config
            {
                Value: ConstrainedSamplingConfig.Grammar grammar,
            })
        {
            return null;
        }

        if (!supportsOpenAIGrammarTools)
        {
            return null;
        }

        var hasLark = grammar.Variants.TryGetValue(GrammarFormat.OpenAiLark, out var lark)
            && !string.IsNullOrWhiteSpace(lark);
        var hasRegex = grammar.Variants.TryGetValue(GrammarFormat.OpenAiRegex, out var regex)
            && !string.IsNullOrWhiteSpace(regex);
        if (!hasLark && !hasRegex)
        {
            throw new InvalidOperationException(
                $"Tool \"{tool.Name}\" cannot use grammar constrained sampling: no supported grammar variant was provided.");
        }

        try
        {
            return new GrammarConstrainedSampling(
                hasLark ? GrammarFormatLark : GrammarFormatRegex,
                hasLark ? lark : regex!,
                InferGrammarInputProperty(tool.Name, tool.Parameters));
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException(
                $"Tool \"{tool.Name}\" cannot use grammar constrained sampling: {error.Message}.");
        }
    }

    private static string InferGrammarInputProperty(string toolName, JsonObject schema)
    {
        if (JsonStr(schema, "type") != "object")
        {
            throw new InvalidOperationException("grammar constrained sampling requires an object parameter schema");
        }

        if (schema["required"] is not JsonArray required || required.Count != 1
            || required[0] is not JsonValue single || !single.TryGetValue<string>(out var inputProperty))
        {
            throw new InvalidOperationException(
                "grammar constrained sampling requires exactly one required string property");
        }

        if (JsonObj(schema, "properties") is not { } properties
            || JsonObj(properties, inputProperty) is not { } propertySchema)
        {
            throw new InvalidOperationException(
                $"grammar constrained sampling requires a properties entry for {inputProperty}");
        }

        if (JsonStr(propertySchema, "type") != "string")
        {
            throw new InvalidOperationException(
                $"grammar constrained sampling property {inputProperty} must have type string");
        }

        return inputProperty;
    }

    /// <summary>pi resolveJsonSchemaStrictSampling：strict 档位决策；不可编译的 require 抛错。</summary>
    private static bool? ResolveJsonSchemaStrictSampling(Tool tool, bool supportsStrictMode)
    {
        if (tool.ConstrainedSampling is not ConstrainedSampling.Config
            {
                Value: ConstrainedSamplingConfig.JsonSchema config,
            })
        {
            return null;
        }

        if (supportsStrictMode)
        {
            try
            {
                MakeStrictJsonSchema(tool.Parameters);
                return true;
            }
            catch (InvalidOperationException error) when (IsUnsupportedStrictSchemaError(error))
            {
                if (config.Strict != StrictMode.Require) return null;
                throw new InvalidOperationException(
                    $"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but {error.Message}.");
            }
        }

        if (config.Strict == StrictMode.Require)
        {
            throw new InvalidOperationException(
                $"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but strict tools are unsupported.");
        }

        return null;
    }

    private static JsonObject GetJsonSchemaToolParameters(Tool tool, bool? strict)
        => strict == true ? MakeStrictJsonSchema(tool.Parameters) : tool.Parameters;

    private sealed class UnsupportedStrictJsonSchemaException : InvalidOperationException
    {
        public UnsupportedStrictJsonSchemaException(string message)
            : base(message)
        {
        }
    }

    private static bool IsUnsupportedStrictSchemaError(Exception error)
        => error is UnsupportedStrictJsonSchemaException;

    /// <summary>pi makeStrictJsonSchema：把工具 schema 编译成 provider strict 子集。</summary>
    private static JsonObject MakeStrictJsonSchema(JsonObject schema)
    {
        var cloned = (JsonObject?)schema.DeepClone()
            ?? throw new UnsupportedStrictJsonSchemaException("root schema must have type object");
        MakeJsonSchemaNodeStrict(cloned);
        if (JsonStr(cloned, "type") != "object")
        {
            throw new UnsupportedStrictJsonSchemaException("root schema must have type object");
        }

        return cloned;
    }

    private static void MakeJsonSchemaNodeStrict(JsonObject schema)
    {
        foreach (var key in UnsupportedStrictSchemaKeys)
        {
            if (schema.ContainsKey(key))
            {
                throw new UnsupportedStrictJsonSchemaException($"{key} schemas are unsupported");
            }
        }

        if (schema["anyOf"] is JsonArray anyOf)
        {
            if (anyOf.Count == 0)
            {
                throw new UnsupportedStrictJsonSchemaException("anyOf must contain at least one schema");
            }

            foreach (var variant in anyOf)
            {
                if (variant is JsonObject variantObject && IsStructuredSchema(variantObject))
                {
                    throw new UnsupportedStrictJsonSchemaException("object and array unions are unsupported");
                }

                if (variant is not JsonObject variantNode)
                {
                    throw new UnsupportedStrictJsonSchemaException("boolean schemas are unsupported");
                }

                MakeJsonSchemaNodeStrict(variantNode);
            }
        }

        if (schema["items"] is JsonArray)
        {
            throw new UnsupportedStrictJsonSchemaException("tuple schemas are unsupported");
        }

        if (schema["items"] is JsonObject itemsNode)
        {
            MakeJsonSchemaNodeStrict(itemsNode);
        }

        var isObjectSchema = JsonStr(schema, "type") == "object";
        if (schema.ContainsKey("properties") && !isObjectSchema)
        {
            throw new UnsupportedStrictJsonSchemaException("properties require type object");
        }

        if (!isObjectSchema) return;
        if (schema.ContainsKey("additionalProperties")
            && schema["additionalProperties"] is JsonValue additional
            && (!additional.TryGetValue<bool>(out var additionalFlag) || additionalFlag))
        {
            throw new UnsupportedStrictJsonSchemaException(
                "schema-valued or true additionalProperties is unsupported");
        }

        if (schema.ContainsKey("properties")
            && schema["properties"] is not JsonObject)
        {
            throw new UnsupportedStrictJsonSchemaException("object properties must be a schema map");
        }

        if (schema["required"] is not null)
        {
            if (schema["required"] is not JsonArray required
                || required.Any(key => key is not JsonValue keyNode || !keyNode.TryGetValue<string>(out _)))
            {
                throw new UnsupportedStrictJsonSchemaException("object required must be a string array");
            }
        }

        var properties = schema.TryGetPropertyValue("properties", out var propertiesNode) && propertiesNode is JsonObject propertyMap
            ? propertyMap
            : new JsonObject();
        var propertyNames = properties.Select(entry => entry.Key).ToList();
        var requiredSet = new HashSet<string>(
            schema["required"] is JsonArray requiredArray
                ? requiredArray.Select(key => (key as JsonValue)?.GetValue<string>() ?? "")
                : Array.Empty<string>());
        if (requiredSet.Any(key => !propertyNames.Contains(key)))
        {
            throw new UnsupportedStrictJsonSchemaException("required contains an unknown property");
        }

        foreach (var entry in properties.ToList())
        {
            if (entry.Value is not JsonObject property) continue;
            MakeJsonSchemaNodeStrict(property);
            if (!requiredSet.Contains(entry.Key) && !SchemaAllowsNull(property))
            {
                properties[entry.Key] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(property.DeepClone(), new JsonObject { ["type"] = "null" }),
                };
            }
        }

        schema["required"] = new JsonArray(propertyNames.Select(name => (JsonNode)name).ToArray());
        schema["additionalProperties"] = false;
    }

    private static bool IsStructuredSchema(JsonObject schema)
    {
        var type = JsonStr(schema, "type");
        var types = type is not null
            ? new[] { type }
            : schema["type"] is JsonArray typeArray
                ? typeArray.Select(node => (node as JsonValue)?.GetValue<string>()).Where(value => value is not null).Select(value => value!).ToArray()
                : Array.Empty<string>();
        return types.Contains("object")
            || types.Contains("array")
            || schema.ContainsKey("properties")
            || schema.ContainsKey("items");
    }

    private static bool SchemaAllowsNull(JsonObject schema)
    {
        if (JsonStr(schema, "type") == "null") return true;
        if (schema["type"] is JsonArray typeArray
            && typeArray.Any(node => (node as JsonValue)?.GetValue<string>() == "null"))
        {
            return true;
        }

        // JSON 字面 null：TryGetPropertyValue 为 true 但节点为 null（STJ 里 null JSON 值不装箱）。
        if (schema.TryGetPropertyValue("const", out var constNode) && constNode is null) return true;
        if (schema["enum"] is JsonArray enumArray
            && enumArray.Any(node => node is null))
        {
            return true;
        }

        return schema["anyOf"] is JsonArray anyOf
            && anyOf.Any(node => node is JsonObject variant && SchemaAllowsNull(variant));
    }

    // ---------------------------------------------------------------------
    // grammar 工具输入 JSON 缓冲（pi constrained-sampling.ts 173–236）
    // ---------------------------------------------------------------------

    private sealed class GrammarToolInputJsonBuffer
    {
        public string Input { get; set; } = "";
        public bool Started { get; set; }
        public bool Closed { get; set; }
    }

    private static string GetGrammarToolInput(string toolName, JsonObject arguments, string inputProperty)
    {
        if (JsonStr(arguments, inputProperty) is not { } input)
        {
            throw new InvalidOperationException(
                $"Grammar tool call \"{toolName}\" requires argument \"{inputProperty}\" to be a string.");
        }

        return input;
    }

    private static string? AppendGrammarToolInputJsonDelta(
        GrammarToolInputJsonBuffer buffer, string inputProperty, string inputDelta)
    {
        if (buffer.Closed)
        {
            throw new InvalidOperationException(
                $"grammar tool input for property \"{inputProperty}\" changed after it was closed");
        }

        if (inputDelta.Length == 0) return null;
        buffer.Input += inputDelta;
        return GrammarToolInputJsonStart(buffer, inputProperty) + JsonSerializer.Serialize(inputDelta)[1..^1];
    }

    private static string? CloseGrammarToolInputJson(
        GrammarToolInputJsonBuffer buffer, string inputProperty, string? finalInput)
    {
        if (buffer.Closed)
        {
            if (finalInput is null || finalInput == buffer.Input) return null;
            throw new InvalidOperationException(
                $"grammar tool input for property \"{inputProperty}\" changed after it was closed");
        }

        if (finalInput is not null && !finalInput.StartsWith(buffer.Input, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"grammar tool input for property \"{inputProperty}\" changed non-monotonically");
        }

        var rest = finalInput is null ? "" : finalInput[buffer.Input.Length..];
        if (finalInput is not null) buffer.Input = finalInput;
        buffer.Closed = true;
        return $"{GrammarToolInputJsonStart(buffer, inputProperty)}{JsonSerializer.Serialize(rest)[1..^1]}}}\"";
    }

    private static string GrammarToolInputJsonStart(GrammarToolInputJsonBuffer buffer, string inputProperty)
    {
        if (buffer.Started) return "";
        buffer.Started = true;
        return $"{{{JsonSerializer.Serialize(inputProperty)}:\"";
    }

    private static IReadOnlyDictionary<string, string> CreateGrammarToolInputProperties(
        IReadOnlyList<Tool> tools, bool supportsOpenAIGrammarTools)
    {
        var properties = new Dictionary<string, string>();
        foreach (var tool in tools)
        {
            if (ResolveGrammarConstrainedSampling(tool, supportsOpenAIGrammarTools) is { } grammar)
            {
                properties[tool.Name] = grammar.InputProperty;
            }
        }

        return properties;
    }

    // ---------------------------------------------------------------------
    // reasoning_details（pi 129–277）
    // ---------------------------------------------------------------------

    private static bool IsOpenAIReasoningDetail(JsonObject detail)
    {
        if (detail.TryGetPropertyValue("id", out var id)
            && id is not null && id is not JsonValue)
        {
            return false;
        }

        if (id is JsonValue idValue && !idValue.TryGetValue<string>(out _))
        {
            return false;
        }

        if (detail.TryGetPropertyValue("format", out var format)
            && format is not null
            && (format is not JsonValue formatValue || !formatValue.TryGetValue<string>(out _)))
        {
            return false;
        }

        if (detail.TryGetPropertyValue("index", out var index)
            && index is not null
            && !(index is JsonValue indexValue && indexValue.TryGetValue<double>(out _)))
        {
            return false;
        }

        return JsonStr(detail, "type") switch
        {
            "reasoning.summary" => JsonStr(detail, "summary") is not null,
            "reasoning.encrypted" => JsonStr(detail, "data") is not null,
            "reasoning.text" => JsonStr(detail, "text") is not null
                && (detail["signature"] is null || JsonStr(detail, "signature") is not null),
            _ => false,
        };
    }

    private static List<JsonObject>? ParseOpenAIReasoningDetails(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        try
        {
            if (JsonNode.Parse(signature) is not JsonArray array || array.Count == 0) return null;
            var details = new List<JsonObject>();
            foreach (var entry in array)
            {
                if (entry is not JsonObject detail || !IsOpenAIReasoningDetail(detail)) return null;
                details.Add((JsonObject)detail.DeepClone());
            }

            return details;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? ParseLegacyEncryptedReasoningDetail(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        try
        {
            if (JsonNode.Parse(signature) is not JsonObject detail || !IsOpenAIReasoningDetail(detail)) return null;
            if (JsonStr(detail, "type") != "reasoning.encrypted") return null;
            if (JsonStr(detail, "id") is not { Length: > 0 }) return null;
            if (JsonStr(detail, "data") is not { Length: > 0 }) return null;
            return detail;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void FillMissingReasoningDetailFields(JsonObject target, JsonObject source)
    {
        if (target["id"] is null && source["id"] is { } id) target["id"] = id.DeepClone();
        if (string.IsNullOrEmpty(JsonStr(target, "format")) && JsonStr(source, "format") is { } format)
        {
            target["format"] = format;
        }

        if (target["index"] is null && source["index"] is { } index) target["index"] = index.DeepClone();
    }

    private static void AppendOpenAIReasoningDetail(List<JsonObject> details, JsonObject detail)
    {
        var last = details.Count > 0 ? details[^1] : null;
        var detailType = JsonStr(detail, "type");
        if (detailType == "reasoning.text" && last is not null && JsonStr(last, "type") == "reasoning.text")
        {
            last["text"] = (JsonStr(last, "text") ?? "") + (JsonStr(detail, "text") ?? "");
            if (string.IsNullOrEmpty(JsonStr(last, "signature")) && JsonStr(detail, "signature") is { } signature)
            {
                last["signature"] = signature;
            }

            FillMissingReasoningDetailFields(last, detail);
            return;
        }

        if (detailType == "reasoning.summary" && last is not null && JsonStr(last, "type") == "reasoning.summary")
        {
            last["summary"] = (JsonStr(last, "summary") ?? "") + (JsonStr(detail, "summary") ?? "");
            FillMissingReasoningDetailFields(last, detail);
            return;
        }

        details.Add((JsonObject)detail.DeepClone());
    }

    private static string SerializeReasoningDetails(List<JsonObject> details)
        => new JsonArray(details.Select(detail => (JsonNode)detail.DeepClone()).ToArray()).ToJsonString();

    // ---------------------------------------------------------------------
    // transcript 归一化（pi utils/transcript.ts 子集）
    // ---------------------------------------------------------------------

    private enum TranscriptToolSupport
    {
        None,
        Additions,
        Changes,
    }

    private sealed class TranscriptToolsResult
    {
        public IReadOnlyList<Tool> RequestTools { get; init; } = Array.Empty<Tool>();
        public int FoldedSystemMessages { get; init; }
    }

    /// <summary>pi resolveTranscript：接受中段 system 消息时原样；否则折叠成首条。</summary>
    private static TranscriptContext ResolveTranscript(TranscriptContext context, bool supportsMidConvoSystemMessages)
        => supportsMidConvoSystemMessages ? context : CollapseSystemMessages(context);

    private static TranscriptContext CollapseSystemMessages(TranscriptContext context)
    {
        var head = GetCurrentSystemMessage(context.Messages);
        var messages = context.Messages.Where(message => message.Role != "system").ToList();
        if (head is not null)
        {
            messages.Insert(0, head);
        }

        return new TranscriptContext { Messages = messages };
    }

    private static SystemMessage? GetCurrentSystemMessage(IReadOnlyList<Message> messages)
    {
        var content = new List<string>();
        var sections = new Dictionary<string, string?>();
        long? timestamp = null;
        foreach (var message in messages)
        {
            if (message is not SystemMessage systemMessage) continue;
            timestamp ??= systemMessage.Timestamp;
            var text = ContentText(systemMessage.Content);
            if (text.Length > 0) content.Add(text);
            if (systemMessage.Sections is not null)
            {
                foreach (var entry in systemMessage.Sections)
                {
                    if (entry.Value is null) sections.Remove(entry.Key);
                    else sections[entry.Key] = entry.Value;
                }
            }
        }

        var tools = GetCurrentTools(messages);
        if (timestamp is null && tools.Count == 0) return null;
        return new SystemMessage
        {
            Content = MessageContent.From(string.Join("\n\n", content)),
            Sections = sections.Count > 0 ? sections : null,
            ToolsAdded = tools.Count > 0 ? tools : null,
            Timestamp = timestamp ?? 0,
        };
    }

    private static IReadOnlyList<Tool> GetCurrentTools(IEnumerable<Message> messages)
    {
        var tools = new Dictionary<string, Tool>();
        foreach (var message in messages)
        {
            if (message is not SystemMessage systemMessage) continue;
            if (systemMessage.ToolsRemoved is not null)
            {
                foreach (var removed in systemMessage.ToolsRemoved)
                {
                    tools.Remove(removed.Name);
                }
            }

            if (systemMessage.ToolsAdded is not null)
            {
                foreach (var tool in systemMessage.ToolsAdded)
                {
                    tools[tool.Name] = tool;
                }
            }
        }

        return tools.Values.ToList();
    }

    private static IReadOnlyList<Tool> GetDeclaredTools(IReadOnlyList<Message> messages)
    {
        var definitions = new Dictionary<string, Tool>();
        foreach (var message in messages)
        {
            if (message is not SystemMessage systemMessage) continue;
            if (systemMessage.ToolsAdded is null) continue;
            foreach (var tool in systemMessage.ToolsAdded)
            {
                definitions[tool.Name] = tool;
            }
        }

        return definitions.Values.ToList();
    }

    private static TranscriptToolsResult ResolveTranscriptTools(
        IReadOnlyList<Message> messages, TranscriptToolSupport support)
    {
        var systemMessages = messages.OfType<SystemMessage>().ToList();
        if (support == TranscriptToolSupport.None)
        {
            return new TranscriptToolsResult
            {
                RequestTools = GetCurrentTools(systemMessages),
                FoldedSystemMessages = systemMessages.Count,
            };
        }

        if (support == TranscriptToolSupport.Changes)
        {
            return new TranscriptToolsResult { RequestTools = Array.Empty<Tool>(), FoldedSystemMessages = 0 };
        }

        var folded = Math.Max(
            messages.Count > 0 && messages[0] is SystemMessage ? 1 : 0,
            FindLastNonAdditiveToolChange(systemMessages) + 1);
        return new TranscriptToolsResult
        {
            RequestTools = GetCurrentTools(systemMessages.Take(folded).ToList()),
            FoldedSystemMessages = folded,
        };
    }

    private static int FindLastNonAdditiveToolChange(IReadOnlyList<SystemMessage> systemMessages)
    {
        // 在先声明过的名字再次出现也算重定义（Kimi K3 忽略移除后又重声明的工具）。
        var declared = new HashSet<string>();
        var last = -1;
        for (var index = 0; index < systemMessages.Count; index++)
        {
            var message = systemMessages[index];
            if ((message.ToolsRemoved?.Count ?? 0) > 0) last = index;
            if (message.ToolsAdded is not null)
            {
                foreach (var tool in message.ToolsAdded)
                {
                    if (declared.Contains(tool.Name)) last = index;
                    declared.Add(tool.Name);
                }
            }
        }

        return last;
    }

    private static bool HasToolHistory(IReadOnlyList<Message> messages)
        => messages.Any(message => message is ToolResultMessage
            || (message is AssistantMessage assistant
                && assistant.Content.Any(block => block is ToolCall)));

    // ---------------------------------------------------------------------
    // parseChunkUsage（pi 1508–1555）+ openrouter-cost.ts
    // ---------------------------------------------------------------------

    internal static Usage ParseChunkUsage(JsonObject rawUsage, Model model)
    {
        var promptTokens = (int)(JsonNum(rawUsage, "prompt_tokens") ?? 0);
        var details = JsonObj(rawUsage, "prompt_tokens_details");
        // cache-read 链：OpenAI/OpenRouter 用 prompt_tokens_details.cached_tokens，
        // DeepSeek 用 prompt_cache_hit_tokens，Kimi 用顶层 cached_tokens。
        var cacheReadTokens = (int)(JsonNum(details, "cached_tokens")
            ?? JsonNum(rawUsage, "prompt_cache_hit_tokens")
            ?? JsonNum(rawUsage, "cached_tokens")
            ?? 0);
        var cacheWriteTokens = (int)(JsonNum(details, "cache_write_tokens") ?? 0);
        var input = Math.Max(0, promptTokens - cacheReadTokens - cacheWriteTokens);
        var outputTokens = (int)(JsonNum(rawUsage, "completion_tokens") ?? 0);
        var reasoningTokens = (int)(JsonNum(JsonObj(rawUsage, "completion_tokens_details"), "reasoning_tokens") ?? 0);

        var usage = new Usage
        {
            Input = input,
            Output = outputTokens,
            CacheRead = cacheReadTokens,
            CacheWrite = cacheWriteTokens,
            Reasoning = reasoningTokens > 0 ? reasoningTokens : null,
            TotalTokens = input + outputTokens + cacheReadTokens + cacheWriteTokens,
        };
        usage = usage with { Cost = ModelOperations.CalculateCost(model, usage) };
        return IsOpenRouterModel(model) ? ApplyOpenRouterReportedCost(usage, rawUsage) : usage;
    }

    private static bool IsOpenRouterModel(Model model)
        => model.Provider == "openrouter" || model.BaseUrl.Contains("openrouter.ai", StringComparison.Ordinal);

    private static bool IsValidCost(double? value)
        => value is { } cost && double.IsFinite(cost) && cost >= 0;

    private static Usage ApplyOpenRouterReportedCost(Usage usage, JsonObject reported)
    {
        if (!IsValidCost(JsonNum(reported, "cost"))) return usage;
        var cost = JsonNum(reported, "cost")!.Value;

        var isByok = reported["is_byok"] is JsonValue byokValue && byokValue.TryGetValue<bool>(out var byok)
            ? byok
            : (bool?)null;
        if (isByok == false)
        {
            return usage with { Cost = usage.Cost with { Total = cost, TotalFromProvider = true } };
        }

        if (isByok == true)
        {
            if (JsonObj(reported, "cost_details") is not { } costDetails) return usage;
            double? upstreamCost = JsonNum(costDetails, "upstream_inference_cost");
            if (upstreamCost is null)
            {
                var promptCost = JsonNum(costDetails, "upstream_inference_prompt_cost");
                var completionsCost = JsonNum(costDetails, "upstream_inference_completions_cost");
                if (!IsValidCost(promptCost) || !IsValidCost(completionsCost)) return usage;
                upstreamCost = promptCost!.Value + completionsCost!.Value;
            }

            if (!IsValidCost(upstreamCost)) return usage;
            return usage with { Cost = usage.Cost with { Total = cost + upstreamCost.Value, TotalFromProvider = true } };
        }

        return usage;
    }

    // ---------------------------------------------------------------------
    // mapStopReason（pi 1556–1586）
    // ---------------------------------------------------------------------

    internal static (StopReason StopReason, string? ErrorMessage) MapStopReasonDetailed(string? reason)
    {
        if (reason is null) return (StopReason.Stop, null);
        return reason switch
        {
            "stop" or "end" => (StopReason.Stop, null),
            "length" => (StopReason.Length, null),
            "function_call" or "tool_calls" => (StopReason.ToolUse, null),
            "content_filter" => (StopReason.Error, "Provider finish_reason: content_filter"),
            "network_error" => (StopReason.Error, "Provider finish_reason: network_error"),
            _ => (StopReason.Error, $"Provider finish_reason: {reason}"),
        };
    }

    // ---------------------------------------------------------------------
    // getCompat（pi 1587–1727）
    // ---------------------------------------------------------------------

    private static OpenAiCompletionsCompat GetCompat(Model model)
    {
        var detected = DetectCompat(model);
        var compat = model.Compat;
        if (compat is null) return detected;

        return new OpenAiCompletionsCompat
        {
            SupportsStore = JsonBool(compat, "supportsStore") ?? detected.SupportsStore,
            SupportsDeveloperRole = JsonBool(compat, "supportsDeveloperRole") ?? detected.SupportsDeveloperRole,
            SupportsReasoningEffort = JsonBool(compat, "supportsReasoningEffort") ?? detected.SupportsReasoningEffort,
            SupportsUsageInStreaming = JsonBool(compat, "supportsUsageInStreaming") ?? detected.SupportsUsageInStreaming,
            SupportsFinishReason = JsonBool(compat, "supportsFinishReason") ?? detected.SupportsFinishReason,
            MaxTokensField = JsonStr(compat, "maxTokensField") ?? detected.MaxTokensField,
            RequiresToolResultName = JsonBool(compat, "requiresToolResultName") ?? detected.RequiresToolResultName,
            RequiresAssistantAfterToolResult = JsonBool(compat, "requiresAssistantAfterToolResult")
                ?? detected.RequiresAssistantAfterToolResult,
            RequiresThinkingAsText = JsonBool(compat, "requiresThinkingAsText") ?? detected.RequiresThinkingAsText,
            RequiresReasoningContentOnAssistantMessages =
                JsonBool(compat, "requiresReasoningContentOnAssistantMessages")
                ?? detected.RequiresReasoningContentOnAssistantMessages,
            ThinkingFormat = JsonStr(compat, "thinkingFormat") ?? detected.ThinkingFormat,
            ZaiToolStream = JsonBool(compat, "zaiToolStream") ?? detected.ZaiToolStream,
            SupportsThinkingTokenBudget = JsonBool(compat, "supportsThinkingTokenBudget")
                ?? detected.SupportsThinkingTokenBudget,
            ThinkingTokenBudgetField = JsonStr(compat, "thinkingTokenBudgetField")
                ?? detected.ThinkingTokenBudgetField,
            SupportsStrictMode = JsonBool(compat, "supportsStrictMode") ?? detected.SupportsStrictMode,
            SupportsOpenAIGrammarTools = JsonBool(compat, "supportsOpenAIGrammarTools")
                ?? detected.SupportsOpenAIGrammarTools,
            SupportsMidConvoSystemMessages = JsonBool(compat, "supportsMidConvoSystemMessages")
                ?? detected.SupportsMidConvoSystemMessages,
            SupportsMidConvoToolAdditions = JsonBool(compat, "supportsMidConvoToolAdditions")
                ?? detected.SupportsMidConvoToolAdditions,
            CacheControlFormat = JsonStr(compat, "cacheControlFormat") ?? detected.CacheControlFormat,
            SendSessionAffinityHeaders = JsonBool(compat, "sendSessionAffinityHeaders")
                ?? detected.SendSessionAffinityHeaders,
            SessionAffinityFormat = JsonStr(compat, "sessionAffinityFormat") ?? detected.SessionAffinityFormat,
            SupportsLongCacheRetention = JsonBool(compat, "supportsLongCacheRetention")
                ?? detected.SupportsLongCacheRetention,
            ChatTemplateKwargs = JsonObj(compat, "chatTemplateKwargs") ?? detected.ChatTemplateKwargs,
            ChatTemplateArgs = JsonObj(compat, "chatTemplateArgs") ?? detected.ChatTemplateArgs,
            VllmPriority = compat.TryGetPropertyValue("vllmPriority", out var priority) && priority is not null
                ? priority.DeepClone()
                : null,
        };
    }

    /// <summary>pi detectCompat：按 provider / baseUrl 自动探测兼容缺省值。</summary>
    private static OpenAiCompletionsCompat DetectCompat(Model model)
    {
        var provider = model.Provider;
        var baseUrl = model.BaseUrl;

        bool Has(string value) => baseUrl.Contains(value, StringComparison.Ordinal);

        var isZai = provider is "zai" or "zai-coding-cn"
            || Has("api.z.ai") || Has("open.bigmodel.cn");
        var isTogether = provider == "together" || Has("api.together.ai") || Has("api.together.xyz");
        var isMoonshot = provider is "moonshotai" or "moonshotai-cn" || Has("api.moonshot.");
        var isOpenRouter = provider == "openrouter" || Has("openrouter.ai");
        var isCloudflareWorkersAi = provider == "cloudflare-workers-ai" || Has("api.cloudflare.com");
        var isCloudflareAiGateway = provider == "cloudflare-ai-gateway" || Has("gateway.ai.cloudflare.com");
        var isNvidia = provider == "nvidia" || Has("integrate.api.nvidia.com");
        var isAntLing = provider == "ant-ling" || Has("api.ant-ling.com");
        var isCerebras = provider == "cerebras" || Has("cerebras.ai");
        var isDeepSeek = provider == "deepseek"
            || baseUrl.ToLowerInvariant().Contains("deepseek.com", StringComparison.Ordinal);

        var isNonStandard = isNvidia
            || isCerebras
            || provider == "xai"
            || Has("api.x.ai")
            || isTogether
            || Has("chutes.ai")
            || isDeepSeek
            || isZai
            || isMoonshot
            || provider == "opencode"
            || Has("opencode.ai")
            || isCloudflareWorkersAi
            || isCloudflareAiGateway
            || isAntLing;

        var useMaxTokens = Has("chutes.ai")
            || isDeepSeek
            || isMoonshot
            || isCloudflareAiGateway
            || isTogether
            || isNvidia
            || isAntLing
            || isZai;

        var isGrok = provider == "xai" || Has("api.x.ai");
        var isOpenRouterDeveloperRoleModel = isOpenRouter
            && (model.Id.StartsWith("anthropic/", StringComparison.Ordinal)
                || model.Id.StartsWith("openai/", StringComparison.Ordinal));
        var cacheControlFormat = provider == "openrouter"
            && model.Id.StartsWith("anthropic/", StringComparison.Ordinal)
            ? "anthropic"
            : null;

        var thinkingFormat = isDeepSeek
            ? "deepseek"
            : isZai
                ? "zai"
                : isTogether
                    ? "together"
                    : isAntLing
                        ? "ant-ling"
                        : isOpenRouter
                            ? "openrouter"
                            : "openai";

        return new OpenAiCompletionsCompat
        {
            SupportsStore = !isNonStandard,
            SupportsDeveloperRole = isOpenRouterDeveloperRoleModel || (!isNonStandard && !isOpenRouter),
            SupportsReasoningEffort = !isGrok && !isZai && !isMoonshot && !isTogether
                && !isCloudflareAiGateway && !isNvidia && !isAntLing,
            SupportsUsageInStreaming = true,
            SupportsFinishReason = true,
            MaxTokensField = useMaxTokens ? "max_tokens" : "max_completion_tokens",
            RequiresToolResultName = false,
            RequiresAssistantAfterToolResult = false,
            RequiresThinkingAsText = false,
            RequiresReasoningContentOnAssistantMessages = isDeepSeek,
            ThinkingFormat = thinkingFormat,
            ZaiToolStream = false,
            SupportsThinkingTokenBudget = false,
            ThinkingTokenBudgetField = null,
            // OpenAI 兼容不蕴含 strict JSON schema 工具支持。
            SupportsStrictMode = false,
            SupportsOpenAIGrammarTools = false,
            SupportsMidConvoSystemMessages = false,
            SupportsMidConvoToolAdditions = false,
            CacheControlFormat = cacheControlFormat,
            SendSessionAffinityHeaders = isOpenRouter,
            SessionAffinityFormat = isOpenRouter ? "openrouter" : "openai",
            SupportsLongCacheRetention = !(isTogether
                || isCloudflareWorkersAi
                || isCloudflareAiGateway
                || isNvidia
                || isAntLing),
        };
    }

    // ---------------------------------------------------------------------
    // simple options（pi simple-options.ts + estimate.ts 子集）
    // ---------------------------------------------------------------------

    private static OpenAiCompletionsOptions BuildSimpleOptions(
        Model model, TranscriptContext context, SimpleStreamOptions? options)
    {
        var clampedReasoning = options?.Reasoning is { } reasoning
            ? ModelOperations.ClampThinkingLevel(model, ToModelThinkingLevel(reasoning))
            : (ModelThinkingLevel?)null;
        var reasoningEffort = clampedReasoning is { } level && level != ModelThinkingLevel.Off
            ? ToThinkingLevel(level)
            : (ThinkingLevel?)null;

        return new OpenAiCompletionsOptions
        {
            ApiKey = options?.ApiKey,
            MaxTokens = ClampMaxTokensToContext(model, context, options?.MaxTokens ?? model.MaxTokens),
            Temperature = options?.Temperature,
            SessionId = options?.SessionId,
            Headers = options?.Headers,
            SamplingParams = ResolveSamplingParams(
                model, clampedReasoning ?? ModelThinkingLevel.Off, options?.SamplingParams),
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            CancellationToken = options?.CancellationToken ?? default,
            Fetch = options?.Fetch,
            Identity = options?.Identity,
            Metadata = options?.Metadata,
            Env = options?.Env,
            CacheRetention = options?.CacheRetention,
            Transport = options?.Transport,
            WebsocketConnectTimeoutMs = options?.WebsocketConnectTimeoutMs,
            Telemetry = options?.Telemetry,
            // pi buildBaseOptions（simple-options.ts）：观察钩子必须透传，否则 simple 路径静默丢钩子。
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
            ToolChoice = options?.ToolChoice switch
            {
                ToolChoice.Auto => (JsonNode?)"auto",
                ToolChoice.None => "none",
                _ => null,
            },
            ReasoningEffort = reasoningEffort,
            ThinkingBudgets = options?.ThinkingBudgets,
        };
    }

    private static JsonObject? ResolveSamplingParams(
        Model model, ModelThinkingLevel level, JsonObject? requestParams)
    {
        var effectiveLevel = ModelOperations.ClampThinkingLevel(model, level);
        var levelParams = model.SamplingParamsByThinkingLevel is { } byLevel
            && byLevel.TryGetValue(effectiveLevel, out var levelValue)
            ? levelValue
            : null;
        if (model.SamplingParams is null && levelParams is null && requestParams is null)
        {
            return null;
        }

        var merged = new JsonObject();
        foreach (var source in new[] { model.SamplingParams, levelParams, requestParams })
        {
            if (source is null) continue;
            foreach (var entry in source)
            {
                merged[entry.Key] = entry.Value?.DeepClone();
            }
        }

        return merged;
    }

    private const int ContextSafetyTokens = 4096;
    private const int MinMaxTokens = 1;
    private const double CharsPerToken = 3.5;
    private const int EstimatedImageChars = 4800;
    private const int DefaultThinkingBudgetMinimal = 1024;
    private const int DefaultThinkingBudgetLow = 2048;
    private const int DefaultThinkingBudgetMedium = 8192;
    private const int DefaultThinkingBudgetHigh = 16384;

    private static int ClampMaxTokensToContext(Model model, TranscriptContext context, int maxTokens)
    {
        if (model.ContextWindow <= 0) return Math.Max(MinMaxTokens, maxTokens);
        var available = model.ContextWindow - (int)EstimateContextTokens(context.Messages) - ContextSafetyTokens;
        return Math.Min(maxTokens, Math.Max(MinMaxTokens, available));
    }

    private static int ThinkingBudgetForLevel(ThinkingLevel level, ThinkingBudgets? customBudgets)
    {
        var clamped = ClampReasoning(level);
        return clamped switch
        {
            ThinkingLevel.Minimal => customBudgets?.Minimal ?? DefaultThinkingBudgetMinimal,
            ThinkingLevel.Low => customBudgets?.Low ?? DefaultThinkingBudgetLow,
            ThinkingLevel.Medium => customBudgets?.Medium ?? DefaultThinkingBudgetMedium,
            ThinkingLevel.High => customBudgets?.High ?? DefaultThinkingBudgetHigh,
            _ => DefaultThinkingBudgetHigh,
        };
    }

    private static ThinkingLevel ClampReasoning(ThinkingLevel effort)
        => effort is ThinkingLevel.XHigh or ThinkingLevel.Max ? ThinkingLevel.High : effort;

    private static int ClampThinkingBudgetToAnswerRoom(int thinkingBudget, int ceiling)
        => Math.Min(thinkingBudget, Math.Max(0, ceiling - MinAnswerTokens));

    private static double EstimateContextTokens(IReadOnlyList<Message> messages)
    {
        var usageInfo = GetLastAssistantUsageInfo(messages);
        if (usageInfo is { } usage)
        {
            var usageTokens = CalculateContextTokens(usage.Usage);
            var trailingTokens = 0d;
            for (var i = usage.Index + 1; i < messages.Count; i++)
            {
                trailingTokens += EstimateMessageTokens(messages[i]);
            }

            return usageTokens + trailingTokens;
        }

        var tokens = 0d;
        foreach (var message in messages)
        {
            tokens += EstimateMessageTokens(message);
        }

        return tokens;
    }

    private static double CalculateContextTokens(Usage usage)
        => usage.TotalTokens > 0
            ? usage.TotalTokens
            : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;

    private static double EstimateMessageTokens(Message message)
    {
        switch (message)
        {
            case SystemMessage system:
                return EstimateTextTokens(GetSystemMessageText(system))
                    + EstimateToolsTokens(system.ToolsAdded)
                    + EstimateToolsTokens(system.ToolsRemoved);
            case UserMessage user:
                return EstimateTextAndImageContentTokens(user.Content);
            case ToolResultMessage toolResult:
                return EstimateTextAndImageContentTokens(MessageContent.From(toolResult.Content));
            case AssistantMessage assistant:
            {
                var chars = 0d;
                foreach (var block in assistant.Content)
                {
                    switch (block)
                    {
                        case TextContent text:
                            chars += text.Text.Length;
                            break;
                        case ThinkingContent thinking:
                            chars += thinking.Thinking.Length;
                            break;
                        case ToolCall toolCall:
                            chars += toolCall.Name.Length + toolCall.Arguments.ToJsonString().Length;
                            break;
                    }
                }

                return Math.Ceiling(chars / CharsPerToken);
            }

            default:
                return 0;
        }
    }

    private static double EstimateTextTokens(string text) => Math.Ceiling(text.Length / CharsPerToken);

    private static double EstimateTextAndImageContentTokens(MessageContent content)
    {
        var chars = 0d;
        foreach (var block in content.ToBlocks())
        {
            chars += block is TextContent text ? text.Text.Length : EstimatedImageChars;
        }

        return Math.Ceiling(chars / CharsPerToken);
    }

    private static double EstimateToolsTokens(IReadOnlyList<Tool>? tools)
    {
        if (tools is not { Count: > 0 }) return 0;
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.Parameters.DeepClone(),
            });
        }

        return EstimateTextTokens(array.ToJsonString());
    }

    private static double EstimateToolsTokens(IReadOnlyList<ToolReference>? tools)
    {
        if (tools is not { Count: > 0 }) return 0;
        var array = new JsonArray();
        foreach (var reference in tools)
        {
            array.Add(new JsonObject { ["name"] = reference.Name });
        }

        return EstimateTextTokens(array.ToJsonString());
    }

    private sealed record LastAssistantUsage(Usage Usage, int Index);

    private static LastAssistantUsage? GetLastAssistantUsageInfo(IReadOnlyList<Message> messages)
    {
        var latestPrefixTimestamp = long.MinValue;
        LastAssistantUsage? usageInfo = null;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is AssistantMessage assistant)
            {
                // 之后插入过更新的前缀消息（如 compaction 摘要）时，旧 usage 不能描述当前前缀。
                var usageAppliesToPrefix = assistant.Timestamp >= latestPrefixTimestamp;
                if (usageAppliesToPrefix
                    && assistant.StopReason is not (StopReason.Aborted or StopReason.Error)
                    && CalculateContextTokens(assistant.Usage) > 0)
                {
                    usageInfo = new LastAssistantUsage(assistant.Usage, i);
                }
            }

            latestPrefixTimestamp = Math.Max(latestPrefixTimestamp, messages[i].Timestamp);
        }

        return usageInfo;
    }

    // ---------------------------------------------------------------------
    // auth / env / 杂项（pi 84–97、provider-env.ts、openai-prompt-cache.ts）
    // ---------------------------------------------------------------------

    private static bool HasHeader(IReadOnlyDictionary<string, string?>? headers, string name)
    {
        if (headers is null) return false;
        foreach (var entry in headers)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase)
                && entry.Value is not null
                && entry.Value.Trim().Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetClientApiKey(
        string provider, string? apiKey, IReadOnlyDictionary<string, string?>? headers)
    {
        if (!string.IsNullOrEmpty(apiKey)) return apiKey;
        if (HasHeader(headers, "authorization") || HasHeader(headers, "cf-aig-authorization")) return "unused";
        throw new InvalidOperationException($"No API key for provider: {provider}");
    }

    private static CacheRetention ResolveCacheRetention(
        CacheRetention? cacheRetention, IReadOnlyDictionary<string, string>? env)
    {
        if (cacheRetention is { } retention) return retention;
        if (GetProviderEnvValue("PI_CACHE_RETENTION", env) == "long") return CacheRetention.Long;
        return CacheRetention.Short;
    }

    private static string? GetProviderEnvValue(string name, IReadOnlyDictionary<string, string>? env)
    {
        if (env is not null && env.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
        {
            return value;
        }

        var processValue = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(processValue) ? null : processValue;
    }

    private static string? ClampOpenAIPromptCacheKey(string? key)
    {
        if (key is null) return null;
        if (key.Length <= OpenAiPromptCacheKeyMaxLength) return key;
        return key[..OpenAiPromptCacheKeyMaxLength];
    }

    // ---------------------------------------------------------------------
    // 错误格式化（pi error-body.ts 的 C# 例外形状等价）
    // ---------------------------------------------------------------------

    private static string FormatProviderError(Exception error)
    {
        int? status;
        string? body;
        string message;
        if (error is FetchException fetchError)
        {
            status = fetchError.StatusCode;
            body = fetchError.ErrorBody;
            message = fetchError.Message;
        }
        else
        {
            status = null;
            body = null;
            message = string.IsNullOrEmpty(error.Message) ? error.GetType().Name : error.Message;
        }

        string? bodyText = null;
        if (body is not null)
        {
            var trimmed = body.Trim();
            if (trimmed.Length > 0)
            {
                bodyText = TruncateErrorText(trimmed, MaxProviderErrorBodyChars);
                if (status is { } statusCode && !message.Contains(bodyText, StringComparison.Ordinal))
                {
                    message = $"{statusCode}: {bodyText}";
                }
            }
        }

        // OpenRouter 在 error.metadata.raw 里给附加信息；已在 message 里则不重复。
        var raw = TryGetRawMetadata(bodyText);
        if (raw is not null && !message.Contains(raw, StringComparison.Ordinal))
        {
            message += $"\n{raw}";
        }

        return message;
    }

    private static string TruncateErrorText(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return $"{text[..maxChars]}... [truncated {text.Length - maxChars} chars]";
    }

    private static string? TryGetRawMetadata(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject parsed) return null;
            return JsonStr(JsonObj(JsonObj(parsed, "error"), "metadata"), "raw");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // 流内可变输出（pi.net MutableAssistantMessage 等价；S3 AssistantMessage 是 record）
    // ---------------------------------------------------------------------

    private sealed class MutableOutput
    {
        public MutableOutput(Model model)
        {
            ModelId = model.Id;
            Api = model.Api;
            Provider = model.Provider;
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public string ModelId { get; }
        public string Api { get; }
        public string Provider { get; }
        public long Timestamp { get; }
        public string? ResponseId { get; set; }
        public string? ResponseModel { get; set; }
        public string? RawStopReason { get; set; }
        public string? ErrorMessage { get; set; }
        public StopReason StopReason { get; set; } = StopReason.Pending;
        public Usage Usage { get; set; } = new();

        public AssistantMessage Snapshot(IReadOnlyList<StreamBlock> blocks)
            => new()
            {
                Content = blocks.Select(block => block.ToContentBlock()).ToList(),
                Api = Api,
                Provider = Provider,
                Model = ModelId,
                Usage = Usage,
                StopReason = StopReason,
                ErrorMessage = ErrorMessage,
                RawStopReason = RawStopReason,
                ResponseId = ResponseId,
                ResponseModel = ResponseModel,
                Timestamp = Timestamp,
            };
    }

    // ---------------------------------------------------------------------
    // 流内块模型（pi StreamingBlock = TextContent | ThinkingContent | ToolCall）
    // ---------------------------------------------------------------------

    private abstract class StreamBlock
    {
        public abstract ContentBlock ToContentBlock();
    }

    private sealed class TextStreamBlock : StreamBlock
    {
        public StringBuilder Text { get; } = new();

        public override ContentBlock ToContentBlock() => new TextContent { Text = Text.ToString() };
    }

    private sealed class ThinkingStreamBlock : StreamBlock
    {
        public StringBuilder Thinking { get; } = new();
        public string ThinkingSignature { get; set; } = "";

        public override ContentBlock ToContentBlock()
            => new ThinkingContent
            {
                Thinking = Thinking.ToString(),
                ThinkingSignature = string.IsNullOrEmpty(ThinkingSignature) ? null : ThinkingSignature,
            };
    }

    private sealed class ToolCallStreamBlock : StreamBlock
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public JsonObject Arguments { get; set; } = new();
        public string? ArgumentsError { get; set; }
        public ToolCallInput Input { get; set; } = ToolCallInput.Json(new ToolCallArguments(), null);

        public override ContentBlock ToContentBlock()
            => new ToolCall
            {
                Id = Id,
                Name = Name,
                Arguments = Arguments,
                ArgumentsError = ArgumentsError,
            };

        public ToolCall ToToolCall()
            => new()
            {
                Id = Id,
                Name = Name,
                Arguments = Arguments,
                ArgumentsError = ArgumentsError,
            };
    }

    private enum ToolCallInputKind
    {
        Json,
        Custom,
    }

    private sealed record ToolCallInput
    {
        public required ToolCallInputKind Kind { get; init; }
        public ToolCallArguments? Arguments { get; init; }
        public string? Property { get; init; }
        public GrammarToolInputJsonBuffer? Buffer { get; init; }
        public int? StreamIndex { get; set; }

        public static ToolCallInput Json(ToolCallArguments arguments, int? streamIndex)
            => new() { Kind = ToolCallInputKind.Json, Arguments = arguments, StreamIndex = streamIndex };

        public static ToolCallInput Custom(string property, GrammarToolInputJsonBuffer buffer)
            => new() { Kind = ToolCallInputKind.Custom, Property = property, Buffer = buffer };
    }

    // ---------------------------------------------------------------------
    // JSON 读取小工具（pi.net JsonObject 扩展的等价私有静态方法）
    // ---------------------------------------------------------------------

    private static string? JsonStr(JsonObject? obj, string key)
        => obj is not null
            && obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue primitive
            && primitive.TryGetValue<string>(out var text)
            ? text
            : null;

    private static double? JsonNum(JsonObject? obj, string key)
        => obj is not null
            && obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue primitive
            && primitive.TryGetValue<double>(out var number)
            ? number
            : null;

    private static JsonObject? JsonObj(JsonObject? obj, string key)
        => obj is not null
            && obj.TryGetPropertyValue(key, out var value)
            ? value as JsonObject
            : null;

    private static bool? JsonBool(JsonObject? obj, string key)
        => obj is not null
            && obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue primitive
            && primitive.TryGetValue<bool>(out var flag)
            ? flag
            : null;

    private static ModelThinkingLevel ToModelThinkingLevel(ThinkingLevel level)
        => level switch
        {
            ThinkingLevel.Minimal => ModelThinkingLevel.Minimal,
            ThinkingLevel.Low => ModelThinkingLevel.Low,
            ThinkingLevel.Medium => ModelThinkingLevel.Medium,
            ThinkingLevel.High => ModelThinkingLevel.High,
            ThinkingLevel.XHigh => ModelThinkingLevel.XHigh,
            ThinkingLevel.Max => ModelThinkingLevel.Max,
            _ => ModelThinkingLevel.Off,
        };

    private static ThinkingLevel? ToThinkingLevel(ModelThinkingLevel level)
        => level switch
        {
            ModelThinkingLevel.Minimal => ThinkingLevel.Minimal,
            ModelThinkingLevel.Low => ThinkingLevel.Low,
            ModelThinkingLevel.Medium => ThinkingLevel.Medium,
            ModelThinkingLevel.High => ThinkingLevel.High,
            ModelThinkingLevel.XHigh => ThinkingLevel.XHigh,
            ModelThinkingLevel.Max => ThinkingLevel.Max,
            _ => null,
        };

    private static string ToThinkingLevelWire(ThinkingLevel level)
        => level switch
        {
            ThinkingLevel.Minimal => "minimal",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.XHigh => "xhigh",
            ThinkingLevel.Max => "max",
            _ => "off",
        };
}
