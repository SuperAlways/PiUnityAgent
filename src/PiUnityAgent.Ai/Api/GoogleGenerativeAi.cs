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
/// Google Generative AI（Gemini）协议适配器（pi <c>api/google-generative-ai.ts</c> + <c>google-shared.ts</c> 移植）：
/// REST <c>POST {baseUrl}/models/{model}:streamGenerateContent?alt=sse</c>；请求 body 顶层
/// <c>contents</c>/<c>systemInstruction</c>/<c>tools</c>/<c>toolConfig</c>，<c>generationConfig</c> 内含
/// <c>thinkingConfig</c>（TS 经 @google/genai SDK 展开即此形状；apiKey 经 <c>x-goog-api-key</c> 头）。
/// 响应为 data-only SSE：GenerateContentResponse chunk 流 —— text/thought part 切换、thoughtSignature
/// 保留、functionCall（缺 id/重复 id 时生成）、finishReason 映射与 toolUse 升级、
/// usageMetadata（thoughtsTokenCount 为 reasoning 子集）。pi 的「拒绝自定义 fetch」限制不照抄：
/// 统一走 <see cref="FetchFunction"/> 端口。失败一律编码为流内终态，不抛出；重试只在流建立前。
/// </summary>
public static class GoogleGenerativeAi
{
    /// <summary>模型 baseUrl 缺省时的官方 v1beta 端点（baseUrl 自带版本路径，不再追加 apiVersion）。</summary>
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private const int MaxNormalizedToolCallIdLength = 64;
    private const int MaxProviderErrorBodyChars = 4000;

    private const double CharsPerToken = 3.5;
    private const int EstimatedImageChars = 4800;

    private static readonly Regex UnpairedSurrogateRegex = new(
        @"[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]",
        RegexOptions.Compiled);

    /// <summary>Gemini 3 系模型 id（gemini-3.x-pro/flash、gemini-4+、flash-latest、gemma-4）用离散 thinkingLevel 控制。</summary>
    private static readonly Regex Gemini3ProFlashRegex = new(@"gemini-3(?:\.\d+)?-(?:pro|flash)", RegexOptions.Compiled);
    private static readonly Regex Gemini4PlusRegex = new(@"gemini-(?:[4-9]|[1-9]\d)", RegexOptions.Compiled);
    private static readonly Regex Gemma4Regex = new(@"gemma-?4", RegexOptions.Compiled);
    private static readonly Regex GeminiMajorVersionRegex = new(@"^gemini(?:-live)?-(\d+)", RegexOptions.Compiled);

    /// <summary>thought 签名必须是 base64（TYPE_BYTES）。</summary>
    private static readonly Regex Base64SignaturePattern = new("^[A-Za-z0-9+/]+={0,2}$", RegexOptions.Compiled);

