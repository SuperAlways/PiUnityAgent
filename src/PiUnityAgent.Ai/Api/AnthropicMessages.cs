using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Api;

/// <summary>
/// pi <c>AnthropicMessagesCompat</c> / <c>getAnthropicCompat</c>（anthropic-messages.ts 225–240 +
/// buildParams 直读字段）的 C# 形状；从 <see cref="Model"/>.Compat JSON 解析。
/// </summary>
internal sealed record AnthropicMessagesCompat
{
    public bool SupportsEagerToolInputStreaming { get; init; } = true;
    public bool SupportsLongCacheRetention { get; init; } = true;
    public bool SendSessionAffinityHeaders { get; init; }
    public string? SessionAffinityFormat { get; init; }
    public bool SupportsCacheControlOnTools { get; init; } = true;
    public bool SupportsTemperature { get; init; } = true;
    public bool AllowEmptySignature { get; init; }
    public bool SupportsStrictTools { get; init; }
    public bool SupportsMidConvoSystemMessages { get; init; }
    public bool SupportsMidConvoToolChanges { get; init; }
    /// <summary>服务端管理 adaptive effort（中段 output_config）；pi 直读 compat.supportsMidConvoEffort。</summary>
    public bool SupportsMidConvoEffort { get; init; }
    /// <summary>只支持 adaptive thinking；pi 直读 compat.forceAdaptiveThinking。</summary>
    public bool ForceAdaptiveThinking { get; init; }
    /// <summary>服务端 fallback 列表（原始 JSON，逐项读 model 字段）；pi 直读 compat.allowedFallbackModels。</summary>
    public JsonArray? AllowedFallbackModels { get; init; }
}

/// <summary>
/// Anthropic Messages 协议适配器（pi <c>api/anthropic-messages.ts</c> 移植）：
/// system 独立字段、content 块协议（thinking / redacted_thinking / tool_use / tool_result / image）、
/// beta 头、cache_control、adaptive/budget thinking、中段 system 与工具变更、OAuth Bearer、
/// 命名 SSE 事件状态机（message_start / content_block_* / message_delta / message_stop）。
/// 请求走注入的 <see cref="FetchFunction"/>（REST <c>POST /v1/messages</c>）。
/// </summary>
public static class AnthropicMessages
{
    private const string ApiVersion = "2023-06-01";

    // Stealth mode: mimic Claude Code's tool naming exactly.
    private const string ClaudeCodeVersion = "2.1.280";

    private static readonly string[] ClaudeCodeTools =
    {
        "Read", "Write", "Edit", "Bash", "Grep", "Glob", "AskUserQuestion",
        "EnterPlanMode", "ExitPlanMode", "KillShell", "NotebookEdit", "Skill",
        "Task", "TaskOutput", "TodoWrite", "WebFetch", "WebSearch",
    };

    private static readonly Dictionary<string, string> ClaudeCodeToolLookup = ClaudeCodeTools.ToDictionary(
        name => name.ToLowerInvariant(), name => name);

    private const string FineGrainedToolStreamingBeta = "fine-grained-tool-streaming-2025-05-14";
    private const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";
    private const string ServerSideFallbackBeta = "server-side-fallback-2026-07-01";
    private const string MidConversationOutputConfigBeta = "mid-conversation-output-config-2026-07-01";
    private const string ThinkingBindingControlsBeta = "thinking-binding-controls-2026-08-01";
    private const string InlineToolsBeta = "inline-tools-2026-09-15";

    // Keywords Anthropic strict tool use rejects with a 400 for the whole request.
    // https://platform.claude.com/docs/en/build-with-claude/structured-outputs#json-schema-limitations
    private static readonly HashSet<string> AnthropicStrictUnsupportedKeywords = new()
    {
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "maxItems", "uniqueItems", "minContains", "maxContains", "minProperties", "maxProperties",
    };

    private static readonly HashSet<string> AnthropicStrictStringFormats = new()
    {
        "date-time", "time", "date", "duration", "email", "hostname", "uri", "ipv4", "ipv6", "uuid",
    };

    private static readonly Regex UnpairedSurrogateRegex = new(
        @"[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]",
        RegexOptions.Compiled);