    private static int _toolCallCounter;

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
        GoogleOptions? options = null)
        => Run(model, context, options);

    public static IAssistantMessageEventStream StreamSimple(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options = null)
        => Run(model, context, BuildSimpleOptions(model, context, options));

    // ---------------------------------------------------------------------
    // stream（pi google-generative-ai.ts stream 59–298）
    // ---------------------------------------------------------------------

    private static IAssistantMessageEventStream Run(Model model, TranscriptContext context, StreamOptions? options)
    {
        var stream = new AssistantMessageEventStream();
        // Gemini 无中段 system 消息：collapseSystemMessages 归一（pi stream 归一步）。
        var normalizedContext = CollapseSystemMessages(context);

        _ = RunCoreAsync(stream, model, normalizedContext, options as GoogleOptions);
        return stream;
    }

    private static async Task RunCoreAsync(
        AssistantMessageEventStream stream,
        Model model,
        TranscriptContext normalizedContext,
        GoogleOptions? options)
    {
        var blocks = new List<StreamBlock>();
        var output = new MutableOutput(model);
        try
        {
            // pi：Google 适配器必须显式带 apiKey（SDK client 构造需要）。
            var apiKey = options?.ApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new InvalidOperationException($"No API key for provider: {model.Provider}");
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

            // pi：buildParams → onPayload → retryGoogleRequest(请求建立)。payload 只构造一次，
            // OnPayload 也只调一次；重试只覆盖请求建立阶段（非 2xx → FetchException）。
            JsonNode payload = BuildParams(model, normalizedContext, options);
            if (options?.OnPayload is { } onPayload)
            {
                var next = await onPayload(payload, model).ConfigureAwait(false);
                if (next is not null)
                {
                    payload = next;
                }
            }

            var request = BuildRequest(model, options, payload, apiKey);
            var response = await ProviderRetry.RunAsync(
                async requestToken =>
                {
                    var attempt = await fetch(request, fetchOptions, requestToken).ConfigureAwait(false);
                    if (attempt.StatusCode is < 200 or >= 300)
                    {
                        // 非 2xx 抛携带 status/headers/body 的 FetchException（408/409/429/5xx 可重试）。
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

            // pi stream:102：start 在事件消费之前推。
            stream.Push(new AssistantMessageEvent.Start(output.Snapshot(blocks)));

            await ProcessGoogleStream(
                response.Body, output, blocks, stream, model,
                options?.OnProviderStreamEvent, lifetime.Token).ConfigureAwait(false);

            if (signal.IsCancellationRequested)
            {
                throw new OperationCanceledException("Request was aborted", signal);
            }

            if (output.StopReason == StopReason.Pending)
            {
                throw new InvalidOperationException("Google stream ended without a finish reason");
            }

            if (output.StopReason is StopReason.Aborted or StopReason.Error)
            {
                throw new InvalidOperationException(output.RawStopReason is not null
                    ? $"Provider stopped with: {output.RawStopReason}"
                    : output.ErrorMessage ?? "An unknown error occurred");
            }

            var final = output.Snapshot(blocks);
            stream.Push(new AssistantMessageEvent.Done(output.StopReason, final));
            stream.End(final);
        }
        catch (Exception error)
        {
            var signal = options?.CancellationToken ?? default;
            var aborted = signal.IsCancellationRequested;
            output.StopReason = aborted ? StopReason.Aborted : StopReason.Error;
            // pi：取消令牌触发的 OCE 归一到 "Request was aborted"；其余按 provider 错误格式化。
            output.ErrorMessage = aborted && error is OperationCanceledException
                ? "Request was aborted"
                : FormatProviderError(error);
            var failed = output.Snapshot(blocks);
            stream.Push(new AssistantMessageEvent.Error(output.StopReason, failed));
            stream.End(failed);
        }
    }

    // ---------------------------------------------------------------------
    // 流消费（GenerateContentResponse chunk → 统一流事件）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 消费 data-only SSE：text/thought part 折叠为文本/思维块（thoughtSignature 保留块内首个
    /// 非空值）、functionCall 生成唯一 ID 并立刻推三连事件、finishReason 映射 + toolUse 升级、
    /// usageMetadata 含 reasoning 子集（pi stream 内循环）。
    /// </summary>
    private static async Task ProcessGoogleStream(
        Stream body,
        MutableOutput output,
        List<StreamBlock> blocks,
        AssistantMessageEventStream stream,
        Model model,
        Func<JsonNode, ProviderModel, Task>? onProviderStreamEvent,
        CancellationToken cancellationToken)
    {
        StreamBlock? currentBlock = null;

        int BlockIndex() => blocks.Count - 1;

        void FinishCurrentBlock()
        {
            if (currentBlock is null) return;
            if (currentBlock is TextStreamBlock textBlock)
            {
                stream.Push(new AssistantMessageEvent.TextEnd(
                    BlockIndex(), textBlock.Text, output.Snapshot(blocks)));
            }
            else if (currentBlock is ThinkingStreamBlock thinkingBlock)
            {
                stream.Push(new AssistantMessageEvent.ThinkingEnd(
                    BlockIndex(), thinkingBlock.Thinking, output.Snapshot(blocks)));
            }

            currentBlock = null;
        }

        await foreach (var sse in SseEventReader.ReadAsync(body, cancellationToken).ConfigureAwait(false))
        {
            if (JsonNode.Parse(sse.Data) is not JsonObject chunk)
            {
                // 非对象 data 照 pi 的宽松解析跳过（SDK 不会产出这种 chunk）。
                continue;
            }

            if (onProviderStreamEvent is { } observer)
            {
                await observer(chunk, model).ConfigureAwait(false);
            }

            // responseId 是每个响应的输出标识：保留首个非空（pi output.responseId ||= chunk.responseId）。
            if (output.ResponseId is null
                && JsonStr(chunk, "responseId") is { Length: > 0 } responseId)
            {
                output.ResponseId = responseId;
            }

            // pi：candidate 可选链取 candidates[0]；usageMetadata 与 candidate 无关，独立处理。
            var candidate = chunk["candidates"] is JsonArray candidates && candidates.Count > 0
                ? candidates[0] as JsonObject
                : null;

            if (candidate?["content"]?["parts"] is JsonArray parts)
            {
                foreach (var partNode in parts)
                {
                    if (partNode is not JsonObject part)
                    {
                        continue;
                    }

                    if (part["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var text))
                    {
                        // thought:true 是思考内容的决定性标记；thoughtSignature 可出现在任意 part 上。
                        var isThinking = IsThinkingPart(part);
                        if (currentBlock is null
                            || (isThinking && currentBlock is not ThinkingStreamBlock)
                            || (!isThinking && currentBlock is not TextStreamBlock))
                        {
                            FinishCurrentBlock();
                            if (isThinking)
                            {
                                var thinkingBlock = new ThinkingStreamBlock();
                                blocks.Add(thinkingBlock);
                                currentBlock = thinkingBlock;
                                stream.Push(new AssistantMessageEvent.ThinkingStart(
                                    BlockIndex(), output.Snapshot(blocks)));
                            }
                            else
                            {
                                var textBlock = new TextStreamBlock();
                                blocks.Add(textBlock);
                                currentBlock = textBlock;
                                stream.Push(new AssistantMessageEvent.TextStart(
                                    BlockIndex(), output.Snapshot(blocks)));
                            }
                        }

                        var signature = JsonStr(part, "thoughtSignature");
                        if (currentBlock is ThinkingStreamBlock thinking)
                        {
                            thinking.Thinking += text;
                            thinking.ThinkingSignature =
                                RetainThoughtSignature(thinking.ThinkingSignature, signature);
                            stream.Push(new AssistantMessageEvent.ThinkingDelta(
                                BlockIndex(), text, output.Snapshot(blocks)));
                        }
                        else if (currentBlock is TextStreamBlock textBlock)
                        {
                            textBlock.Text += text;
                            textBlock.TextSignature =
                                RetainThoughtSignature(textBlock.TextSignature, signature);
                            stream.Push(new AssistantMessageEvent.TextDelta(
                                BlockIndex(), text, output.Snapshot(blocks)));
                        }
                    }

                    if (part["functionCall"] is JsonObject functionCall)
                    {
                        // functionCall 之前必先闭合当前文本/思维块（pi 同）。
                        FinishCurrentBlock();

                        // 缺 id 或 id 重复时生成 `<name>_<ts>_<counter>`（pi `${name || ""}` 前缀同）。
                        var providedId = JsonStr(functionCall, "id");
                        var needsNewId = string.IsNullOrEmpty(providedId)
                            || blocks.OfType<ToolCallStreamBlock>().Any(call => call.Id == providedId);
                        var toolCallId = needsNewId
                            ? $"{JsonStr(functionCall, "name") ?? ""}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _toolCallCounter)}"
                            : providedId!;

                        var toolBlock = new ToolCallStreamBlock
                        {
                            Id = toolCallId,
                            Name = JsonStr(functionCall, "name") ?? "",
                            Arguments = functionCall["args"] as JsonObject is { } args
                                ? (JsonObject)args.DeepClone()
                                : new JsonObject(),
                            ThoughtSignature = JsonStr(part, "thoughtSignature"),
                        };
                        blocks.Add(toolBlock);
                        stream.Push(new AssistantMessageEvent.ToolCallStart(
                            BlockIndex(), output.Snapshot(blocks)));
                        stream.Push(new AssistantMessageEvent.ToolCallDelta(
                            BlockIndex(), toolBlock.Arguments.ToJsonString(), output.Snapshot(blocks)));
                        stream.Push(new AssistantMessageEvent.ToolCallEnd(
                            BlockIndex(), toolBlock.ToToolCall(), output.Snapshot(blocks)));
                    }
                }
            }

            if (JsonStr(candidate, "finishReason") is { } finishReason)
            {
                output.RawStopReason = finishReason;
                output.StopReason = MapStopReason(finishReason);
                if (blocks.OfType<ToolCallStreamBlock>().Any() && output.StopReason == StopReason.Stop)
                {
                    output.StopReason = StopReason.ToolUse;
                }
            }

            if (chunk["usageMetadata"] is JsonObject usage)
            {
                var prompt = (int)(JsonNum(usage, "promptTokenCount") ?? 0);
                var cached = (int)(JsonNum(usage, "cachedContentTokenCount") ?? 0);
                var candidatesTokens = (int)(JsonNum(usage, "candidatesTokenCount") ?? 0);
                var thoughts = (int)(JsonNum(usage, "thoughtsTokenCount") ?? 0);
                output.Usage = new Usage
                {
                    // pi 不夹取下界：cached 大于 prompt 时 input 可为负。
                    Input = prompt - cached,
                    Output = candidatesTokens + thoughts,
                    CacheRead = cached,
                    CacheWrite = 0,
                    Reasoning = thoughts,
                    // 直接用 provider 的 totalTokenCount（未上报即 0），非全桶求和。
                    TotalTokens = (int)(JsonNum(usage, "totalTokenCount") ?? 0),
                };
                output.Usage = output.Usage with
                {
                    Cost = ModelOperations.CalculateCost(model, output.Usage),
                };
            }
        }

        FinishCurrentBlock();
    }

    // ---------------------------------------------------------------------
    // 流内块模型
    // ---------------------------------------------------------------------

    private abstract class StreamBlock
    {
        public abstract ContentBlock ToContentBlock();
    }

    private sealed class TextStreamBlock : StreamBlock
    {
        public string Text { get; set; } = "";
        public string? TextSignature { get; set; }

        public override ContentBlock ToContentBlock()
            => new TextContent { Text = Text, TextSignature = TextSignature };
    }

    private sealed class ThinkingStreamBlock : StreamBlock
    {
        public string Thinking { get; set; } = "";
        public string? ThinkingSignature { get; set; }

        public override ContentBlock ToContentBlock()
            => new ThinkingContent
            {
                Thinking = Thinking,
                ThinkingSignature = ThinkingSignature,
            };
    }

    private sealed class ToolCallStreamBlock : StreamBlock
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public JsonObject Arguments { get; set; } = new();
        public string? ThoughtSignature { get; set; }

        public override ContentBlock ToContentBlock()
            => new ToolCall
            {
                Id = Id,
                Name = Name,
                Arguments = Arguments,
                ThoughtSignature = ThoughtSignature,
            };

        public ToolCall ToToolCall()
            => new()
            {
                Id = Id,
                Name = Name,
                Arguments = Arguments,
                ThoughtSignature = ThoughtSignature,
            };
    }

    // ---------------------------------------------------------------------
    // buildParams（pi 364–425；wire 形状按 GenerateContentRequest 顶层字段）
    // ---------------------------------------------------------------------

    internal static JsonObject BuildParams(Model model, TranscriptContext context, GoogleOptions? options)
    {
        var contents = ConvertMessages(model, context);
        var initialSystemMessage = GetInitialSystemMessage(context.Messages);
        var systemInstruction = initialSystemMessage is not null ? GetSystemMessageText(initialSystemMessage) : "";
        var currentTools = GetCurrentTools(context.Messages);

        var generationConfig = new JsonObject();
        if (options?.Temperature is { } temperature)
        {
            generationConfig["temperature"] = temperature;
        }

        if (options?.MaxTokens is { } maxTokens)
        {
            generationConfig["maxOutputTokens"] = maxTokens;
        }

        var supportsStrictMode = SupportsGoogleStrictToolSampling(model.Id);
        var functionCallingMode = currentTools.Count > 0
            ? ResolveGoogleFunctionCallingMode(currentTools, GetToolChoice(options), supportsStrictMode)
            : null;

        var payload = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = generationConfig,
        };

        // GenerateContentRequest 顶层字段：systemInstruction / tools / toolConfig
        // （TS 经 @google/genai SDK 的 config 字段在传输层展开即此形状）。
        if (systemInstruction.Length > 0)
        {
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["text"] = SanitizeSurrogates(systemInstruction),
                }),
            };
        }

        if (currentTools.Count > 0)
        {
            payload["tools"] = ConvertTools(currentTools, supportsStrictMode);
        }

        if (functionCallingMode is not null)
        {
            payload["toolConfig"] = new JsonObject
            {
                ["functionCallingConfig"] = new JsonObject { ["mode"] = functionCallingMode },
            };
        }

        var thinking = options?.Thinking;
        if (thinking is { Enabled: true } && model.Reasoning)
        {
            var thinkingConfig = new JsonObject { ["includeThoughts"] = true };
            if (thinking.Level is { } level)
            {
                thinkingConfig["thinkingLevel"] = ToGoogleSdkThinkingLevel(level);
            }
            else if (thinking.BudgetTokens is { } budget)
            {
                thinkingConfig["thinkingBudget"] = budget;
            }

            generationConfig["thinkingConfig"] = thinkingConfig;
        }
        else if (model.Reasoning && thinking is { Enabled: false })
        {
            generationConfig["thinkingConfig"] = GetDisabledGoogleThinkingConfig(model);
        }

        return payload;
    }

    /// <summary>GoogleOptions.ToolChoice（native JsonNode）只接受字符串 "auto"/"none"/"any"（pi string 字面量类型）。</summary>
    private static string? GetToolChoice(GoogleOptions? options)
        => options?.ToolChoice is JsonValue choiceValue && choiceValue.TryGetValue<string>(out var choice)
            ? choice
            : null;

    // ---------------------------------------------------------------------
    // convertMessages（pi google-shared.ts 193–345）
    // ---------------------------------------------------------------------

    private static JsonArray ConvertMessages(Model model, TranscriptContext context)
    {
        // Gemini 无中段 system 消息：首段 prompt 走 systemInstruction（Run 已折叠，这里再折叠幂等）。
        var conversation = WithoutInitialSystemMessage(CollapseSystemMessages(context).Messages);
        var contents = new JsonArray();

        // 仅要求显式 id 的模型（Gemini 3+/claude-/gpt-oss-）才归一 tool call id（pi 同）。
        var transformedMessages = TransformMessages(
            conversation,
            model,
            RequiresToolCallId(model.Id) ? NormalizeToolCallId : (Func<string, string>)(id => id));

        foreach (var message in transformedMessages)
        {
            if (message is UserMessage user)
            {
                var parts = new JsonArray();
                foreach (var item in user.Content.ToBlocks())
                {
                    switch (item)
                    {
                        case TextContent text:
                            parts.Add(new JsonObject { ["text"] = SanitizeSurrogates(text.Text) });
                            break;
                        case ImageContent image:
                            parts.Add(new JsonObject
                            {
                                ["inlineData"] = new JsonObject
                                {
                                    ["mimeType"] = image.MimeType,
                                    ["data"] = image.Data,
                                },
                            });
                            break;
                    }
                }

                if (parts.Count == 0) continue;
                contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
            }
            else if (message is AssistantMessage assistant)
            {
                var parts = new JsonArray();
                // 只有同 provider+model 才保留 thinking 块；否则转纯文本（无标记，避免模型模仿）。
                var isSameProviderAndModel = assistant.Provider == model.Provider
                    && assistant.Model == model.Id;

                foreach (var block in assistant.Content)
                {
                    switch (block)
                    {
                        case TextContent textBlock:
                        {
                            var thoughtSignature = ResolveThoughtSignature(
                                isSameProviderAndModel, textBlock.TextSignature);
                            // 空 text 块丢弃——除非带签名：Gemini 可能把签名挂在空文本 part 上
                            // 并要求原样回传，丢弃会破坏推理链。
                            if (string.IsNullOrWhiteSpace(textBlock.Text) && thoughtSignature is null) continue;
                            var part = new JsonObject { ["text"] = SanitizeSurrogates(textBlock.Text) };
                            if (thoughtSignature is not null) part["thoughtSignature"] = thoughtSignature;
                            parts.Add(part);
                            break;
                        }

                        case ThinkingContent thinkingBlock:
                        {
                            if (isSameProviderAndModel)
                            {
                                var thoughtSignature = ResolveThoughtSignature(
                                    isSameProviderAndModel, thinkingBlock.ThinkingSignature);
                                if (string.IsNullOrWhiteSpace(thinkingBlock.Thinking) && thoughtSignature is null)
                                {
                                    continue;
                                }

                                var part = new JsonObject
                                {
                                    ["thought"] = true,
                                    ["text"] = SanitizeSurrogates(thinkingBlock.Thinking),
                                };
                                if (thoughtSignature is not null) part["thoughtSignature"] = thoughtSignature;
                                parts.Add(part);
                            }
                            else
                            {
                                // 跨模型：签名不可用，空块丢弃。
                                if (string.IsNullOrWhiteSpace(thinkingBlock.Thinking)) continue;
                                parts.Add(new JsonObject
                                {
                                    ["text"] = SanitizeSurrogates(thinkingBlock.Thinking),
                                });
                            }

                            break;
                        }

                        case ToolCall toolCall:
                        {
                            var thoughtSignature = ResolveThoughtSignature(
                                isSameProviderAndModel, toolCall.ThoughtSignature);
                            var functionCall = new JsonObject
                            {
                                ["name"] = toolCall.Name,
                                ["args"] = (JsonObject)toolCall.Arguments.DeepClone(),
                            };
                            if (RequiresToolCallId(model.Id))
                            {
                                functionCall["id"] = toolCall.Id;
                            }

                            var part = new JsonObject { ["functionCall"] = functionCall };
                            if (thoughtSignature is not null) part["thoughtSignature"] = thoughtSignature;
                            parts.Add(part);
                            break;
                        }
                    }
                }

                if (parts.Count == 0) continue;
                contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
            }
            else if (message is ToolResultMessage toolResult)
            {
                var textResult = string.Join(
                    "\n", toolResult.Content.OfType<TextContent>().Select(content => content.Text));
                var imageContent = model.Input.Contains(ModelInputModality.Image)
                    ? toolResult.Content.OfType<ImageContent>().ToList()
                    : new List<ImageContent>();

                var hasText = textResult.Trim().Length > 0;
                var hasImages = imageContent.Count > 0;
                var modelSupportsMultimodalFunctionResponse = SupportsMultimodalFunctionResponse(model.Id);

                // 成功用 "output" 键，错误用 "error" 键（SDK 文档约定）。
                var responseValue = hasText
                    ? SanitizeSurrogates(textResult)
                    : hasImages ? "(see attached image)" : "";

                var imageParts = new JsonArray();
                foreach (var imageBlock in imageContent)
                {
                    imageParts.Add(new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        {
                            ["mimeType"] = imageBlock.MimeType,
                            ["data"] = imageBlock.Data,
                        },
                    });
                }

                var functionResponse = new JsonObject
                {
                    ["name"] = toolResult.ToolName,
                    ["response"] = new JsonObject
                    {
                        [toolResult.IsError ? "error" : "output"] = responseValue,
                    },
                };
                if (hasImages && modelSupportsMultimodalFunctionResponse)
                {
                    functionResponse["parts"] = imageParts;
                }

                if (RequiresToolCallId(model.Id))
                {
                    functionResponse["id"] = toolResult.ToolCallId;
                }

                var functionResponsePart = new JsonObject { ["functionResponse"] = functionResponse };

                // Cloud Code Assist 要求全部 functionResponse 在单个 user turn：能合并就合并。
                if (contents.Count > 0
                    && contents[contents.Count - 1] is JsonObject lastContent
                    && JsonStr(lastContent, "role") == "user"
                    && lastContent["parts"] is JsonArray lastParts
                    && lastParts.OfType<JsonObject>().Any(part => part.ContainsKey("functionResponse")))
                {
                    lastParts.Add(functionResponsePart);
                }
                else
                {
                    contents.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(functionResponsePart),
                    });
                }

                // Gemini < 3：图片放独立 user 消息。
                if (hasImages && !modelSupportsMultimodalFunctionResponse)
                {
                    var turnParts = new JsonArray(new JsonObject { ["text"] = "Tool result image:" });
                    foreach (var imagePart in imageParts.ToArray())
                    {
                        turnParts.Add(imagePart!.DeepClone());
                    }

                    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = turnParts });
                }
            }
        }

        return contents;
    }

    /// <summary>规范化 tool call id：仅要求显式 id 的模型（Gemini 3+/claude-/gpt-oss-）按 ^[a-zA-Z0-9_-]{1,64}$ 归一。</summary>
    private static string NormalizeToolCallId(string id)
    {
        var builder = new StringBuilder(id.Length);
        foreach (var ch in id)
        {
            var asciiLetterOrDigit = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');
            builder.Append(asciiLetterOrDigit || ch is '_' or '-' ? ch : '_');
        }

        return builder.Length <= MaxNormalizedToolCallIdLength
            ? builder.ToString()
            : builder.ToString(0, MaxNormalizedToolCallIdLength);
    }

    // ---------------------------------------------------------------------
    // transform-messages.ts 移植（与 Anthropic/OpenAI 家族同形的共享转换）
    // ---------------------------------------------------------------------

    private static IReadOnlyList<Message> TransformMessages(
        IReadOnlyList<Message> messages,
        Model model,
        Func<string, string> normalizeToolCallId)
    {
        var toolCallIdMap = new Dictionary<string, string>();
        var imageAwareMessages = DowngradeUnsupportedImages(messages, model);

        // First pass: image downgrade / thinking 保留 / tool call id 归一。
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
                                    // 跨模型的 thought 签名不可用：剥掉再发。
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

        // Second pass: 孤立 tool call 补合成结果；errored/aborted assistant 整条跳过。
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

    // ---------------------------------------------------------------------
    // convertTools + JSON Schema const→enum（pi google-shared.ts 347–460）
    // ---------------------------------------------------------------------

    private static readonly HashSet<string> JsonSchemaMetaDeclarations = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$anchor", "$dynamicAnchor", "$vocabulary", "$comment", "$defs", "definitions",
    };

    private static readonly HashSet<string> JsonSchemaNameMaps = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "$defs", "definitions", "dependentSchemas", "dependencies",
    };

    private static readonly HashSet<string> JsonSchemaSubschemas = new(StringComparer.Ordinal)
    {
        "additionalProperties", "unevaluatedProperties", "propertyNames", "items", "prefixItems",
        "additionalItems", "unevaluatedItems", "contains", "allOf", "anyOf", "oneOf", "not",
        "if", "then", "else", "contentSchema",
    };

    /// <summary>
    /// 工具 → Gemini 函数声明：<c>parametersJsonSchema</c>（完整 JSON Schema）+ const→enum 改写
    /// （Gemini 在 parametersJsonSchema 里忽略 const 但认 enum）。pi convertTools 的
    /// useParameters 分支只服务 Vertex Cloud Code Assist，不在 S5 范围。
    /// </summary>
    private static JsonArray ConvertTools(IReadOnlyList<Tool> tools, bool supportsStrictMode)
    {
        var declarations = new JsonArray();
        foreach (var tool in tools)
        {
            var strict = ResolveJsonSchemaStrictSampling(tool, supportsStrictMode);
            var parameters = GetJsonSchemaToolParameters(tool, strict);
            declarations.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parametersJsonSchema"] = RewriteConstAsEnum(parameters),
            });
        }

        return new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
    }

    /// <summary>pi rewriteConstAsEnum：字符串/数字 const 改写成单值 enum；只下钻子 schema 关键字与名字映射。</summary>
    private static JsonNode? RewriteConstAsEnum(JsonNode? schema, bool isNameMap = false)
    {
        if (schema is JsonArray array)
        {
            var items = new JsonArray();
            foreach (var item in array.ToArray())
            {
                items.Add(RewriteConstAsEnum(item));
            }

            return items;
        }

        if (schema is not JsonObject obj) return schema?.DeepClone();

        var result = new JsonObject();
        foreach (var entry in obj.ToArray())
        {
            if (isNameMap || JsonSchemaSubschemas.Contains(entry.Key))
            {
                result[entry.Key] = RewriteConstAsEnum(entry.Value);
            }
            else if (JsonSchemaNameMaps.Contains(entry.Key))
            {
                result[entry.Key] = RewriteConstAsEnum(entry.Value, true);
            }
            else
            {
                result[entry.Key] = entry.Value?.DeepClone();
            }
        }

        if (!isNameMap && result.ContainsKey("const") && !result.ContainsKey("enum"))
        {
            if (result["const"] is JsonValue literal
                && literal.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
            {
                var constant = literal.DeepClone();
                result.Remove("const");
                result["enum"] = new JsonArray(constant);
            }
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // strict 采样（pi constrained-sampling.ts resolveJsonSchemaStrictSampling，无 provider 关键字检查）
    // ---------------------------------------------------------------------

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

    /// <summary>pi makeStrictJsonSchema：把工具 schema 编译成 strict 子集。</summary>
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

            foreach (var variant in anyOf.ToArray())
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

        if (schema.ContainsKey("properties") && schema["properties"] is not JsonObject)
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
                ? typeArray.Select(node => (node as JsonValue)?.GetValue<string>())
                    .Where(value => value is not null).Select(value => value!).ToArray()
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
        if (schema["enum"] is JsonArray enumArray && enumArray.Any(node => node is null))
        {
            return true;
        }

        return schema["anyOf"] is JsonArray anyOf
            && anyOf.Any(node => node is JsonObject variant && SchemaAllowsNull(variant));
    }

    // ---------------------------------------------------------------------
    // function calling mode（pi google-shared.ts 469–495）
    // ---------------------------------------------------------------------

    private static string MapToolChoice(string choice)
        => choice switch
        {
            "auto" => "AUTO",
            "none" => "NONE",
            "any" => "ANY",
            _ => "AUTO",
        };

    /// <summary>none/any 直映射；strict 采样要求 VALIDATED；否则只有显式 toolChoice 才带 toolConfig。</summary>
    private static string? ResolveGoogleFunctionCallingMode(
        IReadOnlyList<Tool> tools, string? toolChoice, bool supportsStrictMode)
    {
        var useStrictMode = tools.Any(tool => ResolveJsonSchemaStrictSampling(tool, supportsStrictMode) == true);
        if (toolChoice is "none" or "any")
        {
            return MapToolChoice(toolChoice);
        }

        if (useStrictMode)
        {
            return "VALIDATED";
        }

        return toolChoice is not null ? MapToolChoice(toolChoice) : null;
    }

    // ---------------------------------------------------------------------
    // thinking 档位（pi google-shared.ts 36–113 + getGoogleBudget 426–466）
    // ---------------------------------------------------------------------

    /// <summary>Gemini 3+ 用离散 thinkingLevel 而非 token budget（pi usesGoogleThinkingLevel）。</summary>
    private static bool UsesGoogleThinkingLevel(Model model)
    {
        var id = model.Id.ToLowerInvariant();
        return Gemini3ProFlashRegex.IsMatch(id)
            || Gemini4PlusRegex.IsMatch(id)
            || id is "gemini-flash-latest" or "gemini-flash-lite-latest"
            || Gemma4Regex.IsMatch(id);
    }

    /// <summary>模型 id 的 Gemini 主版本号（无则 null）。</summary>
    private static int? GetGeminiMajorVersion(string modelId)
    {
        var match = GeminiMajorVersionRegex.Match(modelId.ToLowerInvariant());
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>经 Google API 的哪些模型要求 functionCall/functionResponse 显式 id。</summary>
    private static bool RequiresToolCallId(string modelId)
    {
        var geminiMajorVersion = GetGeminiMajorVersion(modelId);
        return modelId.StartsWith("claude-", StringComparison.Ordinal)
            || modelId.StartsWith("gpt-oss-", StringComparison.Ordinal)
            || (geminiMajorVersion is { } version && version >= 3);
    }

    /// <summary>是否支持多模态 functionResponse（Gemini 3+；非 Gemini 模型默认支持）。</summary>
    private static bool SupportsMultimodalFunctionResponse(string modelId)
    {
        if (GetGeminiMajorVersion(modelId) is { } version) return version >= 3;
        return true;
    }

    /// <summary>Gemini 3+ 在受验工具调用模式下要求必填参数。</summary>
    private static bool SupportsGoogleStrictToolSampling(string modelId)
        => GetGeminiMajorVersion(modelId) is { } version && version >= 3;

    private static string WireLevelName(ModelThinkingLevel level)
        => level switch
        {
            ModelThinkingLevel.Off => "off",
            ModelThinkingLevel.Minimal => "minimal",
            ModelThinkingLevel.Low => "low",
            ModelThinkingLevel.Medium => "medium",
            ModelThinkingLevel.High => "high",
            ModelThinkingLevel.XHigh => "xhigh",
            ModelThinkingLevel.Max => "max",
            _ => level.ToString().ToLowerInvariant(),
        };

    /// <summary>
    /// 解析模型档位映射为标准 Google 档位（minimal/low/medium/high）；
    /// 映射到其它值（含 off/xhigh/max）按 pi 抛 Unsupported（终态 Error）。
    /// </summary>
    private static string ResolveGoogleThinkingLevel(Model model, ModelThinkingLevel level)
    {
        var mapped = model.ThinkingLevelMap is { } map && map.TryGetValue(level, out var value)
            ? value
            : null;
        var resolved = mapped is not null ? mapped.ToLowerInvariant() : WireLevelName(level);
        return resolved is "minimal" or "low" or "medium" or "high"
            ? resolved
            : throw new InvalidOperationException(
                $"Unsupported Google thinking level mapping for {model.Provider}/{model.Id}: "
                + $"{WireLevelName(level)} -> {mapped ?? "null"}");
    }

    private static ThinkingLevel ParseGoogleLevel(string level)
        => level switch
        {
            "minimal" => ThinkingLevel.Minimal,
            "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            _ => throw new InvalidOperationException($"unknown level: {level}"),
        };

    /// <summary>pi 档位 → Google wire 档位（大写）；xhigh/max 无 Google wire 值，按 Unsupported 终态。</summary>
    private static string ToGoogleSdkThinkingLevel(ThinkingLevel level)
        => level switch
        {
            ThinkingLevel.Minimal => "MINIMAL",
            ThinkingLevel.Low => "LOW",
            ThinkingLevel.Medium => "MEDIUM",
            ThinkingLevel.High => "HIGH",
            _ => throw new InvalidOperationException($"Unsupported Google thinking level: {level}"),
        };

    /// <summary>禁用思考的 thinkingConfig：level 型走 fallback 档位，其余 thinkingBudget 0。</summary>
    private static JsonObject GetDisabledGoogleThinkingConfig(Model model)
    {
        if (!UsesGoogleThinkingLevel(model))
        {
            return new JsonObject { ["thinkingBudget"] = 0 };
        }

        var fallback = ModelOperations.ClampThinkingLevel(model, ModelThinkingLevel.Off);
        if (fallback == ModelThinkingLevel.Off)
        {
            return new JsonObject { ["thinkingBudget"] = 0 };
        }

        return new JsonObject
        {
            ["thinkingLevel"] = ToGoogleSdkThinkingLevel(
                ParseGoogleLevel(ResolveGoogleThinkingLevel(model, fallback))),
        };
    }

    /// <summary>token 预算表（2.5-pro / 2.5-flash-lite / 2.5-flash；其余 -1 动态）。pi getGoogleBudget。</summary>
    internal static int GetGoogleBudget(Model model, string level, ThinkingBudgets? customBudgets = null)
    {
        var custom = level switch
        {
            "minimal" => customBudgets?.Minimal,
            "low" => customBudgets?.Low,
            "medium" => customBudgets?.Medium,
            "high" => customBudgets?.High,
            _ => null,
        };
        if (custom is { } explicitBudget) return explicitBudget;

        if (model.Id.Contains("2.5-pro", StringComparison.Ordinal))
        {
            return level switch
            {
                "minimal" => 128,
                "low" => 2048,
                "medium" => 8192,
                "high" => 32768,
                _ => -1,
            };
        }

        // flash-lite 必须先于 flash 判定（"gemini-2.5-flash-lite" 含 "2.5-flash" 子串，pi TS 同序）。
        if (model.Id.Contains("2.5-flash-lite", StringComparison.Ordinal))
        {
            return level switch
            {
                "minimal" => 512,
                "low" => 2048,
                "medium" => 8192,
                "high" => 24576,
                _ => -1,
            };
        }

        if (model.Id.Contains("2.5-flash", StringComparison.Ordinal))
        {
            return level switch
            {
                "minimal" => 128,
                "low" => 2048,
                "medium" => 8192,
                "high" => 24576,
                _ => -1,
            };
        }

        return -1;
    }

    // ---------------------------------------------------------------------
    // thought part / 签名（pi google-shared.ts 115–162）
    // ---------------------------------------------------------------------

    /// <summary>thought:true 是思考内容的决定性标记；thoughtSignature 不代表该 part 是思考内容。</summary>
    private static bool IsThinkingPart(JsonObject part)
        => part["thought"] is JsonValue flag && flag.TryGetValue<bool>(out var thought) && thought;

    /// <summary>保留块内首个非空签名（后续 delta 可能省略；不跨 part 合并/搬移）。</summary>
    private static string? RetainThoughtSignature(string? existing, string? incoming)
        => !string.IsNullOrEmpty(incoming) ? incoming : existing;

    /// <summary>仅保留同 provider/model 且 base64 合法的签名（TYPE_BYTES）。</summary>
    private static string? ResolveThoughtSignature(bool isSameProviderAndModel, string? signature)
    {
        if (!isSameProviderAndModel) return null;
        if (string.IsNullOrEmpty(signature)) return null;
        if (signature.Length % 4 != 0) return null;
        return Base64SignaturePattern.IsMatch(signature) ? signature : null;
    }

    // ---------------------------------------------------------------------
    // mapStopReason（pi google-shared.ts 487–511）
    // ---------------------------------------------------------------------

    private static readonly HashSet<string> ErrorFinishReasons = new(StringComparer.Ordinal)
    {
        "BLOCKLIST", "PROHIBITED_CONTENT", "SPII", "SAFETY", "IMAGE_SAFETY", "IMAGE_PROHIBITED_CONTENT",
        "IMAGE_RECITATION", "IMAGE_OTHER", "RECITATION", "FINISH_REASON_UNSPECIFIED", "OTHER", "LANGUAGE",
        "MALFORMED_FUNCTION_CALL", "UNEXPECTED_TOOL_CALL", "TOO_MANY_TOOL_CALLS", "NO_IMAGE",
    };

    /// <summary>Gemini finishReason → StopReason；未知值抛 Unhandled（终态 Error，pi 同）。</summary>
    private static StopReason MapStopReason(string? reason)
        => reason switch
        {
            "STOP" => StopReason.Stop,
            "MAX_TOKENS" => StopReason.Length,
            _ when reason is not null && ErrorFinishReasons.Contains(reason) => StopReason.Error,
            _ => throw new InvalidOperationException($"Unhandled stop reason: {reason ?? "null"}"),
        };

    // ---------------------------------------------------------------------
    // 请求构造（pi createClient 342–363 + SDK 传输形状）
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

    private static FetchRequest BuildRequest(Model model, GoogleOptions? options, JsonNode payload, string apiKey)
    {
        // pi createClient：identity 头最底层，model.headers / options.Headers 依次覆盖。
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        if (options?.Headers is not null)
        {
            foreach (var entry in options.Headers)
            {
                if (entry.Value is null) headers.Remove(entry.Key);
                else headers[entry.Key] = entry.Value;
            }
        }

        // @google/genai SDK：apiKey 经 x-goog-api-key 头发送（非 query 参数）。
        headers["x-goog-api-key"] = apiKey;

        var fetchHeaders = new FetchHeaders();
        foreach (var entry in headers)
        {
            fetchHeaders[entry.Key] = entry.Value;
        }

        var baseUrl = string.IsNullOrEmpty(model.BaseUrl) ? DefaultBaseUrl : model.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/models/{model.Id}:streamGenerateContent?alt=sse";
        return new FetchRequest("POST", url, fetchHeaders, Encoding.UTF8.GetBytes(payload.ToJsonString()));
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

    // ---------------------------------------------------------------------
    // transcript 工具（pi utils/transcript.ts + utils/text.ts）
    // ---------------------------------------------------------------------

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

    private static IReadOnlyList<Message> WithoutInitialSystemMessage(IReadOnlyList<Message> messages)
        => messages.Count > 0 && messages[0] is SystemMessage ? messages.Skip(1).ToList() : messages.ToList();

    private static SystemMessage? GetInitialSystemMessage(IReadOnlyList<Message> messages)
        => messages.Count > 0 && messages[0] is SystemMessage initial ? initial : null;

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

    private static string ContentText(MessageContent content)
    {
        if (content is MessageContent.Text text) return text.Value;
        return string.Join("\n", content.ToBlocks().OfType<TextContent>().Select(block => block.Text));
    }

    private static string SanitizeSurrogates(string text) => UnpairedSurrogateRegex.Replace(text, string.Empty);

    // ---------------------------------------------------------------------
    // simple options（pi simple-options.ts buildBaseOptions + streamSimple 299–341）
    // ---------------------------------------------------------------------

    private static GoogleOptions BuildSimpleOptions(
        Model model, TranscriptContext context, SimpleStreamOptions? options)
    {
        var clampedReasoning = options?.Reasoning is { } reasoning
            ? ModelOperations.ClampThinkingLevel(model, ToModelThinkingLevel(reasoning))
            : (ModelThinkingLevel?)null;

        var baseOptions = new GoogleOptions
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
            // pi buildBaseOptions：观察钩子必须透传，否则 simple 路径静默丢钩子。
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
            ToolChoice = options?.ToolChoice switch
            {
                ToolChoice.Auto => (JsonNode?)"auto",
                ToolChoice.None => "none",
                _ => null,
            },
        };

        // pi streamSimple：无 reasoning 或 clamp 后 off → thinking disabled。
        if (clampedReasoning is null or ModelThinkingLevel.Off)
        {
            return baseOptions with { Thinking = new GoogleThinkingOptions { Enabled = false } };
        }

        if (UsesGoogleThinkingLevel(model))
        {
            return baseOptions with
            {
                Thinking = new GoogleThinkingOptions
                {
                    Enabled = true,
                    Level = ParseGoogleLevel(ResolveGoogleThinkingLevel(model, clampedReasoning.Value)),
                },
            };
        }

        return baseOptions with
        {
            Thinking = new GoogleThinkingOptions
            {
                Enabled = true,
                BudgetTokens = GetGoogleBudget(
                    model, ResolveGoogleThinkingLevel(model, clampedReasoning.Value), options?.ThinkingBudgets),
            },
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

    private static int ClampMaxTokensToContext(Model model, TranscriptContext context, int maxTokens)
    {
        if (model.ContextWindow <= 0) return Math.Max(1, maxTokens);
        var available = model.ContextWindow - (int)EstimateContextTokens(context.Messages) - 4096;
        return Math.Min(maxTokens, Math.Max(1, available));
    }

    private static double EstimateContextTokens(IReadOnlyList<Message> messages)
    {
        var usageInfo = GetLastAssistantUsageInfo(messages);
        if (usageInfo is { } usage)
        {
            var usageTokens = usage.Usage.TotalTokens > 0
                ? usage.Usage.TotalTokens
                : usage.Usage.Input + usage.Usage.Output + usage.Usage.CacheRead + usage.Usage.CacheWrite;
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

    private static (int Index, Usage Usage)? GetLastAssistantUsageInfo(IReadOnlyList<Message> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantMessage assistant && assistant.Usage is { } usage)
            {
                return (i, usage);
            }
        }

        return null;
    }

    private static double EstimateMessageTokens(Message message)
    {
        switch (message)
        {
            case SystemMessage system:
                return EstimateTextTokens(GetSystemMessageText(system)) + EstimateToolsTokens(system.ToolsAdded);
            case UserMessage user:
                return EstimateContentTokens(user.Content);
            case ToolResultMessage toolResult:
                return EstimateContentTokens(MessageContent.From(toolResult.Content));
            case AssistantMessage assistant:
            {
                var tokens = 0d;
                foreach (var block in assistant.Content)
                {
                    switch (block)
                    {
                        case TextContent text:
                            tokens += EstimateTextTokens(text.Text);
                            break;
                        case ThinkingContent thinking:
                            tokens += EstimateTextTokens(thinking.Thinking);
                            break;
                        case ToolCall toolCall:
                            tokens += EstimateTextTokens(toolCall.Name)
                                + EstimateTextTokens(toolCall.Arguments.ToJsonString());
                            break;
                    }
                }

                return tokens;
            }

            default:
                return 0;
        }
    }

    private static double EstimateContentTokens(MessageContent content)
    {
        var tokens = 0d;
        foreach (var block in content.ToBlocks())
        {
            switch (block)
            {
                case TextContent text:
                    tokens += EstimateTextTokens(text.Text);
                    break;
                case ImageContent:
                    tokens += Math.Ceiling(EstimatedImageChars / CharsPerToken);
                    break;
            }
        }

        return tokens;
    }

    private static double EstimateTextTokens(string text) => Math.Ceiling(text.Length / CharsPerToken);

    private static double EstimateToolsTokens(IReadOnlyList<Tool>? tools)
    {
        if (tools is null) return 0;
        var tokens = 0d;
        foreach (var tool in tools)
        {
            tokens += EstimateTextTokens(tool.Name)
                + EstimateTextTokens(tool.Description)
                + EstimateTextTokens(tool.Parameters.ToJsonString());
        }

        return tokens;
    }

    // ---------------------------------------------------------------------
    // 错误格式化（与 Anthropic/OpenAI 家族同形）
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

        if (body is { } errorBody)
        {
            var trimmed = errorBody.Trim();
            if (trimmed.Length > 0 && status is { } statusCode
                && !message.Contains(trimmed, StringComparison.Ordinal))
            {
                message = $"{statusCode}: {TruncateErrorText(trimmed)}";
            }
        }

        return message;
    }

    private static string TruncateErrorText(string text)
    {
        if (text.Length <= MaxProviderErrorBodyChars) return text;
        return $"{text[..MaxProviderErrorBodyChars]}... [truncated {text.Length - MaxProviderErrorBodyChars} chars]";
    }

    // ---------------------------------------------------------------------
    // 流外可变输出（pi.net MutableAssistantMessage 等价；S3 AssistantMessage 是 record）
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
                Timestamp = Timestamp,
            };
    }

    // ---------------------------------------------------------------------
    // JSON 读取小工具
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
}