    private const int MaxProviderErrorBodyChars = 4000;
    private const int MaxNormalizedToolCallIdLength = 64;
    private const int MinAnswerTokens = 1024;

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
        AnthropicOptions? options = null)
        => Run(model, context, options);

    public static IAssistantMessageEventStream StreamSimple(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options = null)
        => Run(model, context, BuildSimpleOptions(model, context, options));

    /// <summary>
    /// 基类 <see cref="StreamOptions"/>（门面 <c>Models.Stream</c> 经 applyAuth 产出）升格为本协议专属选项：
    /// 逐字段显式拷贝全部基类字段（ApiKey/Fetch/CancellationToken/Headers/hooks…），协议专属字段保持 null。
    /// 已是 <see cref="AnthropicOptions"/> 则原样返回；直转丢失认证正是本方法的由来。
    /// </summary>
    private static AnthropicOptions? ToAnthropicOptions(StreamOptions? options)
    {
        if (options is null) return null;
        if (options is AnthropicOptions anthropic) return anthropic;
        return new AnthropicOptions
        {
            ApiKey = options.ApiKey,
            MaxTokens = options.MaxTokens,
            Temperature = options.Temperature,
            SessionId = options.SessionId,
            Headers = options.Headers,
            SamplingParams = options.SamplingParams,
            TimeoutMs = options.TimeoutMs,
            MaxRetries = options.MaxRetries,
            MaxRetryDelayMs = options.MaxRetryDelayMs,
            CancellationToken = options.CancellationToken,
            Fetch = options.Fetch,
            Identity = options.Identity,
            Metadata = options.Metadata,
            Env = options.Env,
            CacheRetention = options.CacheRetention,
            Transport = options.Transport,
            WebsocketConnectTimeoutMs = options.WebsocketConnectTimeoutMs,
            Telemetry = options.Telemetry,
            TransformHeaders = options.TransformHeaders,
            OnPayload = options.OnPayload,
            OnResponse = options.OnResponse,
            OnProviderStreamEvent = options.OnProviderStreamEvent,
        };
    }

    // ---------------------------------------------------------------------
    // stream（pi streamSimple + stream 的请求半程；SSE 半程归 Task 3）
    // ---------------------------------------------------------------------

    private static IAssistantMessageEventStream Run(Model model, TranscriptContext context, StreamOptions? options)
    {
        var stream = new AssistantMessageEventStream();
        var compat = GetCompat(model);
        var anthropicOptions = ToAnthropicOptions(options);
        var normalizedContext = ResolveTranscript(context, compat.SupportsMidConvoSystemMessages);

        _ = RunCoreAsync(stream, model, normalizedContext, anthropicOptions, compat);
        return stream;
    }

    private static async Task RunCoreAsync(
        AssistantMessageEventStream stream,
        Model model,
        TranscriptContext normalizedContext,
        AnthropicOptions? options,
        AnthropicMessagesCompat compat)
    {
        var blocks = new List<StreamBlock>();
        var output = new MutableOutput(model);
        try
        {
            // pi assertRequestAuth：key 或请求头自带认证（authorization/x-api-key/cf-aig-authorization）。
            // 注意：头自带认证时 apiKey 保持 null，绝不伪造占位 key 去覆盖用户头。
            var apiKey = options?.ApiKey;
            if (string.IsNullOrEmpty(apiKey)
                && !HasHeader(options?.Headers, "authorization")
                && !HasHeader(options?.Headers, "x-api-key")
                && !HasHeader(options?.Headers, "cf-aig-authorization"))
            {
                throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            }

            var isOAuth = IsOAuthToken(apiKey);
            var cacheRetention = ResolveCacheRetention(options?.CacheRetention, options?.Env);
            var currentTools = GetCurrentTools(normalizedContext.Messages);

            var fetch = options?.Fetch;
            if (fetch is null)
            {
                throw new InvalidOperationException(
                    "No fetch transport configured; set options.Fetch to enable provider requests.");
            }

            var signal = options?.CancellationToken ?? default;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(signal);
            var fetchOptions = BuildFetchOptions(options?.TimeoutMs);

            // pi stream 649–691：Anthropic 会用 400 拒绝「strict 工具集超出容量」的请求。这类拒绝
            // 只跟 strict 有关：先按原样发一次，命中后关掉 prefer 工具的 strict 再发一次（require
            // 不在此列）；流一旦建立绝不重发——只降级「被拒绝的请求」。
            var disablePreferredStrictTools = false;
            IFetchResponse response;
            while (true)
            {
                signal.ThrowIfCancellationRequested();
                var built = BuildParamsForRequest(
                    model, normalizedContext, isOAuth, options, compat, disablePreferredStrictTools);
                // pi anthropic-messages.ts:654：effort-message 模式的当前 effort 记到回复快照，
                // 下一回合 ConvertMessagesCore 靠它重放历史 effort 标记。只在有值时覆盖（pi truthy），
                // 无 effort 模式不得把已有值抹成空。
                if (built.ProviderThinkingLevel is not null)
                {
                    output.ProviderThinkingLevel = built.ProviderThinkingLevel;
                }
                JsonNode payload = built.Params;
                if (options?.OnPayload is { } onPayload)
                {
                    var next = await onPayload(payload, model).ConfigureAwait(false);
                    if (next is not null)
                    {
                        payload = next;
                    }

                    payload["stream"] = true;
                }

                var request = BuildRequest(
                    model, options, payload, apiKey, isOAuth, compat, cacheRetention);
                try
                {
                    response = await ProviderRetry.RunAsync(
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
                    break;
                }
                catch (FetchException error) when (
                    !signal.IsCancellationRequested
                    && !disablePreferredStrictTools
                    && built.HasPreferredStrictTools
                    && IsStrictToolLimitRejection(error))
                {
                    disablePreferredStrictTools = true;
                    output.Diagnostics.Add(new AssistantMessageDiagnostic
                    {
                        Type = "anthropic_strict_tools_fallback",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Error = new DiagnosticErrorInfo { Message = StrictRejectionMessage(error) },
                        Details = new JsonObject { ["reason"] = "strict_tool_limits_exceeded" },
                    });
                }
            }

            await using var responseScope = response;
            if (options?.OnResponse is { } onResponse)
            {
                await onResponse(
                    new ProviderResponse(response.StatusCode, HeadersToRecord(response.Headers)),
                    model).ConfigureAwait(false);
            }

            // pi stream:693：start 在事件消费之前推。
            stream.Push(new AssistantMessageEvent.Start(output.Snapshot(blocks)));

            var inputTransformations = await ProcessAnthropicStream(
                response.Body, output, blocks, stream, model, compat, currentTools, isOAuth,
                options?.OnProviderStreamEvent, lifetime.Token).ConfigureAwait(false);

            if (signal.IsCancellationRequested)
            {
                throw new OperationCanceledException("Request was aborted", signal);
            }

            if (output.StopReason == StopReason.Pending)
            {
                throw new InvalidOperationException("Anthropic stream ended without a stop reason");
            }

            if (output.StopReason is StopReason.Aborted or StopReason.Error)
            {
                throw new InvalidOperationException(output.ErrorMessage ?? "An unknown error occurred");
            }

            if (inputTransformations is { Count: > 0 })
            {
                output.Diagnostics.Add(BuildInputTransformationsDiagnostic(inputTransformations));
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
    // SSE 状态机（pi iterateSseMessages/iterateAnthropicEvents 477–576 + stream 695–916）
    // ---------------------------------------------------------------------

    /// <summary>pi ANTHROPIC_MESSAGE_EVENTS：只有这六个命名事件的 data 才进状态机。</summary>
    private static readonly HashSet<string> AnthropicMessageEvents = new()
    {
        "message_start",
        "message_delta",
        "message_stop",
        "content_block_start",
        "content_block_delta",
        "content_block_stop",
    };

    /// <summary>pi constrained-sampling.ts 152–156：strict 容量拒绝的三条消息。</summary>
    private static readonly string[] StrictToolLimitErrorMessages =
    {
        "The compiled grammar is too large",
        "Too many strict tools (",
        "Schemas contains too many parameters with union types (",
    };

    internal static bool IsStrictToolLimitErrorMessage(string message)
        => StrictToolLimitErrorMessages.Any(limit => message.Contains(limit, StringComparison.Ordinal));

    /// <summary>
    /// Anthropic 的错误体是 <c>{"type":"error","error":{"type":"invalid_request_error","message":"…"}}</c>；
    /// 400 + invalid_request_error + strict 容量消息才降级重发（pi isStrictToolLimitRejection）。
    /// </summary>
    internal static bool IsStrictToolLimitRejection(FetchException error)
    {
        if (error.StatusCode != 400 || string.IsNullOrEmpty(error.ErrorBody))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(error.ErrorBody) is not JsonObject parsed
                || parsed["error"] is not JsonObject inner)
            {
                return false;
            }

            return JsonStr(inner, "type") == "invalid_request_error"
                && IsStrictToolLimitErrorMessage(JsonStr(inner, "message") ?? error.Message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>诊断里的错误消息取错误体内层 message（pi SDK APIError.message 同源）；解析不出用整体消息。</summary>
    private static string StrictRejectionMessage(FetchException error)
    {
        if (error.ErrorBody is { Length: > 0 } body)
        {
            try
            {
                if (JsonNode.Parse(body) is JsonObject parsed
                    && parsed["error"] is JsonObject inner
                    && JsonStr(inner, "message") is { Length: > 0 } message)
                {
                    return message;
                }
            }
            catch (JsonException)
            {
            }
        }

        return error.Message;
    }

    /// <summary>
    /// 消费 Anthropic 命名 SSE 流：content_block.index → 统一 contentIndex 槽位；
    /// thinking 签名回填；tool 参数用 <see cref="ToolCallArguments"/> 在块结束时一次解析。
    /// 返回 message_start/message_delta 带上的 input_transformations（无则 null）。
    /// </summary>
    private static async Task<JsonArray?> ProcessAnthropicStream(
        Stream body,
        MutableOutput output,
        List<StreamBlock> blocks,
        AssistantMessageEventStream stream,
        Model model,
        AnthropicMessagesCompat compat,
        IReadOnlyList<Tool> currentTools,
        bool isOAuth,
        Func<JsonNode, ProviderModel, Task>? onProviderStreamEvent,
        CancellationToken cancellationToken)
    {
        var sawMessageStart = false;
        var sawMessageStop = false;
        var usageModel = model;
        JsonArray? inputTransformations = null;
        // pi openBlocks：Anthropic content_block index → 统一 contentIndex 槽位；
        // 流状态（tool call 参数累积器等）留在槽上，永不进入 partial。
        var openBlocks = new Dictionary<int, AnthropicBlockSlot>();

        await foreach (var sse in SseEventReader.ReadAsync(body, cancellationToken).ConfigureAwait(false))
        {
            if (sse.Event == "error")
            {
                // 命名 error 事件 = 服务端中途故障，直接抛给统一 catch（pi iterateAnthropicEvents）。
                throw new InvalidOperationException(sse.Data);
            }

            if (sse.Event is null || !AnthropicMessageEvents.Contains(sse.Event))
            {
                // ping / 未知命名事件照 pi 静默跳过。
                continue;
            }

            if (JsonNode.Parse(sse.Data) is not JsonObject @event)
            {
                throw new InvalidOperationException(
                    $"Could not parse Anthropic SSE event {sse.Event}: data is not a JSON object; data={sse.Data}");
            }

            if (onProviderStreamEvent is { } observer)
            {
                await observer(@event, model).ConfigureAwait(false);
            }

            switch (JsonStr(@event, "type"))
            {
                case "message_start":
                {
                    sawMessageStart = true;
                    var message = JsonObj(@event, "message");
                    output.ResponseId = JsonStr(message, "id");
                    if (message?["input_transformations"] is JsonArray transformations)
                    {
                        inputTransformations = (JsonArray)transformations.DeepClone();
                    }

                    var responseModel = JsonStr(message, "model");
                    if (responseModel is not null && responseModel != model.Id)
                    {
                        // 服务端 fallback 模型接手：计价改用 compat.allowedFallbackModels 里该模型的 cost。
                        output.ResponseModel = responseModel;
                        usageModel = model;
                        foreach (var fallback in compat.AllowedFallbackModels ?? new JsonArray())
                        {
                            if (fallback is not JsonObject entry
                                || JsonStr(entry, "provider") != model.Provider
                                || JsonStr(entry, "model") != responseModel
                                || entry["cost"] is not JsonObject cost)
                            {
                                continue;
                            }

                            usageModel = model with
                            {
                                Id = responseModel,
                                Cost = new ModelCost(
                                    JsonNum(cost, "input") ?? 0,
                                    JsonNum(cost, "output") ?? 0,
                                    JsonNum(cost, "cacheRead") ?? 0,
                                    JsonNum(cost, "cacheWrite") ?? 0),
                            };
                            break;
                        }
                    }

                    // message_start 就记初值：流早停（中止/截断）时 input/cache 计数仍可用。
                    if (JsonObj(message, "usage") is { } usage)
                    {
                        var cacheCreation = JsonObj(usage, "cache_creation");
                        var input = (int)(JsonNum(usage, "input_tokens") ?? 0);
                        var outputTokens = (int)(JsonNum(usage, "output_tokens") ?? 0);
                        var cacheRead = (int)(JsonNum(usage, "cache_read_input_tokens") ?? 0);
                        var cacheWrite = (int)(JsonNum(usage, "cache_creation_input_tokens") ?? 0);
                        output.Usage = new Usage
                        {
                            Input = input,
                            Output = outputTokens,
                            CacheRead = cacheRead,
                            CacheWrite = cacheWrite,
                            CacheWrite1h = (int)(JsonNum(cacheCreation, "ephemeral_1h_input_tokens") ?? 0),
                            TotalTokens = input + outputTokens + cacheRead + cacheWrite,
                        };
                        output.Usage = output.Usage with
                        {
                            Cost = ModelOperations.CalculateCost(usageModel, output.Usage),
                        };
                    }

                    break;
                }

                case "content_block_start":
                {
                    var wireIndex = ReadIndex(@event);
                    if (JsonObj(@event, "content_block") is not { } contentBlock
                        || JsonStr(contentBlock, "type") is not { } blockType)
                    {
                        break;
                    }

                    if (blockType == "fallback")
                    {
                        if (blocks.Count > 0)
                        {
                            throw new InvalidOperationException(
                                "Anthropic performed an unsupported mid-output model fallback");
                        }

                        break;
                    }

                    switch (blockType)
                    {
                        case "text":
                        {
                            var textBlock = new TextStreamBlock { Text = JsonStr(contentBlock, "text") ?? "" };
                            blocks.Add(textBlock);
                            openBlocks[wireIndex] = new AnthropicBlockSlot
                            {
                                ContentIndex = blocks.Count - 1,
                                Text = textBlock,
                            };
                            stream.Push(new AssistantMessageEvent.TextStart(
                                blocks.Count - 1, output.Snapshot(blocks)));
                            break;
                        }

                        case "thinking":
                        {
                            var thinkingBlock = new ThinkingStreamBlock
                            {
                                Thinking = JsonStr(contentBlock, "thinking") ?? "",
                                ThinkingSignature = JsonStr(contentBlock, "signature") ?? "",
                            };
                            blocks.Add(thinkingBlock);
                            openBlocks[wireIndex] = new AnthropicBlockSlot
                            {
                                ContentIndex = blocks.Count - 1,
                                Thinking = thinkingBlock,
                            };
                            stream.Push(new AssistantMessageEvent.ThinkingStart(
                                blocks.Count - 1, output.Snapshot(blocks)));
                            break;
                        }

                        case "redacted_thinking":
                        {
                            var redactedBlock = new ThinkingStreamBlock
                            {
                                Thinking = "[Reasoning redacted]",
                                ThinkingSignature = JsonStr(contentBlock, "data") ?? "",
                                Redacted = true,
                            };
                            blocks.Add(redactedBlock);
                            openBlocks[wireIndex] = new AnthropicBlockSlot
                            {
                                ContentIndex = blocks.Count - 1,
                                Thinking = redactedBlock,
                            };
                            stream.Push(new AssistantMessageEvent.ThinkingStart(
                                blocks.Count - 1, output.Snapshot(blocks)));
                            break;
                        }

                        case "tool_use":
                        {
                            var name = JsonStr(contentBlock, "name") ?? "";
                            var toolBlock = new ToolCallStreamBlock
                            {
                                Id = JsonStr(contentBlock, "id") ?? "",
                                Name = isOAuth ? FromClaudeCodeName(name, currentTools) : name,
                                Arguments = contentBlock["input"] as JsonObject is { } input
                                    ? (JsonObject)input.DeepClone()
                                    : new JsonObject(),
                            };
                            blocks.Add(toolBlock);
                            openBlocks[wireIndex] = new AnthropicBlockSlot
                            {
                                ContentIndex = blocks.Count - 1,
                                ToolCall = toolBlock,
                                JsonArguments = new ToolCallArguments(),
                            };
                            stream.Push(new AssistantMessageEvent.ToolCallStart(
                                blocks.Count - 1, output.Snapshot(blocks)));
                            break;
                        }
                    }

                    break;
                }

                case "content_block_delta":
                {
                    var wireIndex = ReadIndex(@event);
                    if (!openBlocks.TryGetValue(wireIndex, out var slot))
                    {
                        break;
                    }

                    var delta = JsonObj(@event, "delta");
                    switch (JsonStr(delta, "type"))
                    {
                        case "text_delta" when slot.Text is not null:
                        {
                            var text = JsonStr(delta, "text") ?? "";
                            slot.Text.Text += text;
                            stream.Push(new AssistantMessageEvent.TextDelta(
                                slot.ContentIndex, text, output.Snapshot(blocks)));
                            break;
                        }

                        case "thinking_delta" when slot.Thinking is not null:
                        {
                            var thinking = JsonStr(delta, "thinking") ?? "";
                            slot.Thinking.Thinking += thinking;
                            stream.Push(new AssistantMessageEvent.ThinkingDelta(
                                slot.ContentIndex, thinking, output.Snapshot(blocks)));
                            break;
                        }

                        case "input_json_delta" when slot.ToolCall is not null && slot.JsonArguments is not null:
                        {
                            var jsonDelta = JsonStr(delta, "partial_json") ?? "";
                            // pi anthropic-messages.ts:795：append 的 partial 参数回填块，partial 消息可见。
                            slot.ToolCall.Arguments = slot.JsonArguments.Append(jsonDelta);
                            stream.Push(new AssistantMessageEvent.ToolCallDelta(
                                slot.ContentIndex, jsonDelta, output.Snapshot(blocks)));
                            break;
                        }

                        case "signature_delta" when slot.Thinking is not null:
                        {
                            // pi 不为签名回填推流事件；只回填块上的 thinkingSignature。
                            slot.Thinking.ThinkingSignature += JsonStr(delta, "signature") ?? "";
                            break;
                        }
                    }

                    break;
                }

                case "content_block_stop":
                {
                    var wireIndex = ReadIndex(@event);
                    if (!openBlocks.Remove(wireIndex, out var slot))
                    {
                        break;
                    }

                    if (slot.Text is not null)
                    {
                        stream.Push(new AssistantMessageEvent.TextEnd(
                            slot.ContentIndex, slot.Text.Text, output.Snapshot(blocks)));
                    }
                    else if (slot.Thinking is not null)
                    {
                        stream.Push(new AssistantMessageEvent.ThinkingEnd(
                            slot.ContentIndex, slot.Thinking.Thinking, output.Snapshot(blocks)));
                    }
                    else if (slot.ToolCall is not null && slot.JsonArguments is not null)
                    {
                        var block = slot.ToolCall;
                        // pi anthropic-messages.ts:830 Object.assign(block, arguments.finish())：
                        // 非法参数 JSON 不失败流，落 argumentsError（pi parseToolCallArguments）。
                        var (arguments, argumentsError) = slot.JsonArguments.Finish();
                        block.Arguments = arguments;
                        block.ArgumentsError = argumentsError;

                        stream.Push(new AssistantMessageEvent.ToolCallEnd(
                            slot.ContentIndex, block.ToToolCall(), output.Snapshot(blocks)));
                    }

                    break;
                }

                case "message_delta":
                {
                    if (@event["input_transformations"] is JsonArray deltaTransformations)
                    {
                        inputTransformations = (JsonArray)deltaTransformations.DeepClone();
                    }

                    var delta = JsonObj(@event, "delta");
                    // pi anthropic-messages.ts:841：`if (event.delta.stop_reason)` 真值判断，空串视为未上报。
                    if (JsonStr(delta, "stop_reason") is { Length: > 0 } rawStopReason)
                    {
                        output.RawStopReason = rawStopReason;
                        var (mapped, errorMessage) = MapStopReason(
                            rawStopReason, JsonStr(JsonObj(delta, "stop_details"), "explanation"));
                        output.StopReason = mapped;
                        if (errorMessage is not null)
                        {
                            output.ErrorMessage = errorMessage;
                        }
                    }

                    var deltaUsage = JsonObj(@event, "usage");
                    if (deltaUsage is not null)
                    {
                        // 只更新带上来的字段：代理可能在 message_delta 省略 input/cache，message_start 的值站住。
                        var current = output.Usage;
                        var input = current.Input;
                        var outputTokens = current.Output;
                        var cacheRead = current.CacheRead;
                        var cacheWrite = current.CacheWrite;
                        var cacheWrite1h = current.CacheWrite1h;
                        var reasoning = current.Reasoning;
                        if (deltaUsage["input_tokens"] is not null)
                        {
                            input = (int)(JsonNum(deltaUsage, "input_tokens") ?? 0);
                        }

                        if (deltaUsage["output_tokens"] is not null)
                        {
                            outputTokens = (int)(JsonNum(deltaUsage, "output_tokens") ?? 0);
                        }

                        if (deltaUsage["cache_read_input_tokens"] is not null)
                        {
                            cacheRead = (int)(JsonNum(deltaUsage, "cache_read_input_tokens") ?? 0);
                        }

                        if (deltaUsage["cache_creation_input_tokens"] is not null)
                        {
                            cacheWrite = (int)(JsonNum(deltaUsage, "cache_creation_input_tokens") ?? 0);
                        }

                        // Vercel AI Gateway 等会把 TTL 细分放进 delta usage（SDK 类型只在 message_start 有）。
                        var cacheCreation = JsonObj(deltaUsage, "cache_creation");
                        if (cacheCreation?["ephemeral_1h_input_tokens"] is not null)
                        {
                            cacheWrite1h = (int)(JsonNum(cacheCreation, "ephemeral_1h_input_tokens") ?? 0);
                        }

                        // Anthropic 把 reasoning tokens 报成 output 的子集。
                        var thinkingTokens = JsonNum(JsonObj(deltaUsage, "output_tokens_details"), "thinking_tokens");
                        if (thinkingTokens is not null)
                        {
                            reasoning = (int)thinkingTokens;
                        }

                        output.Usage = new Usage
                        {
                            Input = input,
                            Output = outputTokens,
                            CacheRead = cacheRead,
                            CacheWrite = cacheWrite,
                            CacheWrite1h = cacheWrite1h,
                            Reasoning = reasoning,
                        };
                    }

                    // Anthropic 不报 total_tokens，按分量合计；随后按（可能切换过的）计价模型算成本。
                    output.Usage = output.Usage with
                    {
                        TotalTokens = output.Usage.Input + output.Usage.Output
                            + output.Usage.CacheRead + output.Usage.CacheWrite,
                    };
                    output.Usage = output.Usage with
                    {
                        Cost = ModelOperations.CalculateCost(usageModel, output.Usage),
                    };

                    // OpenRouter 在这条 message_delta 的 usage 里附带实际账户计费（cost/is_byok/cost_details
                    // ——OpenAPI schema 与 Anthropic SDK 均不声明，只能从原始 JSON 读）。命中即用实付覆盖
                    // 目录价总价，并标记 totalFromProvider（pi anthropic-messages.ts:881–888 →
                    // openrouter-cost.ts applyOpenRouterReportedCost）。
                    if (deltaUsage is not null && IsOpenRouterModel(model))
                    {
                        output.Usage = ApplyOpenRouterReportedCost(output.Usage, deltaUsage);
                    }

                    break;
                }

                case "message_stop":
                    sawMessageStop = true;
                    break;
            }
        }

        if (sawMessageStart && !sawMessageStop)
        {
            throw new InvalidOperationException("Anthropic stream ended before message_stop");
        }

        return inputTransformations;
    }

    /// <summary>Anthropic content_block index（缺省 -1：没有槽位命中，delta 被忽略）。</summary>
    private static int ReadIndex(JsonObject @event)
        => JsonNum(@event, "index") is { } index ? (int)index : -1;

    /// <summary>OAuth 隐身模式：响应阶段的 tool_use 名按声明工具表还原原名（pi fromClaudeCodeName）。</summary>
    private static string FromClaudeCodeName(string name, IReadOnlyList<Tool> tools)
    {
        if (tools.Count > 0)
        {
            var matched = tools.FirstOrDefault(tool =>
                string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return matched.Name;
            }
        }

        return name;
    }

    private static AssistantMessageDiagnostic BuildInputTransformationsDiagnostic(JsonArray transformations)
    {
        var items = new JsonArray();
        foreach (var transformation in transformations)
        {
            if (transformation is not JsonObject entry)
            {
                continue;
            }

            var item = new JsonObject();
            if (JsonStr(entry, "type") is { } type) item["type"] = type;
            if (JsonStr(entry, "path") is { } path) item["path"] = path;
            if (JsonStr(entry, "reason") is { } reason) item["reason"] = reason;
            items.Add(item);
        }

        return new AssistantMessageDiagnostic
        {
            Type = "anthropic_input_transformations",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Details = new JsonObject { ["transformations"] = items },
        };
    }

    // ---------------------------------------------------------------------
    // 流内块模型（pi openBlocks 槽位载荷 = text | thinking | toolCall）
    // ---------------------------------------------------------------------

    private abstract class StreamBlock
    {
        public abstract ContentBlock ToContentBlock();
    }

    private sealed class TextStreamBlock : StreamBlock
    {
        public string Text { get; set; } = "";

        public override ContentBlock ToContentBlock() => new TextContent { Text = Text };
    }

    private sealed class ThinkingStreamBlock : StreamBlock
    {
        public string Thinking { get; set; } = "";
        public string ThinkingSignature { get; set; } = "";
        public bool Redacted { get; set; }

        public override ContentBlock ToContentBlock()
            => new ThinkingContent
            {
                Thinking = Thinking,
                ThinkingSignature = ThinkingSignature.Length > 0 ? ThinkingSignature : null,
                Redacted = Redacted ? true : null,
            };
    }

    private sealed class ToolCallStreamBlock : StreamBlock
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public JsonObject Arguments { get; set; } = new();
        public string? ArgumentsError { get; set; }

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

    /// <summary>pi openBlocks 槽位：Anthropic content_block index → 统一 contentIndex + 块载荷 + 流状态。</summary>
    private sealed class AnthropicBlockSlot
    {
        public int ContentIndex { get; init; }
        public TextStreamBlock? Text { get; init; }
        public ThinkingStreamBlock? Thinking { get; init; }
        public ToolCallStreamBlock? ToolCall { get; init; }

        /// <summary>tool_use 的 JSON 参数累积器（input_json_delta）。</summary>
        public ToolCallArguments? JsonArguments { get; init; }
    }

    // ---------------------------------------------------------------------
    // 请求构造（pi createClient 1008–1108 头合并 + SDK betas→anthropic-beta 头）
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
        AnthropicOptions? options,
        JsonNode payload,
        string? apiKey,
        bool isOAuth,
        AnthropicMessagesCompat compat,
        CacheRetention cacheRetention)
    {
        // pi createClient：identity 头最底层，accept / browser-access 默认，OAuth 换 Claude Code 头。
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ClientIdentityHelpers.GetIdentityHeaders(
            new ClientIdentityTarget(model.Provider, model.BaseUrl), options?.Identity))
        {
            headers[entry.Key] = entry.Value;
        }

        headers["accept"] = "application/json";
        headers["anthropic-dangerous-direct-browser-access"] = "true";

        // pi createClient：OAuth 走 Claude Code 身份头（copilot 动态头不在 S5 范围，见偏离表）。
        if (isOAuth)
        {
            headers["user-agent"] = $"claude-cli/{ClaudeCodeVersion}";
            headers["x-app"] = "cli";
        }
        else
        {
            var cacheSessionId = cacheRetention == CacheRetention.None ? null : options?.SessionId;
            if (cacheSessionId is not null && compat.SendSessionAffinityHeaders)
            {
                headers[compat.SessionAffinityFormat == "openrouter" ? "x-session-id" : "x-session-affinity"]
                    = cacheSessionId;
            }
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

        if (!string.IsNullOrEmpty(apiKey))
        {
            if (isOAuth)
            {
                headers["authorization"] = $"Bearer {apiKey}";
            }
            else
            {
                headers["x-api-key"] = apiKey;
            }
        }

        if (!IsHeaderSuppressed(options?.Headers, "anthropic-version") && !headers.ContainsKey("anthropic-version"))
        {
            headers["anthropic-version"] = ApiVersion;
        }

        // SDK 把 params.betas 转成 anthropic-beta 头（不进 body）。
        if (payload["betas"] is JsonArray betas && payload is JsonObject payloadObject)
        {
            payloadObject.Remove("betas");
            var features = betas.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var feature) ? feature : null)
                .Where(feature => !string.IsNullOrEmpty(feature))
                .ToList();
            if (features.Count > 0 && !IsHeaderSuppressed(options?.Headers, "anthropic-beta"))
            {
                headers["anthropic-beta"] = string.Join(",", features!);
            }
        }

        var fetchHeaders = new FetchHeaders();
        foreach (var entry in headers)
        {
            fetchHeaders[entry.Key] = entry.Value;
        }

        var url = $"{model.BaseUrl.TrimEnd('/')}/v1/messages";
        return new FetchRequest("POST", url, fetchHeaders, Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

    private static bool IsHeaderSuppressed(IReadOnlyDictionary<string, string?>? headers, string name)
        => headers is not null && headers.Keys.Any(key =>
            string.Equals(key, name, StringComparison.OrdinalIgnoreCase)
            && headers[key] is null);

    private static bool HasHeader(IReadOnlyDictionary<string, string?>? headers, string name)
        => headers is not null && headers.Keys.Any(key =>
            string.Equals(key, name, StringComparison.OrdinalIgnoreCase)
            && headers[key] is { } value
            && value.Trim().Length > 0);

    private static bool IsOAuthToken(string? apiKey)
        => apiKey is not null && apiKey.Contains("sk-ant-oat", StringComparison.Ordinal);

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
    // buildParams（pi 1153–1328）
    // ---------------------------------------------------------------------

    /// <summary>请求参数与「是否含 <c>strict: "prefer"</c> 工具」的打包结果（pi buildParams 返回值）。</summary>
    internal sealed record BuiltParams(JsonObject Params, bool HasPreferredStrictTools, List<string> Betas)
    {
        /// <summary>
        /// effort-message（adaptive 中段 effort）模式生效时的当前 effort（pi buildParams 返回
        /// <c>providerThinkingLevel</c>，anthropic-messages.ts:1326）。模式未生效为 null。
        /// stream（Task 3）把它记到 <c>output.providerThinkingLevel</c>，随 assistant 消息落库，
        /// 供下一回合 <c>insertThinkingLevelMessages</c> 重放历史 effort 标记。
        /// </summary>
        public string? ProviderThinkingLevel { get; init; }
    }

    internal static BuiltParams BuildParamsForRequest(
        Model model,
        TranscriptContext context,
        bool isOAuthToken,
        AnthropicOptions? options,
        AnthropicMessagesCompat compat,
        bool disablePreferredStrictTools = false)
    {
        var cacheControl = GetCacheControl(model, options?.CacheRetention, options?.Env, compat);
        var initialSystemMessage = GetInitialSystemMessage(context.Messages);
        var initialSystemText = initialSystemMessage is not null ? GetSystemMessageText(initialSystemMessage) : "";
        var transformedMessages = TransformMessages(context.Messages, model, NormalizeToolCallId);
        var conversationMessages = initialSystemMessage is not null
            ? transformedMessages.Skip(1).ToList()
            : transformedMessages;

        // Native tool changes keep the request-level tool list fixed and define every later tool
        // by value in a `tool_addition` block. Anthropic rejects a list where every tool is
        // deferred, so there must be an initial active tool to anchor the placeholder.
        var initialTools = initialSystemMessage?.ToolsAdded ?? Array.Empty<Tool>();
        var nativeToolChanges = compat.SupportsMidConvoSystemMessages
            && compat.SupportsMidConvoToolChanges
            && initialTools.Count > 0;
        var hasPreferredStrictTools = false;

        JsonArray ConvertDefinitions(IReadOnlyList<Tool> toolsToConvert, JsonObject? toolCacheControl)
        {
            var definitions = ConvertTools(
                toolsToConvert,
                isOAuthToken,
                compat.SupportsEagerToolInputStreaming,
                compat.SupportsStrictTools,
                toolCacheControl,
                disablePreferredStrictTools);
            if (HasPreferredStrictTool(definitions, toolsToConvert))
            {
                hasPreferredStrictTools = true;
            }

            return definitions;
        }

        var thinkingDisabled = model.Reasoning
            && options?.ThinkingEnabled == false
            && !(model.ThinkingLevelMap is { } levelMap
                && levelMap.TryGetValue(ModelThinkingLevel.Off, out var offMapping)
                && offMapping is null);
        // Effort cannot change between messages while thinking is disabled, so "off" sends no effort markers.
        var effortMessages = compat.SupportsMidConvoEffort && !thinkingDisabled;
        var converted = ConvertMessagesCore(
            conversationMessages,
            isOAuthToken,
            cacheControl,
            compat.AllowEmptySignature,
            effortMessages ? model.Provider : null,
            nativeToolChanges
                ? tools => ConvertDefinitions(tools, null)
                : (Func<IReadOnlyList<Tool>, JsonArray>?)null);

        var activeEffort = WireEffort(options?.Effort) ?? "high";
        var parameters = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = effortMessages
                ? InsertThinkingLevelMessages(converted, activeEffort)
                : converted.Messages,
            ["max_tokens"] = options?.MaxTokens ?? model.MaxTokens,
            ["stream"] = true,
        };

        var betaFeatures = GetBetaFeatures(model, context, isOAuthToken, nativeToolChanges, options, compat);
        // SDK 把 betas 参数转成 anthropic-beta 头（BuildRequest 里出体转头）；OnPayload 也能看到它。
        if (betaFeatures.Count > 0)
        {
            parameters["betas"] = new JsonArray(betaFeatures.Select(feature => (JsonNode)feature).ToArray());
        }

        // For OAuth tokens, we MUST include Claude Code identity.
        if (isOAuthToken)
        {
            var system = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = "You are Claude Code, Anthropic's official CLI for Claude.",
            });
            if (cacheControl is not null)
            {
                ((JsonObject)system[0]!)["cache_control"] = cacheControl.DeepClone();
            }

            if (initialSystemText.Length > 0)
            {
                var block = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = SanitizeSurrogates(initialSystemText),
                };
                if (cacheControl is not null) block["cache_control"] = cacheControl.DeepClone();
                system.Add(block);
            }

            parameters["system"] = system;
        }
        else if (initialSystemText.Length > 0)
        {
            var block = new JsonObject
            {
                ["type"] = "text",
                ["text"] = SanitizeSurrogates(initialSystemText),
            };
            if (cacheControl is not null) block["cache_control"] = cacheControl.DeepClone();
            parameters["system"] = new JsonArray(block);
        }

        // Temperature is incompatible with extended thinking and unsupported on Claude Opus 4.7+.
        if (options?.Temperature is { } temperature
            && options.ThinkingEnabled != true
            && !compat.SupportsMidConvoEffort
            && compat.SupportsTemperature)
        {
            parameters["temperature"] = temperature;
        }

        var toolCacheControl = compat.SupportsCacheControlOnTools ? cacheControl : null;
        if (nativeToolChanges)
        {
            // Initial tools stay active with the cache breakpoint on the last one, followed by the
            // placeholder; later tools are defined by value in `tool_addition` blocks.
            var tools = ConvertDefinitions(initialTools, toolCacheControl);
            tools.Add(DeferredToolPlaceholder());
            parameters["tools"] = tools;
        }
        else
        {
            var tools = GetCurrentTools(context.Messages);
            if (tools.Count > 0)
            {
                parameters["tools"] = ConvertDefinitions(tools, toolCacheControl);
            }
        }

        if (thinkingDisabled)
        {
            // No effort is sent, so the model default applies. Anthropic rejects `block_binding`
            // with disabled thinking.
            parameters["thinking"] = new JsonObject { ["type"] = "disabled" };
        }
        else if (effortMessages)
        {
            // Managed effort models use adaptive thinking so prefix mismatches can
            // be dropped instead of surfacing as persistent 400 responses.
            parameters["thinking"] = new JsonObject
            {
                ["type"] = "adaptive",
                ["display"] = WireThinkingDisplay(options?.ThinkingDisplay) ?? "summarized",
                ["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" },
            };
            // The top-level effort is part of the cached prompt, so it stays fixed at "low":
            // organizations can cap effort and reject requests containing any level above the cap.
            parameters["output_config"] = new JsonObject { ["effort"] = "low" };
        }
        else if (model.Reasoning && options?.ThinkingEnabled == true)
        {
            // Default to "summarized" so Opus 4.7 and Mythos Preview behave like
            // older Claude 4 models (whose API default is also "summarized").
            var display = WireThinkingDisplay(options.ThinkingDisplay) ?? "summarized";
            if (compat.ForceAdaptiveThinking)
            {
                // Adaptive thinking: Claude decides when and how much to think.
                parameters["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = display };
                if (options.Effort is { } effort)
                {
                    parameters["output_config"] = new JsonObject { ["effort"] = WireEffort(effort) };
                }
            }
            else
            {
                // Budget-based thinking for older models.
                parameters["thinking"] = new JsonObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = options.ThinkingBudgetTokens is > 0 ? options.ThinkingBudgetTokens.Value : 1024,
                    ["display"] = display,
                };
            }
        }

        if (options?.Metadata is { } metadata
            && metadata["user_id"] is JsonValue userIdValue
            && userIdValue.TryGetValue<string>(out var userId))
        {
            parameters["metadata"] = new JsonObject { ["user_id"] = userId };
        }

        if (options?.ToolChoice is { } toolChoice)
        {
            if (toolChoice is JsonValue choiceValue && choiceValue.TryGetValue<string>(out var choice))
            {
                parameters["tool_choice"] = new JsonObject { ["type"] = choice };
            }
            else
            {
                parameters["tool_choice"] = toolChoice.DeepClone();
            }
        }

        if (compat.AllowedFallbackModels is { Count: > 0 } fallbacks)
        {
            var fallbackModels = new JsonArray();
            foreach (var fallback in fallbacks)
            {
                if (fallback is JsonObject entry && entry["model"] is { } fallbackModel)
                {
                    fallbackModels.Add(fallbackModel.DeepClone());
                }
            }

            if (fallbackModels.Count > 0)
            {
                parameters["fallbacks"] = fallbackModels;
            }
        }

        return new BuiltParams(parameters, hasPreferredStrictTools, betaFeatures)
        {
            // pi anthropic-messages.ts:1326：providerThinkingLevel = effortMessages ? activeEffort : undefined。
            ProviderThinkingLevel = effortMessages ? activeEffort : null,
        };
    }

    private static JsonObject DeferredToolPlaceholder() => new()
    {
        ["name"] = "__pi_deferred_placeholder__",
        ["description"] = "Reserved placeholder. Never available. Never call this.",
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray(),
        },
        ["defer_loading"] = true,
    };

    private static AnthropicMessagesCompat GetCompat(Model model)
    {
        var compat = model.Compat;
        bool Opt(string key, bool fallback)
            => compat is not null
                && compat.TryGetPropertyValue(key, out var value)
                && value is JsonValue { } primitive
                && primitive.TryGetValue<bool>(out var flag)
                    ? flag
                    : fallback;

        var isOpenRouter = model.Provider == "openrouter"
            || model.BaseUrl.Contains("openrouter.ai", StringComparison.Ordinal);
        string? sessionAffinityFormat = null;
        if (compat is not null
            && compat.TryGetPropertyValue("sessionAffinityFormat", out var formatNode)
            && formatNode is JsonValue { } formatValue
            && formatValue.TryGetValue<string>(out var format))
        {
            sessionAffinityFormat = format;
        }
        else if (isOpenRouter)
        {
            sessionAffinityFormat = "openrouter";
        }

        JsonArray? fallbacks = null;
        if (compat is not null
            && compat.TryGetPropertyValue("allowedFallbackModels", out var fallbackNode)
            && fallbackNode is JsonArray fallbackArray)
        {
            fallbacks = fallbackArray;
        }

        return new AnthropicMessagesCompat
        {
            SupportsEagerToolInputStreaming = Opt("supportsEagerToolInputStreaming", true),
            SupportsLongCacheRetention = Opt("supportsLongCacheRetention", true),
            SendSessionAffinityHeaders = Opt("sendSessionAffinityHeaders", isOpenRouter),
            SessionAffinityFormat = sessionAffinityFormat,
            SupportsCacheControlOnTools = Opt("supportsCacheControlOnTools", true),
            SupportsTemperature = Opt("supportsTemperature", true),
            AllowEmptySignature = Opt("allowEmptySignature", false),
            SupportsStrictTools = Opt("supportsStrictTools", false),
            SupportsMidConvoSystemMessages = Opt("supportsMidConvoSystemMessages", false),
            SupportsMidConvoToolChanges = Opt("supportsMidConvoToolChanges", false),
            SupportsMidConvoEffort = Opt("supportsMidConvoEffort", false),
            ForceAdaptiveThinking = Opt("forceAdaptiveThinking", false),
            AllowedFallbackModels = fallbacks,
        };
    }

    private static List<string> GetBetaFeatures(
        Model model,
        TranscriptContext context,
        bool isOAuthToken,
        bool nativeToolChanges,
        AnthropicOptions? options,
        AnthropicMessagesCompat compat)
    {
        // model.headers / options.headers 显式配置的 anthropic-beta 最权威：null 清空，值精确采用。
        string? configuredFeatures = null;
        var configuredExplicitly = false;
        void Scan(IReadOnlyDictionary<string, string?>? headers)
        {
            if (headers is null) return;
            foreach (var key in headers.Keys)
            {
                if (!string.Equals(key, "anthropic-beta", StringComparison.OrdinalIgnoreCase)) continue;
                configuredFeatures = headers[key];
                configuredExplicitly = true;
            }
        }

        void ScanModel(IReadOnlyDictionary<string, string>? headers)
        {
            if (headers is null) return;
            foreach (var key in headers.Keys)
            {
                if (!string.Equals(key, "anthropic-beta", StringComparison.OrdinalIgnoreCase)) continue;
                configuredFeatures = headers[key];
                configuredExplicitly = true;
            }
        }

        ScanModel(model.Headers);
        Scan(options?.Headers);

        if (configuredExplicitly)
        {
            if (configuredFeatures is null) return new List<string>();
            return configuredFeatures
                .Split(',')
                .Select(feature => feature.Trim())
                .Where(feature => feature.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var features = new List<string>();
        if (isOAuthToken)
        {
            features.Add("claude-code-20250219");
            features.Add("oauth-2025-04-20");
        }

        if (GetCurrentTools(context.Messages).Count > 0 && !compat.SupportsEagerToolInputStreaming)
        {
            features.Add(FineGrainedToolStreamingBeta);
        }

        if (model.Reasoning
            && options?.ThinkingEnabled == true
            && (options.InterleavedThinking ?? true)
            && !compat.ForceAdaptiveThinking)
        {
            features.Add(InterleavedThinkingBeta);
        }

        if (compat.AllowedFallbackModels is { Count: > 0 })
        {
            features.Add(ServerSideFallbackBeta);
        }

        if (compat.SupportsMidConvoEffort)
        {
            features.Add(MidConversationOutputConfigBeta);
            features.Add(ThinkingBindingControlsBeta);
        }

        if (nativeToolChanges) features.Add(InlineToolsBeta);
        return features.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string? WireThinkingDisplay(AnthropicThinkingDisplay? display)
        => display switch
        {
            AnthropicThinkingDisplay.Summarized => "summarized",
            AnthropicThinkingDisplay.Omitted => "omitted",
            _ => null,
        };

    /// <summary>
    /// pi mapThinkingLevelToEffort：ThinkingLevelMap 命中优先，否则 minimal/low→low、medium→medium、
    /// 其余→high（effort "max" 在 adaptive 模型全量可用；原生 xhigh 仅部分 Claude 型号支持）。
    /// C# AnthropicOptions.Effort 形状是 ThinkingLevel?，映射经字符串回转；模型映射值不在
    /// Anthropic effort 词表内时按 pi 的 default 分支回落 high。
    /// </summary>
    private static string MapThinkingLevelToEffort(Model model, ThinkingLevel level)
    {
        if (model.ThinkingLevelMap is { } map
            && map.TryGetValue(ToModelThinkingLevel(level), out var mapped)
            && mapped is not null)
        {
            return mapped;
        }

        return level switch
        {
            ThinkingLevel.Minimal or ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            _ => "high",
        };
    }

    private static ThinkingLevel ParseEffort(string effort)
        => effort switch
        {
            "low" => ThinkingLevel.Low,
            "medium" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            "xhigh" => ThinkingLevel.XHigh,
            "max" => ThinkingLevel.Max,
            _ => ThinkingLevel.High,
        };

    private static string WireEffort(ThinkingLevel? effort)
        => effort switch
        {
            ThinkingLevel.Minimal => "low",
            ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High => "high",
            ThinkingLevel.XHigh => "xhigh",
            ThinkingLevel.Max => "max",
            _ => "high",
        };

    // ---------------------------------------------------------------------
    // cache control（pi 85–110 + resolveCacheRetention）
    // ---------------------------------------------------------------------

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

    private static JsonObject? GetCacheControl(
        Model model,
        CacheRetention? cacheRetention,
        IReadOnlyDictionary<string, string>? env,
        AnthropicMessagesCompat compat)
    {
        var retention = ResolveCacheRetention(cacheRetention, env);
        if (retention == CacheRetention.None) return null;
        var cacheControl = new JsonObject { ["type"] = "ephemeral" };
        if (retention == CacheRetention.Long && compat.SupportsLongCacheRetention)
        {
            cacheControl["ttl"] = "1h";
        }

        return cacheControl;
    }

    // ---------------------------------------------------------------------
    // convertMessages（pi 1348–1556）
    // ---------------------------------------------------------------------

    private sealed record ConvertedMessages(JsonArray Messages, Dictionary<int, string> AssistantLevels);

    private static ConvertedMessages ConvertMessagesCore(
        IReadOnlyList<Message> transformedMessages,
        bool isOAuthToken,
        JsonObject? cacheControl,
        bool allowEmptySignature,
        string? managedProvider,
        Func<IReadOnlyList<Tool>, JsonArray>? convertToolDefinitions)
    {
        var parameters = new JsonArray();
        var assistantLevels = new Dictionary<int, string>();
        // Later system messages are held back and emitted directly before the next assistant
        // message that is sent (or at the end of the transcript). Anthropic requires
        // `tool_result` blocks to immediately follow their `tool_use` (#11091).
        var pendingSystemMessages = new List<JsonObject>();

        void FlushPendingSystemMessages()
        {
            foreach (var message in pendingSystemMessages) parameters.Add(message);
            pendingSystemMessages.Clear();
        }

        for (var i = 0; i < transformedMessages.Count; i++)
        {
            var message = transformedMessages[i];

            if (message is SystemMessage system)
            {
                // Later system messages only reach this point when the model accepts them natively.
                var text = RenderSystemMessageUpdate(system);
                var blocks = new JsonArray();
                if (text.Length > 0)
                {
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = SanitizeSurrogates(text),
                    });
                }

                if (convertToolDefinitions is not null)
                {
                    var added = system.ToolsAdded ?? Array.Empty<Tool>();
                    var redefined = added.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (var removed in system.ToolsRemoved ?? Array.Empty<ToolReference>())
                    {
                        // A new definition under the same name replaces the old one, so no removal is needed.
                        if (redefined.Contains(removed.Name)) continue;
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_removal",
                            ["tool"] = new JsonObject
                            {
                                ["type"] = "tool_reference",
                                ["name"] = isOAuthToken ? ToClaudeCodeName(removed.Name) : removed.Name,
                            },
                        });
                    }

                    foreach (var definition in convertToolDefinitions(added).ToArray())
                    {
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_addition",
                            ["tool"] = new JsonObject
                            {
                                ["type"] = "tool_definition",
                                ["definition"] = definition!.DeepClone(),
                            },
                        });
                    }
                }

                if (blocks.Count > 0)
                {
                    pendingSystemMessages.Add(new JsonObject { ["role"] = "system", ["content"] = blocks });
                }
            }
            else if (message is UserMessage user)
            {
                if (user.Content is MessageContent.Text textContent)
                {
                    if (textContent.Value.Trim().Length > 0)
                    {
                        parameters.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = SanitizeSurrogates(textContent.Value),
                        });
                    }
                }
                else
                {
                    var blocks = new JsonArray();
                    foreach (var item in user.Content.ToBlocks())
                    {
                        switch (item)
                        {
                            case TextContent text:
                                if (text.Text.Trim().Length == 0) continue;
                                blocks.Add(new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = SanitizeSurrogates(text.Text),
                                });
                                break;
                            case ImageContent image:
                                blocks.Add(ConvertImageBlock(image));
                                break;
                        }
                    }

                    if (blocks.Count == 0) continue;
                    parameters.Add(new JsonObject { ["role"] = "user", ["content"] = blocks });
                }
            }
            else if (message is AssistantMessage assistant)
            {
                var blocks = new JsonArray();
                foreach (var block in assistant.Content)
                {
                    switch (block)
                    {
                        case TextContent text:
                            if (text.Text.Trim().Length == 0) continue;
                            blocks.Add(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = SanitizeSurrogates(text.Text),
                            });
                            break;

                        case ThinkingContent thinking:
                            // Redacted thinking: pass the opaque payload back as redacted_thinking.
                            if (thinking.Redacted == true)
                            {
                                blocks.Add(new JsonObject
                                {
                                    ["type"] = "redacted_thinking",
                                    ["data"] = thinking.ThinkingSignature ?? "",
                                });
                                continue;
                            }

                            var signature = thinking.ThinkingSignature;
                            var hasSignature = !string.IsNullOrEmpty(signature) && signature.Trim().Length > 0;
                            if (thinking.Thinking.Trim().Length == 0 && !hasSignature) continue;
                            // A missing/empty signature (e.g. from an aborted stream) becomes plain
                            // text unless the model is marked as accepting empty signatures.
                            if (!hasSignature)
                            {
                                blocks.Add(allowEmptySignature
                                    ? new JsonObject
                                    {
                                        ["type"] = "thinking",
                                        ["thinking"] = SanitizeSurrogates(thinking.Thinking),
                                        ["signature"] = "",
                                    }
                                    : new JsonObject
                                    {
                                        ["type"] = "text",
                                        ["text"] = SanitizeSurrogates(thinking.Thinking),
                                    });
                            }
                            else
                            {
                                blocks.Add(new JsonObject
                                {
                                    ["type"] = "thinking",
                                    ["thinking"] = SanitizeSurrogates(thinking.Thinking),
                                    ["signature"] = signature,
                                });
                            }

                            break;

                        case ToolCall toolCall:
                            blocks.Add(new JsonObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = toolCall.Id,
                                ["name"] = isOAuthToken ? ToClaudeCodeName(toolCall.Name) : toolCall.Name,
                                ["input"] = toolCall.Arguments.DeepClone(),
                            });
                            break;
                    }
                }

                if (blocks.Count == 0) continue;
                // 转换后没有块的 assistant 消息会被丢弃，系统更新必须压在它之后（#11091）。
                FlushPendingSystemMessages();
                var messageIndex = parameters.Count;
                parameters.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                if (managedProvider is not null
                    && assistant.Api == "anthropic-messages"
                    && assistant.Provider == managedProvider
                    && IsAnthropicEffort(assistant.ProviderThinkingLevel))
                {
                    assistantLevels[messageIndex] = assistant.ProviderThinkingLevel!;
                }
            }
            else if (message is ToolResultMessage)
            {
                // Collect all consecutive toolResult messages (needed for z.ai's Anthropic endpoint).
                var toolResults = new JsonArray();
                // Anthropic 拒收「错误工具结果里带图片」，这些图片放到该回合所有工具结果之后、
                // 同一个 user 消息里（图片之前先放一行点名工具调用的说明）——#10794。
                var errorImages = new JsonArray();
                var j = i;
                while (j < transformedMessages.Count && transformedMessages[j] is ToolResultMessage toolMessage)
                {
                    var split = SplitErrorToolResultImages(toolMessage);
                    toolResults.Add(ConvertToolResult(toolMessage, split?.Content));
                    if (split is not null)
                    {
                        errorImages.Add(new JsonObject { ["type"] = "text", ["text"] = split.Caption });
                        foreach (var image in split.Images)
                        {
                            errorImages.Add(ConvertImageBlock(image));
                        }
                    }

                    j++;
                }

                // Skip the messages we've already processed.
                i = j - 1;

                foreach (var block in errorImages.ToArray())
                {
                    toolResults.Add(block!.DeepClone());
                }

                parameters.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
            }
        }

        FlushPendingSystemMessages();

        // Add cache_control to the last user or system message to cache conversation history.
        if (cacheControl is not null && parameters.Count > 0)
        {
            var lastMessage = (JsonObject)parameters[^1]!;
            var role = JsonStr(lastMessage, "role");
            if (role is "user" or "system")
            {
                if (lastMessage["content"] is JsonArray content)
                {
                    if (content.Count > 0
                        && content[^1] is JsonObject lastBlock
                        && JsonStr(lastBlock, "type")
                            is "text" or "image" or "tool_result" or "tool_addition" or "tool_removal")
                    {
                        lastBlock["cache_control"] = cacheControl.DeepClone();
                    }
                }
                else if (lastMessage["content"] is JsonValue contentValue
                         && contentValue.TryGetValue<string>(out var stringContent))
                {
                    lastMessage["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = stringContent,
                        ["cache_control"] = cacheControl.DeepClone(),
                    });
                }
            }
        }

        return new ConvertedMessages(parameters, assistantLevels);
    }

    private static JsonArray InsertThinkingLevelMessages(ConvertedMessages converted, string activeEffort)
    {
        var messages = new JsonArray();
        for (var index = 0; index < converted.Messages.Count; index++)
        {
            if (converted.AssistantLevels.TryGetValue(index, out var historicalEffort))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = new JsonArray(),
                    ["output_config"] = new JsonObject { ["effort"] = historicalEffort },
                });
            }

            messages.Add(converted.Messages[index]!.DeepClone());
        }

        messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = new JsonArray(),
            ["output_config"] = new JsonObject { ["effort"] = activeEffort },
        });
        return messages;
    }

    private static bool IsAnthropicEffort(string? value)
        => value is "low" or "medium" or "high" or "xhigh" or "max";

    private static JsonObject ConvertImageBlock(ImageContent image) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject
        {
            ["type"] = "base64",
            ["media_type"] = string.IsNullOrEmpty(image.MimeType) ? "image/png" : image.MimeType,
            ["data"] = image.Data,
        },
    };

    private static JsonObject ConvertToolResult(ToolResultMessage message, IReadOnlyList<ContentBlock>? content = null)
        => new()
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = message.ToolCallId,
            ["content"] = ConvertContentBlocks(content ?? message.Content),
            ["is_error"] = message.IsError,
        };

    private static JsonNode ConvertContentBlocks(IReadOnlyList<ContentBlock> content)
    {
        // If only text blocks, return as concatenated string for simplicity.
        var hasImages = content.Any(block => block is ImageContent);
        if (!hasImages)
        {
            return JsonValue.Create(SanitizeSurrogates(string.Join(
                "\n", content.OfType<TextContent>().Select(text => text.Text))))!;
        }

        // With images, convert to a content block array (Anthropic needs a text lead).
        var blocks = new JsonArray();
        var hasText = false;
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent text:
                    hasText = true;
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = SanitizeSurrogates(text.Text),
                    });
                    break;
                case ImageContent image:
                    blocks.Add(ConvertImageBlock(image));
                    break;
            }
        }

        if (!hasText)
        {
            blocks.Insert(0, new JsonObject { ["type"] = "text", ["text"] = "(see attached image)" });
        }

        return blocks;
    }

    private static string ToClaudeCodeName(string name)
        => ClaudeCodeToolLookup.TryGetValue(name.ToLowerInvariant(), out var canonical) ? canonical : name;

    /// <summary>pi transform-messages.ts splitErrorToolResultImages：错误工具结果拆图（#10794）。</summary>
    private sealed record SplitErrorToolResult(
        IReadOnlyList<ContentBlock> Content,
        string Caption,
        IReadOnlyList<ImageContent> Images);

    private static SplitErrorToolResult? SplitErrorToolResultImages(ToolResultMessage message)
    {
        if (!message.IsError) return null;
        var images = message.Content.OfType<ImageContent>().ToList();
        if (images.Count == 0) return null;
        return new SplitErrorToolResult(
            ReplaceImagesWithPlaceholders(message.Content, "(image attached after the tool results)"),
            $"Images from the error result of tool call {message.ToolCallId} ({message.ToolName}):",
            images);
    }

    private static IReadOnlyList<ContentBlock> ReplaceImagesWithPlaceholders(
        IReadOnlyList<ContentBlock> content, string placeholder)
    {
        var result = new List<ContentBlock>();
        var previousWasPlaceholder = false;
        foreach (var block in content)
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

        return result;
    }

    // ---------------------------------------------------------------------
    // normalizeToolCallId（pi 1330–1333）
    // ---------------------------------------------------------------------

    /// <summary>Normalize tool call IDs to Anthropic's required pattern <c>^[a-zA-Z0-9_-]+$</c> and 64 length.</summary>
    public static string NormalizeToolCallId(string id)
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
    // convertTools（pi 1619–1659）+ Anthropic strict 关键字（pi 1509–1560）
    // ---------------------------------------------------------------------

    private static bool HasPreferredStrictTool(JsonArray definitions, IReadOnlyList<Tool> tools)
    {
        var count = Math.Min(definitions.Count, tools.Count);
        for (var index = 0; index < count; index++)
        {
            if (definitions[index] is JsonObject definition
                && definition["strict"] is JsonValue strictValue
                && strictValue.TryGetValue<bool>(out var strict)
                && strict
                && PrefersJsonSchemaStrictSampling(tools[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonArray ConvertTools(
        IReadOnlyList<Tool> tools,
        bool isOAuthToken,
        bool supportsEagerToolInputStreaming,
        bool supportsStrictTools,
        JsonObject? cacheControl,
        bool disablePreferredStrictTools = false)
    {
        var result = new JsonArray();
        if (tools is null) return result;

        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            // 降级路径：prefer 工具按原始 schema 发送；require 工具仍走 strict。
            var strict = disablePreferredStrictTools && PrefersJsonSchemaStrictSampling(tool)
                ? (bool?)null
                : ResolveJsonSchemaStrictSampling(tool, supportsStrictTools, IsAnthropicStrictUnsupportedKeyword);
            var parameters = GetJsonSchemaToolParameters(tool, strict);

            JsonObject inputSchema;
            if (strict == true)
            {
                inputSchema = (JsonObject)parameters.DeepClone();
                inputSchema["type"] = "object";
                inputSchema["properties"] = parameters["properties"]?.DeepClone() ?? new JsonObject();
                inputSchema["required"] = parameters["required"]?.DeepClone() ?? new JsonArray();
            }
            else
            {
                inputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = parameters["properties"]?.DeepClone() ?? new JsonObject(),
                    ["required"] = parameters["required"]?.DeepClone() ?? new JsonArray(),
                };
            }

            var definition = new JsonObject
            {
                ["name"] = isOAuthToken ? ToClaudeCodeName(tool.Name) : tool.Name,
                ["description"] = tool.Description,
            };
            if (supportsEagerToolInputStreaming) definition["eager_input_streaming"] = true;
            if (strict == true) definition["strict"] = true;
            definition["input_schema"] = inputSchema;
            if (cacheControl is not null && index == tools.Count - 1)
            {
                definition["cache_control"] = cacheControl.DeepClone();
            }

            result.Add(definition);
        }

        return result;
    }

    private static bool IsAnthropicStrictUnsupportedKeyword(string key, JsonNode? value)
    {
        if (AnthropicStrictUnsupportedKeywords.Contains(key)) return true;
        if (key == "minItems")
        {
            // 非 0/1 数值（含非数值本身）都会被 Anthropic strict 拒收。
            if (value is JsonValue number && number.TryGetValue<double>(out var asDouble))
            {
                return asDouble != 0 && asDouble != 1;
            }

            return true;
        }

        if (key == "format")
        {
            return value is not JsonValue formatValue
                || !formatValue.TryGetValue<string>(out var format)
                || !AnthropicStrictStringFormats.Contains(format);
        }

        return false;
    }

    /// <summary>pi constrained-sampling.ts prefersJsonSchemaStrictSampling。</summary>
    private static bool PrefersJsonSchemaStrictSampling(Tool tool)
        => tool.ConstrainedSampling is ConstrainedSampling.Config
        {
            Value: ConstrainedSamplingConfig.JsonSchema { Strict: StrictMode.Prefer },
        };

    /// <summary>pi resolveJsonSchemaStrictSampling + provider 关键字检查；不可编译的 require 抛错。</summary>
    private static bool? ResolveJsonSchemaStrictSampling(
        Tool tool,
        bool supportsStrictMode,
        Func<string, JsonNode?, bool>? isUnsupportedKeyword)
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
                MakeStrictJsonSchema(tool.Parameters, isUnsupportedKeyword);
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
        => strict == true ? MakeStrictJsonSchema(tool.Parameters, null) : tool.Parameters;

    private sealed class UnsupportedStrictJsonSchemaException : InvalidOperationException
    {
        public UnsupportedStrictJsonSchemaException(string message)
            : base(message)
        {
        }
    }

    private static bool IsUnsupportedStrictSchemaError(Exception error)
        => error is UnsupportedStrictJsonSchemaException;

    /// <summary>pi makeStrictJsonSchema + provider 关键字检查：把工具 schema 编译成 strict 子集。</summary>
    private static JsonObject MakeStrictJsonSchema(
        JsonObject schema, Func<string, JsonNode?, bool>? isUnsupportedKeyword)
    {
        var cloned = (JsonObject?)schema.DeepClone()
            ?? throw new UnsupportedStrictJsonSchemaException("root schema must have type object");
        MakeJsonSchemaNodeStrict(cloned, isUnsupportedKeyword);
        if (JsonStr(cloned, "type") != "object")
        {
            throw new UnsupportedStrictJsonSchemaException("root schema must have type object");
        }

        return cloned;
    }

    private static void MakeJsonSchemaNodeStrict(
        JsonObject schema, Func<string, JsonNode?, bool>? isUnsupportedKeyword)
    {
        foreach (var key in UnsupportedStrictSchemaKeys)
        {
            if (schema.ContainsKey(key))
            {
                throw new UnsupportedStrictJsonSchemaException($"{key} schemas are unsupported");
            }
        }

        if (isUnsupportedKeyword is not null)
        {
            foreach (var entry in schema.ToArray())
            {
                if (isUnsupportedKeyword(entry.Key, entry.Value))
                {
                    var rendered = entry.Value is null ? "null" : entry.Value.ToJsonString();
                    throw new UnsupportedStrictJsonSchemaException($"{entry.Key}: {rendered} is unsupported");
                }
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

                MakeJsonSchemaNodeStrict(variantNode, isUnsupportedKeyword);
            }
        }

        if (schema["items"] is JsonArray)
        {
            throw new UnsupportedStrictJsonSchemaException("tuple schemas are unsupported");
        }

        if (schema["items"] is JsonObject itemsNode)
        {
            MakeJsonSchemaNodeStrict(itemsNode, isUnsupportedKeyword);
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
            MakeJsonSchemaNodeStrict(property, isUnsupportedKeyword);
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
    // mapStopReason（pi 1660–1685；Task 3 SSE 消费）
    // ---------------------------------------------------------------------

    internal static (StopReason StopReason, string? ErrorMessage) MapStopReason(
        string reason, string? stopDetailsExplanation = null)
        => reason switch
        {
            "end_turn" => (StopReason.Stop, null),
            "max_tokens" => (StopReason.Length, null),
            "tool_use" => (StopReason.ToolUse, null),
            "refusal" => (StopReason.Error, string.IsNullOrEmpty(stopDetailsExplanation)
                ? "The model refused to complete the request"
                : stopDetailsExplanation),
            "pause_turn" => (StopReason.Stop, null),
            "stop_sequence" => (StopReason.Stop, null),
            "sensitive" => (StopReason.Error, "Provider stopped with: sensitive"),
            _ => throw new InvalidOperationException($"Unhandled stop reason: {reason}"),
        };

    // ---------------------------------------------------------------------
    // transform-messages.ts 移植（与 OpenAI 家族同形）
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
    // transcript 工具（pi utils/transcript.ts + utils/text.ts）
    // ---------------------------------------------------------------------

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
    // simple options（pi simple-options.ts + StreamSimple）
    // ---------------------------------------------------------------------

    private static AnthropicOptions BuildSimpleOptions(
        Model model, TranscriptContext context, SimpleStreamOptions? options)
    {
        var clampedReasoning = options?.Reasoning is { } reasoning
            ? ModelOperations.ClampThinkingLevel(model, ToModelThinkingLevel(reasoning))
            : (ModelThinkingLevel?)null;

        var baseOptions = new AnthropicOptions
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

        if (options?.Reasoning is not { } requested)
        {
            return baseOptions with { ThinkingEnabled = false };
        }

        // For models with adaptive thinking: use an effort level.
        // For older models: use budget-based thinking.
        if (GetCompat(model).ForceAdaptiveThinking)
        {
            return baseOptions with
            {
                ThinkingEnabled = true,
                Effort = ParseEffort(MapThinkingLevelToEffort(model, requested)),
            };
        }

        // Undefined means the caller did not request an output cap; let the helper use the model cap.
        var adjusted = AdjustMaxTokensForThinking(
            baseOptions.MaxTokens, model.MaxTokens, requested, options?.ThinkingBudgets);
        var maxTokens = ClampMaxTokensToContext(model, context, adjusted.MaxTokens);
        return baseOptions with
        {
            MaxTokens = maxTokens,
            ThinkingEnabled = true,
            ThinkingBudgetTokens = Math.Min(adjusted.ThinkingBudget, Math.Max(0, maxTokens - MinAnswerTokens)),
        };
    }

    private sealed record AdjustedTokens(int MaxTokens, int ThinkingBudget);

    private static AdjustedTokens AdjustMaxTokensForThinking(
        int? baseMaxTokens, int modelMaxTokens, ThinkingLevel reasoningLevel, ThinkingBudgets? customBudgets)
    {
        var thinkingBudget = ThinkingBudgetForLevel(reasoningLevel, customBudgets);
        var maxTokens = baseMaxTokens is not { } current
            ? modelMaxTokens
            : Math.Min(current + thinkingBudget, modelMaxTokens);
        if (maxTokens <= thinkingBudget)
        {
            thinkingBudget = ClampThinkingBudgetToAnswerRoom(thinkingBudget, maxTokens);
        }

        return new AdjustedTokens(maxTokens, thinkingBudget);
    }

    private static int ThinkingBudgetForLevel(ThinkingLevel level, ThinkingBudgets? customBudgets)
    {
        var clamped = ClampReasoning(level);
        return clamped switch
        {
            ThinkingLevel.Minimal => customBudgets?.Minimal ?? 1024,
            ThinkingLevel.Low => customBudgets?.Low ?? 2048,
            ThinkingLevel.Medium => customBudgets?.Medium ?? 8192,
            ThinkingLevel.High => customBudgets?.High ?? 16384,
            _ => 16384,
        };
    }

    private static ThinkingLevel ClampReasoning(ThinkingLevel effort)
        => effort is ThinkingLevel.XHigh or ThinkingLevel.Max ? ThinkingLevel.High : effort;

    private static int ClampThinkingBudgetToAnswerRoom(int thinkingBudget, int ceiling)
        => Math.Min(thinkingBudget, Math.Max(0, ceiling - MinAnswerTokens));

    private static int ClampMaxTokensToContext(Model model, TranscriptContext context, int maxTokens)
    {
        if (model.ContextWindow <= 0) return Math.Max(1, maxTokens);
        var available = model.ContextWindow - (int)EstimateContextTokens(context.Messages) - 4096;
        return Math.Min(maxTokens, Math.Max(1, available));
    }

    private const double CharsPerToken = 3.5;
    private const int EstimatedImageChars = 4800;

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

    // ---------------------------------------------------------------------
    // 错误格式化（与 OpenAI 家族同形）
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

        /// <summary>effort-message 模式的当前 effort（pi assistant.providerThinkingLevel）。</summary>
        public string? ProviderThinkingLevel { get; set; }
        public string? ResponseId { get; set; }
        public string? ResponseModel { get; set; }
        public string? RawStopReason { get; set; }
        public string? ErrorMessage { get; set; }
        public StopReason StopReason { get; set; } = StopReason.Pending;
        public Usage Usage { get; set; } = new();
        public List<AssistantMessageDiagnostic> Diagnostics { get; } = new();

        public AssistantMessage Snapshot(IReadOnlyList<StreamBlock> blocks)
            => new()
            {
                Content = blocks.Select(block => block.ToContentBlock()).ToList(),
                Api = Api,
                Provider = Provider,
                Model = ModelId,
                ProviderThinkingLevel = ProviderThinkingLevel,
                Usage = Usage,
                StopReason = StopReason,
                ErrorMessage = ErrorMessage,
                RawStopReason = RawStopReason,
                ResponseId = ResponseId,
                ResponseModel = ResponseModel,
                Diagnostics = Diagnostics.Count > 0 ? Diagnostics.ToList() : null,
                Timestamp = Timestamp,
            };
    }

    // ---------------------------------------------------------------------
    // OpenRouter 报价覆盖（pi utils/openrouter-cost.ts）
    // ---------------------------------------------------------------------

    /// <summary>provider 是 openrouter，或 baseUrl 指向它（pi isOpenRouterModel，openrouter-cost.ts:24）。</summary>
    private static bool IsOpenRouterModel(Model model)
        => model.Provider == "openrouter" || model.BaseUrl.Contains("openrouter.ai", StringComparison.Ordinal);

    /// <summary>合法的计费值：有限且非负（pi isValidCost，openrouter-cost.ts:20）。</summary>
    private static bool IsValidCost(double? value)
        => value is { } cost && double.IsFinite(cost) && cost >= 0;

    /// <summary>
    /// 响应带完整、合法的 OpenRouter 账户计费时，用实付覆盖按目录价算出的总价并标记
    /// <see cref="UsageCost.TotalFromProvider"/>（pi applyOpenRouterReportedCost，openrouter-cost.ts:29–55）：
    /// 非 BYOK 直接用 <c>cost</c>；BYOK 的上报值只含 OpenRouter 自己的费用，还要加上游推理成本
    /// （<c>upstream_inference_cost</c> 缺失时用 <c>upstream_inference_prompt_cost +
    /// upstream_inference_completions_cost</c>）；<c>is_byok</c> 缺失或数值不合法（负数/非有限）时不动用量。
    /// </summary>
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

    private static JsonObject? JsonObj(JsonObject? obj, string key)
        => obj is not null && obj.TryGetPropertyValue(key, out var value) ? value as JsonObject : null;
}
