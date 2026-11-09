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
/// OpenAI Responses 兼容矩阵（pi <c>getCompat</c> openai-responses.ts 91–107 的解析结果）。
/// Responses 无按 provider 的探测矩阵：只有缺省值 + <c>Model.Compat</c> 显式覆盖。
/// </summary>
internal sealed record OpenAiResponsesCompat
{
    public bool SupportsDeveloperRole { get; init; } = true;

    public bool SupportsMidConvoSystemMessages { get; init; }

    /// <summary>openrouter（provider/baseUrl 命中）或 openai。</summary>
    public string SessionAffinityFormat { get; init; } = "openai";

    public bool SupportsLongCacheRetention { get; init; } = true;

    public bool SupportsStrictMode { get; init; }

    public bool SupportsOpenAIGrammarTools { get; init; }

    public bool SupportsAdditionalTools { get; init; }

    public bool SupportsToolSearch { get; init; }

    public bool SupportsExplicitPromptCacheMode { get; init; }

    public bool SupportsMaxOutputTokens { get; init; } = true;

    public bool SupportsMidConvoEffort { get; init; }
}

/// <summary>
/// OpenAI Responses 协议适配器（pi <c>api/openai-responses.ts</c> + <c>api/openai-responses-shared.ts</c> 的 C# 移植）：
/// input items 消息转换（reasoning item 回放、工具 ID 管道归一化、configuration_update、additional_tools/tool_search）、
/// tools 转换（function/grammar）、SSE 输出项状态机（output_index 槽位、reasoning summary/text、
/// function/custom tool call 参数流、usage/service tier 定价、status→stopReason）。
/// 通讯走 <see cref="FetchFunction"/> 端口（pi 里由 OpenAI SDK 承担）；失败一律编码为流内终态，不抛出。
/// ChatGPT sign-in 限制字段不在本实现（TODO(后续扩展, D18)：非 sk- 令牌省略 prompt_cache_* / max_output_tokens / temperature）。
/// </summary>
public static class OpenAiResponses
{
    private static readonly HashSet<string> OpenAiToolCallProviders = new() { "openai", "openai-codex", "opencode" };

    /// <summary>OpenAI Responses 拒绝 max_output_tokens 低于 16（pi#6265）。</summary>
    private const int OpenAiResponsesMinOutputTokens = 16;

    /// <summary>configuration_update 项接管思考档位时，reasoning.effort 固定为此值。</summary>
    private const string ConfigurationUpdateRequestEffort = "medium";

    private const int OpenAiPromptCacheKeyMaxLength = 64;
    private const int MaxProviderErrorBodyChars = 4000;
    private const char GrammarFormatLark = 'l';
    private const char GrammarFormatRegex = 'r';

    /// <summary>output_index 缺失（不合规服务端）时的槽位哨兵；JS 侧等价于 "undefined" 键，互不与真实索引冲突。</summary>
    private const int MissingOutputIndex = -1;

    private static readonly Regex UnpairedSurrogateRegex = new(
        @"[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]",
        RegexOptions.Compiled);

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
        OpenAiResponsesOptions? options = null)
        => Run(model, context, options);

    public static IAssistantMessageEventStream StreamSimple(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options = null)
        => Run(model, context, BuildSimpleOptions(model, context, options));

    /// <summary>
    /// 基类 <see cref="StreamOptions"/>（门面 <c>Models.Stream</c> 经 applyAuth 产出）升格为本协议专属选项：
    /// 逐字段显式拷贝全部基类字段（ApiKey/Fetch/CancellationToken/Headers/hooks…），协议专属字段保持 null。
    /// 已是 <see cref="OpenAiResponsesOptions"/> 则原样返回；直转丢失认证正是本方法的由来。
    /// </summary>
    private static OpenAiResponsesOptions? ToOpenAiResponsesOptions(StreamOptions? options)
    {
        if (options is null) return null;
        if (options is OpenAiResponsesOptions responses) return responses;
        return new OpenAiResponsesOptions
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
    // stream（pi openai-responses.ts 154–273 的 IIFE 形态）
    // ---------------------------------------------------------------------

    private static IAssistantMessageEventStream Run(Model model, TranscriptContext context, StreamOptions? options)
    {
        var stream = new AssistantMessageEventStream();
        var compat = GetCompat(model);
        var normalizedContext = ResolveTranscript(context, compat.SupportsMidConvoSystemMessages);

        _ = RunCoreAsync(stream, model, normalizedContext, ToOpenAiResponsesOptions(options), compat);
        return stream;
    }

    private static async Task RunCoreAsync(
        AssistantMessageEventStream stream,
        Model model,
        TranscriptContext normalizedContext,
        StreamOptions? options,
        OpenAiResponsesCompat compat)
    {
        var responsesOptions = options as OpenAiResponsesOptions;
        var blocks = new List<StreamBlock>();
        var configurationUpdateEffort = GetConfigurationUpdateEffort(model, responsesOptions, compat);
        var output = new MutableOutput(model)
        {
            ProviderThinkingLevel = configurationUpdateEffort,
        };
        try
        {
            var apiKey = GetClientApiKey(model.Provider, options?.ApiKey, options?.Headers);
            var cacheRetention = ResolveCacheRetention(options?.CacheRetention, options?.Env);
            var cacheSessionId = cacheRetention == CacheRetention.None ? null : options?.SessionId;
            var grammarToolInputProperties = CreateGrammarToolInputProperties(
                GetDeclaredTools(normalizedContext.Messages),
                compat.SupportsOpenAIGrammarTools);

            JsonNode payload = BuildParams(
                model, normalizedContext, options, compat, grammarToolInputProperties, configurationUpdateEffort);
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

            await ProcessResponsesStream(response.Body, output, blocks, stream, model, new ResponsesStreamOptions
            {
                OnProviderStreamEvent = options?.OnProviderStreamEvent,
                ServiceTier = responsesOptions?.ServiceTier,
                GrammarToolInputProperties = grammarToolInputProperties,
                ApplyServiceTierPricing = (usage, serviceTier) => ApplyServiceTierPricing(usage, serviceTier, model),
            }, lifetime.Token).ConfigureAwait(false);

            if (signal.IsCancellationRequested)
            {
                throw new OperationCanceledException("Request was aborted", signal);
            }

            if (output.StopReason == StopReason.Pending)
            {
                throw new InvalidOperationException("OpenAI Responses stream ended without a stop reason");
            }

            if (output.StopReason is StopReason.Aborted or StopReason.Error)
            {
                throw new InvalidOperationException(output.ErrorMessage ?? "An unknown error occurred");
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
    // 请求构造（pi createClient 294–337 + headers 合并）
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
        OpenAiResponsesCompat compat)
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

        if (cacheSessionId is not null)
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

        var url = $"{model.BaseUrl.TrimEnd('/')}/responses";
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
    // getCompat（pi 91–107）+ prompt cache 缺省（pi 124–141）
    // ---------------------------------------------------------------------

    private static OpenAiResponsesCompat GetCompat(Model model)
    {
        var detected = new OpenAiResponsesCompat
        {
            SessionAffinityFormat = DetectSessionAffinityFormat(model),
        };
        var compat = model.Compat;
        if (compat is null) return detected;

        return new OpenAiResponsesCompat
        {
            SupportsDeveloperRole = JsonBool(compat, "supportsDeveloperRole") ?? detected.SupportsDeveloperRole,
            SupportsMidConvoSystemMessages = JsonBool(compat, "supportsMidConvoSystemMessages")
                ?? detected.SupportsMidConvoSystemMessages,
            SessionAffinityFormat = JsonStr(compat, "sessionAffinityFormat") ?? detected.SessionAffinityFormat,
            SupportsLongCacheRetention = JsonBool(compat, "supportsLongCacheRetention")
                ?? detected.SupportsLongCacheRetention,
            SupportsStrictMode = JsonBool(compat, "supportsStrictMode") ?? detected.SupportsStrictMode,
            SupportsOpenAIGrammarTools = JsonBool(compat, "supportsOpenAIGrammarTools")
                ?? detected.SupportsOpenAIGrammarTools,
            SupportsAdditionalTools = JsonBool(compat, "supportsAdditionalTools")
                ?? detected.SupportsAdditionalTools,
            SupportsToolSearch = JsonBool(compat, "supportsToolSearch") ?? detected.SupportsToolSearch,
            SupportsExplicitPromptCacheMode = JsonBool(compat, "supportsExplicitPromptCacheMode")
                ?? detected.SupportsExplicitPromptCacheMode,
            SupportsMaxOutputTokens = JsonBool(compat, "supportsMaxOutputTokens")
                ?? detected.SupportsMaxOutputTokens,
            SupportsMidConvoEffort = JsonBool(compat, "supportsMidConvoEffort") ?? detected.SupportsMidConvoEffort,
        };
    }

    private static string DetectSessionAffinityFormat(Model model)
        => model.Provider == "openrouter" || model.BaseUrl.Contains("openrouter.ai", StringComparison.Ordinal)
            ? "openrouter"
            : "openai";

    /// <summary>pi getConfigurationUpdateEffort 107–122（ChatGPT sign-in 分支属 D18，不实现）。</summary>
    private static string? GetConfigurationUpdateEffort(
        Model model, OpenAiResponsesOptions? options, OpenAiResponsesCompat compat)
    {
        if (!compat.SupportsMidConvoEffort || options?.ReasoningEffort is not { } requested)
        {
            return null;
        }

        return MappedOrRequested(model, requested) ?? ToThinkingLevelWire(requested);
    }

    private static string? GetPromptCacheRetention(OpenAiResponsesCompat compat, CacheRetention cacheRetention)
        => cacheRetention == CacheRetention.Long
            && compat.SupportsLongCacheRetention
            && !compat.SupportsExplicitPromptCacheMode
            ? "24h"
            : null;

    private static JsonObject? GetPromptCacheOptions(OpenAiResponsesCompat compat, CacheRetention cacheRetention)
    {
        if (!compat.SupportsExplicitPromptCacheMode) return null;
        if (cacheRetention == CacheRetention.None) return new JsonObject { ["mode"] = "explicit" };
        if (cacheRetention == CacheRetention.Long && compat.SupportsLongCacheRetention)
        {
            return new JsonObject { ["ttl"] = "30m" };
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // buildParams（pi 339–424）
    // ---------------------------------------------------------------------

    internal static JsonObject BuildParams(
        Model model,
        TranscriptContext context,
        StreamOptions? options,
        OpenAiResponsesCompat compat,
        IReadOnlyDictionary<string, string> grammarToolInputProperties,
        string? configurationUpdateEffort)
    {
        var responsesOptions = options as OpenAiResponsesOptions;
        var transcriptTools = ResolveTranscriptTools(context.Messages, GetResponsesToolSupport(compat));
        var messages = ConvertResponsesMessages(model, context, OpenAiToolCallProviders,
            new ConvertResponsesMessagesOptions
            {
                GrammarToolInputProperties = grammarToolInputProperties,
                SupportsMidConvoSystemMessages = compat.SupportsMidConvoSystemMessages,
                SupportsAdditionalTools = compat.SupportsAdditionalTools,
                SupportsToolSearch = compat.SupportsToolSearch,
                ToolOptions = new ConvertResponsesToolsOptions
                {
                    SupportsStrictMode = compat.SupportsStrictMode,
                    SupportsOpenAIGrammarTools = compat.SupportsOpenAIGrammarTools,
                },
                ConfigurationUpdateEffort = configurationUpdateEffort,
            });

        var cacheRetention = ResolveCacheRetention(options?.CacheRetention, options?.Env);
        var parameters = new JsonObject
        {
            ["model"] = model.Id,
            ["input"] = messages,
            ["stream"] = true,
        };

        if (cacheRetention != CacheRetention.None
            && ClampOpenAIPromptCacheKey(options?.SessionId) is { } promptCacheKey)
        {
            parameters["prompt_cache_key"] = promptCacheKey;
        }

        if (GetPromptCacheRetention(compat, cacheRetention) is { } promptCacheRetention)
        {
            parameters["prompt_cache_retention"] = promptCacheRetention;
        }

        if (GetPromptCacheOptions(compat, cacheRetention) is { } promptCacheOptions)
        {
            parameters["prompt_cache_options"] = promptCacheOptions;
        }

        parameters["store"] = false;

        if (options?.MaxTokens is { } maxTokens && maxTokens > 0 && compat.SupportsMaxOutputTokens)
        {
            parameters["max_output_tokens"] = Math.Max(maxTokens, OpenAiResponsesMinOutputTokens);
        }

        if (options?.Temperature is { } temperature)
        {
            parameters["temperature"] = temperature;
        }

        if (responsesOptions?.ServiceTier is { } serviceTier)
        {
            parameters["service_tier"] = serviceTier;
        }

        if (transcriptTools.RequestTools.Count > 0)
        {
            parameters["tools"] = ConvertResponsesTools(transcriptTools.RequestTools, new ConvertResponsesToolsOptions
            {
                SupportsStrictMode = compat.SupportsStrictMode,
                SupportsOpenAIGrammarTools = compat.SupportsOpenAIGrammarTools,
            });
        }

        if (responsesOptions?.ToolChoice is { } toolChoice)
        {
            parameters["tool_choice"] = toolChoice.DeepClone();
        }

        // pi：reasoningEffort = options.reasoningEffort ??（有 reasoningSummary 时 "medium"）。
        var effortForSampling = responsesOptions?.ReasoningEffort
            ?? (responsesOptions?.ReasoningSummary is not null ? ThinkingLevel.Medium : (ThinkingLevel?)null);
        if (model.Reasoning)
        {
            if (effortForSampling is { } requested)
            {
                var requestedEffort = MappedOrRequested(model, requested) ?? ToThinkingLevelWire(requested);
                var effort = configurationUpdateEffort is null ? requestedEffort : ConfigurationUpdateRequestEffort;
                parameters["reasoning"] = new JsonObject
                {
                    ["effort"] = effort,
                    ["summary"] = responsesOptions?.ReasoningSummary switch
                    {
                        OpenAiReasoningSummary.Detailed => "detailed",
                        OpenAiReasoningSummary.Concise => "concise",
                        _ => "auto",
                    },
                };
                parameters["include"] = new JsonArray("reasoning.encrypted_content");
            }
            else if (model.Provider != "github-copilot" && !OffExplicitlyDisabled(model))
            {
                // pi：thinkingLevelMap?.off !== null → 发 reasoning.effort（map 缺失/off 缺省/off 非 null 均成立）。
                parameters["reasoning"] = new JsonObject
                {
                    ["effort"] = OffMapping(model) ?? "none",
                };
            }

            if (model.Provider == "xai")
            {
                parameters["include"] = new JsonArray("reasoning.encrypted_content");
            }
        }

        // 最后合并：模型/请求级采样参数覆盖具名请求字段。
        var samplingLevel = effortForSampling is { } samplingEffort
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

    // ---------------------------------------------------------------------
    // service tier 定价（pi 425–453）
    // ---------------------------------------------------------------------

    private static double GetServiceTierCostMultiplier(Model model, string? serviceTier)
        => serviceTier switch
        {
            "flex" => 0.5,
            "priority" or "fast" => model.Id == "gpt-5.5" ? 2.5 : 2,
            _ => 1,
        };

    private static Usage ApplyServiceTierPricing(Usage usage, string? serviceTier, Model model)
    {
        var multiplier = GetServiceTierCostMultiplier(model, serviceTier);
        if (multiplier == 1) return usage;

        var cost = usage.Cost;
        return usage with
        {
            Cost = cost with
            {
                Input = cost.Input * multiplier,
                Output = cost.Output * multiplier,
                CacheRead = cost.CacheRead * multiplier,
                CacheWrite = cost.CacheWrite * multiplier,
                Total = (cost.Input + cost.Output + cost.CacheRead + cost.CacheWrite) * multiplier,
            },
        };
    }

    // ---------------------------------------------------------------------
    // convertResponsesMessages（pi openai-responses-shared.ts 192–438）
    // ---------------------------------------------------------------------

    internal sealed record ConvertResponsesMessagesOptions
    {
        /// <summary>pi includeSystemPrompt ?? true。</summary>
        public bool IncludeSystemPrompt { get; init; } = true;

        public IReadOnlyDictionary<string, string>? GrammarToolInputProperties { get; init; }

        public bool SupportsMidConvoSystemMessages { get; init; }

        public bool SupportsAdditionalTools { get; init; }

        public bool SupportsToolSearch { get; init; }

        public ConvertResponsesToolsOptions? ToolOptions { get; init; }

        /// <summary>
        /// pi configurationUpdateEffort：本次回复的思考档位以 configuration_update 项发送，
        /// 之前的输入永远不变（保 prompt cache）。
        /// </summary>
        public string? ConfigurationUpdateEffort { get; init; }
    }

    internal sealed record ConvertResponsesToolsOptions
    {
        /// <summary>pi strict?: boolean | null。</summary>
        public bool? Strict { get; init; }

        /// <summary>pi supportsStrictMode ?? true。</summary>
        public bool SupportsStrictMode { get; init; } = true;

        public bool SupportsOpenAIGrammarTools { get; init; }

        public bool ToolSearchResult { get; init; }
    }

    /// <summary>pi getResponsesToolSupport 167–174。</summary>
    private static TranscriptToolSupport GetResponsesToolSupport(OpenAiResponsesCompat compat)
        => compat.SupportsMidConvoSystemMessages && compat.SupportsAdditionalTools
            ? TranscriptToolSupport.Changes
            : compat.SupportsToolSearch ? TranscriptToolSupport.Additions : TranscriptToolSupport.None;

    private static TranscriptToolSupport GetResponsesToolSupport(ConvertResponsesMessagesOptions options)
        => options.SupportsMidConvoSystemMessages && options.SupportsAdditionalTools
            ? TranscriptToolSupport.Changes
            : options.SupportsToolSearch ? TranscriptToolSupport.Additions : TranscriptToolSupport.None;

    /// <summary>
    /// pi renderIncrementalToolsNotice 182–186：每个后续 additional_tools 项的前导说明。
    /// </summary>
    private static string RenderIncrementalToolsNotice(HashSet<string> unavailable)
    {
        const string notice =
            "This is an incremental tools update. Previously declared tools remain available unless explicitly "
            + "marked unavailable. A tool declared again here is available again, and its latest definition replaces "
            + "the earlier one.";
        return unavailable.Count > 0
            ? $"{notice}\nStill unavailable: {string.Join(", ", unavailable)}."
            : notice;
    }

    private static string RenderToolRemovalNotice(IReadOnlyList<string> names)
        => $"The following tools are no longer available. Do not call them:\n"
           + string.Join("\n", names.Select(name => $"- {name}"));

    internal static JsonArray ConvertResponsesMessages(
        Model model,
        TranscriptContext context,
        IReadOnlyCollection<string> allowedToolCallProviders,
        ConvertResponsesMessagesOptions? options)
    {
        var normalizedContext = ResolveTranscript(context, options?.SupportsMidConvoSystemMessages ?? false);
        var messages = new JsonArray();

        static string NormalizeIdPart(string part)
        {
            var sanitized = new string(part.Select(ch =>
                (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || char.IsDigit(ch) || ch is '_' or '-'
                    ? ch
                    : '_').ToArray());
            if (sanitized.Length > 64)
            {
                sanitized = sanitized[..64];
            }

            return sanitized.TrimEnd('_');
        }

        static string BuildForeignResponsesItemId(string itemId)
        {
            var normalized = $"fc_{ShortHash(itemId)}";
            return normalized.Length > 64 ? normalized[..64] : normalized;
        }

        string NormalizeToolCallId(string id, AssistantMessage source)
        {
            if (!allowedToolCallProviders.Contains(model.Provider)) return NormalizeIdPart(id);
            if (!id.Contains('|')) return NormalizeIdPart(id);

            var separatorIndex = id.IndexOf('|');
            var callId = id[..separatorIndex];
            var itemId = id[(separatorIndex + 1)..];
            var normalizedCallId = NormalizeIdPart(callId);
            // 跨 provider/api 的工具调用按外来 ID 重造（fc_ + hash）；同源时保留 item 级唯一性。
            var isForeignToolCall = source.Provider != model.Provider || source.Api != model.Api;
            var normalizedItemId = isForeignToolCall ? BuildForeignResponsesItemId(itemId) : NormalizeIdPart(itemId);
            // OpenAI Responses API 要求 item id 以 "fc" 开头。
            if (!normalizedItemId.StartsWith("fc_", StringComparison.Ordinal))
            {
                normalizedItemId = NormalizeIdPart($"fc_{normalizedItemId}");
            }

            return $"{normalizedCallId}|{normalizedItemId}";
        }

        var transformedMessages = TransformMessages(normalizedContext.Messages, model, NormalizeToolCallId);
        var toolSupport = GetResponsesToolSupport(options ?? new ConvertResponsesMessagesOptions());
        var transcriptTools = ResolveTranscriptTools(normalizedContext.Messages, toolSupport);
        var declaredAdditionalTools = false;
        // 已宣布移除且未再声明的工具。
        var unavailableTools = new HashSet<string>();
        var includeInitialSystemMessage = options?.IncludeSystemPrompt ?? true;
        // pi：instructionRole 直读 raw model.compat.supportsDeveloperRole（!== false 才 developer）。
        var instructionRole = model.Reasoning
            && !(model.Compat is { } rawCompat && JsonBool(rawCompat, "supportsDeveloperRole") == false)
            ? "developer"
            : "system";

        void PushConfigurationUpdate(string effort)
        {
            messages.Add(new JsonObject
            {
                ["type"] = "configuration_update",
                ["reasoning"] = new JsonObject { ["effort"] = effort },
            });
        }

        void AppendSystemToolAdditions(SystemMessage message, string seed)
        {
            var tools = message.ToolsAdded ?? Array.Empty<Tool>();
            foreach (var tool in tools)
            {
                unavailableTools.Remove(tool.Name);
            }

            if (tools.Count == 0) return;
            if (toolSupport == TranscriptToolSupport.Changes)
            {
                if (declaredAdditionalTools)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = instructionRole,
                        ["content"] = RenderIncrementalToolsNotice(unavailableTools),
                    });
                }

                declaredAdditionalTools = true;
                messages.Add(new JsonObject
                {
                    ["type"] = "additional_tools",
                    ["role"] = "developer",
                    ["tools"] = ConvertResponsesTools(tools, options?.ToolOptions),
                });
                return;
            }

            if (toolSupport != TranscriptToolSupport.Additions) return;
            var names = tools.Select(tool => tool.Name).ToList();
            var callId = $"pi_tool_load_{ShortHash($"{seed}:{string.Join(",", names)}")}";
            messages.Add(new JsonObject
            {
                ["type"] = "tool_search_call",
                ["call_id"] = callId,
                ["execution"] = "client",
                ["status"] = "completed",
                ["arguments"] = new JsonObject
                {
                    ["query"] = string.Join(" ", names),
                    ["limit"] = names.Count,
                },
            });
            messages.Add(new JsonObject
            {
                ["type"] = "tool_search_output",
                ["call_id"] = callId,
                ["execution"] = "client",
                ["status"] = "completed",
                ["tools"] = ConvertResponsesTools(
                    tools,
                    (options?.ToolOptions ?? new ConvertResponsesToolsOptions()) with { ToolSearchResult = true }),
            });
        }

        var msgIndex = 0;
        var sourceIndex = 0;
        var systemIndex = 0;

        foreach (var msg in transformedMessages)
        {
            var isLeadingSystemMessage = sourceIndex++ == 0 && msg is SystemMessage;
            switch (msg)
            {
                case SystemMessage systemMessage:
                {
                    // transformMessages 保持系统消息顺序，序号与 transcript 一致。
                    var anchorsToolChanges = systemIndex++ >= transcriptTools.FoldedSystemMessages;
                    if (anchorsToolChanges)
                    {
                        AppendSystemToolAdditions(systemMessage, $"system:{msgIndex}");
                    }

                    if (!isLeadingSystemMessage || includeInitialSystemMessage)
                    {
                        var parts = new List<string>
                        {
                            isLeadingSystemMessage
                                ? GetSystemMessageText(systemMessage)
                                : RenderSystemMessageUpdate(systemMessage),
                        };
                        if (anchorsToolChanges)
                        {
                            // 重定义 = 同名移除 + 同名添加；只有真移除才宣布。
                            var added = new HashSet<string>(
                                (systemMessage.ToolsAdded ?? Array.Empty<Tool>()).Select(tool => tool.Name));
                            var removed = (systemMessage.ToolsRemoved ?? Array.Empty<ToolReference>())
                                .Select(tool => tool.Name)
                                .Where(name => !added.Contains(name))
                                .ToList();
                            if (removed.Count > 0)
                            {
                                parts.Add(RenderToolRemovalNotice(removed));
                            }

                            // 在本消息的 additions 之后记录（其 notice 已先于本移除 notice）。
                            foreach (var name in removed)
                            {
                                unavailableTools.Add(name);
                            }
                        }

                        var text = string.Join("\n\n", parts.Where(part => part.Length > 0));
                        if (text.Length > 0)
                        {
                            messages.Add(new JsonObject
                            {
                                ["role"] = instructionRole,
                                ["content"] = SanitizeSurrogates(text),
                            });
                        }
                    }

                    break;
                }

                case UserMessage userMessage:
                {
                    if (userMessage.Content is MessageContent.Text textContent)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "input_text",
                                ["text"] = SanitizeSurrogates(textContent.Value),
                            }),
                        });
                        break;
                    }

                    var content = new JsonArray();
                    foreach (var item in userMessage.Content.ToBlocks())
                    {
                        switch (item)
                        {
                            case TextContent text:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "input_text",
                                    ["text"] = SanitizeSurrogates(text.Text),
                                });
                                break;
                            case ImageContent image:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "input_image",
                                    ["detail"] = "auto",
                                    ["image_url"] = $"data:{image.MimeType};base64,{image.Data}",
                                });
                                break;
                        }
                    }

                    if (content.Count == 0) continue;
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = content,
                    });

                    break;
                }

                case AssistantMessage assistant:
                {
                    // pi output: ResponseInput 数组先收集再摊入 messages；C# 用 List 避免同一节点
                    // 二次挂父（STJ 对已挂父节点 Add 会抛 InvalidOperationException）。
                    var output = new List<JsonNode>();
                    var isSameProviderAndApi = assistant.Provider == model.Provider && assistant.Api == model.Api;
                    var isSameModel = isSameProviderAndApi && assistant.Model == model.Id;
                    var isDifferentModel = isSameProviderAndApi && assistant.Model != model.Id;
                    var textBlockIndex = 0;

                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case ThinkingContent thinking:
                            {
                                if (thinking.ThinkingSignature is not null)
                                {
                                    // pi：JSON.parse(thinkingSignature) as ResponseReasoningItem —— 原样回放；
                                    // 非法签名同样抛错进终态 Error（JSON 字面 null 会产出 null item，pi 同）。
                                    output.Add(JsonNode.Parse(thinking.ThinkingSignature)!);
                                }

                                break;
                            }

                            case TextContent text:
                            {
                                var parsedSignature = ParseTextSignature(text.TextSignature);
                                var fallbackMessageId = textBlockIndex == 0
                                    ? $"msg_pi_{msgIndex}"
                                    : $"msg_pi_{msgIndex}_{textBlockIndex}";
                                textBlockIndex++;
                                // OpenAI 要求 id 最长 64 字符。
                                var msgId = parsedSignature?.Id ?? fallbackMessageId;
                                if (msgId.Length > 64)
                                {
                                    msgId = $"msg_{ShortHash(msgId)}";
                                }

                                var messageItem = new JsonObject
                                {
                                    ["type"] = "message",
                                    ["role"] = "assistant",
                                    ["content"] = new JsonArray(new JsonObject
                                    {
                                        ["type"] = "output_text",
                                        ["text"] = SanitizeSurrogates(text.Text),
                                        ["annotations"] = new JsonArray(),
                                    }),
                                    ["status"] = "completed",
                                    ["id"] = msgId,
                                };
                                if (parsedSignature?.Phase is { } phase)
                                {
                                    messageItem["phase"] = phase;
                                }

                                output.Add(messageItem);
                                break;
                            }

                            case ToolCall toolCall:
                            {
                                var pipeIndex = toolCall.Id.IndexOf('|');
                                var callId = pipeIndex >= 0 ? toolCall.Id[..pipeIndex] : toolCall.Id;
                                var itemIdRaw = pipeIndex >= 0 ? toolCall.Id[(pipeIndex + 1)..] : null;
                                var customInputProperty =
                                    options?.GrammarToolInputProperties is { } properties
                                    && properties.TryGetValue(toolCall.Name, out var propertyName)
                                        ? propertyName
                                        : null;
                                var itemId = itemIdRaw;

                                // 不同模型的消息 id 置空以避开配对校验（OpenAI 记录 rs_xxx 配对过的 item id）；
                                // 前缀不匹配回放类型的 id 同样丢弃（function_call 要 fc_*，custom_tool_call 要 ctc_*）。
                                var itemIdPrefix = customInputProperty is null ? "fc_" : "ctc_";
                                if (isDifferentModel
                                    || itemId is null
                                    || !itemId.StartsWith(itemIdPrefix, StringComparison.Ordinal))
                                {
                                    itemId = null;
                                }

                                if (customInputProperty is not null)
                                {
                                    var customItem = new JsonObject
                                    {
                                        ["type"] = "custom_tool_call",
                                        ["call_id"] = callId,
                                        ["name"] = toolCall.Name,
                                        ["input"] = SanitizeSurrogates(GetGrammarToolInput(
                                            toolCall.Name, toolCall.Arguments, customInputProperty)),
                                    };
                                    if (itemId is not null) customItem["id"] = itemId;
                                    if (isSameModel && toolCall.Namespace is not null)
                                    {
                                        customItem["namespace"] = toolCall.Namespace;
                                    }

                                    output.Add(customItem);
                                }
                                else
                                {
                                    var functionItem = new JsonObject
                                    {
                                        ["type"] = "function_call",
                                        ["call_id"] = callId,
                                        ["name"] = toolCall.Name,
                                        ["arguments"] = toolCall.Arguments.ToJsonString(),
                                    };
                                    if (itemId is not null) functionItem["id"] = itemId;
                                    if (isSameModel && toolCall.Namespace is not null)
                                    {
                                        functionItem["namespace"] = toolCall.Namespace;
                                    }

                                    output.Add(functionItem);
                                }

                                break;
                            }
                        }
                    }

                    if (output.Count == 0) continue;
                    if (options?.ConfigurationUpdateEffort is not null
                        && isSameModel
                        && assistant.ProviderThinkingLevel is { } providerThinkingLevel)
                    {
                        PushConfigurationUpdate(providerThinkingLevel);
                    }

                    foreach (var node in output)
                    {
                        messages.Add(node);
                    }

                    break;
                }

                case ToolResultMessage toolResult:
                {
                    var pipeIndex = toolResult.ToolCallId.IndexOf('|');
                    var callId = pipeIndex >= 0 ? toolResult.ToolCallId[..pipeIndex] : toolResult.ToolCallId;
                    var output = ConvertToolResultOutput(model, toolResult.Content);

                    if (options?.GrammarToolInputProperties is { } toolProperties
                        && toolProperties.ContainsKey(toolResult.ToolName))
                    {
                        messages.Add(new JsonObject
                        {
                            ["type"] = "custom_tool_call_output",
                            ["call_id"] = callId,
                            ["output"] = output,
                        });
                    }
                    else
                    {
                        messages.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = callId,
                            ["output"] = output,
                        });
                    }

                    break;
                }

                default:
                    break;
            }

            if (!isLeadingSystemMessage) msgIndex++;
        }

        if (options?.ConfigurationUpdateEffort is { } finalEffort)
        {
            PushConfigurationUpdate(finalEffort);
        }

        return messages;
    }

    /// <summary>pi convertToolResultOutput 83–110：文本合并；有图且模型支持图时输出 input_image 数组。</summary>
    private static JsonNode ConvertToolResultOutput(Model model, IReadOnlyList<ContentBlock> content)
    {
        var textResult = string.Join("\n", content.OfType<TextContent>().Select(block => block.Text));
        var images = content.OfType<ImageContent>().ToList();
        var hasText = textResult.Trim().Length > 0;

        if (images.Count == 0 || !model.Input.Contains(ModelInputModality.Image))
        {
            return SanitizeSurrogates(
                hasText ? textResult : images.Count > 0 ? "(see attached image)" : "(no tool output)");
        }

        var output = new JsonArray();
        if (hasText)
        {
            output.Add(new JsonObject { ["type"] = "input_text", ["text"] = SanitizeSurrogates(textResult) });
        }

        foreach (var image in images)
        {
            output.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["detail"] = "auto",
                ["image_url"] = $"data:{image.MimeType};base64,{image.Data}",
            });
        }

        return output;
    }

    private sealed record ParsedTextSignature(string Id, string? Phase);

    /// <summary>pi parseTextSignature 61–79：TextSignatureV1 JSON 或旧式纯字符串 id。</summary>
    private static ParsedTextSignature? ParseTextSignature(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        if (signature.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                if (JsonNode.Parse(signature) is JsonObject parsed
                    && JsonNum(parsed, "v") is { } version && version == 1
                    && JsonStr(parsed, "id") is { } id)
                {
                    var phase = JsonStr(parsed, "phase");
                    if (phase is "commentary" or "final_answer")
                    {
                        return new ParsedTextSignature(id, phase);
                    }

                    return new ParsedTextSignature(id, null);
                }
            }
            catch (JsonException)
            {
                // 落到旧式纯字符串处理。
            }
        }

        return new ParsedTextSignature(signature, null);
    }

    private static string EncodeTextSignatureV1(string? id, string? phase)
    {
        var payload = new JsonObject { ["v"] = 1 };
        if (!string.IsNullOrEmpty(id)) payload["id"] = id;
        if (!string.IsNullOrEmpty(phase)) payload["phase"] = phase;
        return payload.ToJsonString();
    }

    // ---------------------------------------------------------------------
    // convertResponsesTools（pi 439–485）
    // ---------------------------------------------------------------------

    internal static JsonArray ConvertResponsesTools(IReadOnlyList<Tool> tools, ConvertResponsesToolsOptions? options)
    {
        var defaultStrict = options?.Strict ?? false;
        var supportsStrictMode = options?.SupportsStrictMode ?? true;
        var supportsOpenAIGrammarTools = options?.SupportsOpenAIGrammarTools ?? false;
        var result = new JsonArray();

        foreach (var tool in tools)
        {
            if (ResolveGrammarConstrainedSampling(tool, supportsOpenAIGrammarTools) is { } grammar)
            {
                var grammarTool = new JsonObject
                {
                    ["type"] = "custom",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["format"] = new JsonObject
                    {
                        ["type"] = "grammar",
                        ["syntax"] = grammar.Format == GrammarFormatLark ? "lark" : "regex",
                        ["definition"] = grammar.Definition,
                    },
                };
                if (options?.ToolSearchResult == true) grammarTool["defer_loading"] = true;
                result.Add(grammarTool);
                continue;
            }

            var strict = ResolveJsonSchemaStrictSampling(tool, supportsStrictMode) ?? defaultStrict;
            var functionTool = new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = GetJsonSchemaToolParameters(tool, strict),
            };
            if (options?.ToolSearchResult == true) functionTool["defer_loading"] = true;
            if (supportsStrictMode)
            {
                functionTool["strict"] = strict;
            }

            result.Add(functionTool);
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // processResponsesStream（pi 503–891）
    // ---------------------------------------------------------------------

    private sealed record ResponsesStreamOptions
    {
        public Func<JsonNode, ProviderModel, Task>? OnProviderStreamEvent { get; init; }

        public string? ServiceTier { get; init; }

        public IReadOnlyDictionary<string, string>? GrammarToolInputProperties { get; init; }

        public Func<Usage, string?, Usage>? ApplyServiceTierPricing { get; init; }
    }

    private enum SlotKind
    {
        Thinking,
        Text,
        ToolCall,
    }

    /// <summary>
    /// pi ResponsesOutputSlot：按 output_index 挂槽位，流状态（tool call 参数累积器等）
    /// 永远在槽上、不在内容块上——永不进入 partial。
    /// </summary>
    private sealed class ResponsesOutputSlot
    {
        public required SlotKind Kind { get; init; }
        public int ContentIndex { get; init; }
        public ThinkingStreamBlock? Thinking { get; init; }
        public TextStreamBlock? Text { get; init; }
        public ToolCallStreamBlock? ToolCall { get; init; }

        /// <summary>function_call 的 JSON 参数累积器（kind: json）。</summary>
        public ToolCallArguments? JsonArguments { get; set; }

        /// <summary>custom（grammar）工具输入的属性名 / JSON 缓冲 / 未发送前缀（kind: custom）。</summary>
        public string? CustomProperty { get; set; }
        public GrammarToolInputJsonBuffer? CustomBuffer { get; set; }
        public string Unsent { get; set; } = "";
    }

    private static async Task ProcessResponsesStream(
        Stream body,
        MutableOutput output,
        List<StreamBlock> blocks,
        AssistantMessageEventStream stream,
        Model model,
        ResponsesStreamOptions options,
        CancellationToken cancellationToken)
    {
        var sawTerminalResponseEvent = false;
        var outputSlots = new Dictionary<int, ResponsesOutputSlot>();
        // output_item.done 尚未到达的 tool call。
        var unfinishedToolCalls = new HashSet<ToolCallStreamBlock>();
        var reasoningBlocksById = new Dictionary<string, ThinkingStreamBlock>();

        void PushToolCallDelta(ResponsesOutputSlot slot, string? delta)
        {
            if (delta is null) return;
            stream.Push(new AssistantMessageEvent.ToolCallDelta(
                slot.ContentIndex, delta, output.Snapshot(blocks)));
        }

        void ApplyMessagePhaseStopReason(JsonObject item)
        {
            if (JsonStr(item, "type") == "message" && JsonStr(item, "phase") == "final_answer")
            {
                output.StopReason = StopReason.Stop;
            }
        }

        ResponsesOutputSlot? CreateSlot(int outputIndex, JsonObject item)
        {
            switch (JsonStr(item, "type"))
            {
                case "reasoning":
                {
                    var block = new ThinkingStreamBlock();
                    blocks.Add(block);
                    var slot = new ResponsesOutputSlot
                    {
                        Kind = SlotKind.Thinking,
                        Thinking = block,
                        ContentIndex = blocks.Count - 1,
                    };
                    outputSlots[outputIndex] = slot;
                    stream.Push(new AssistantMessageEvent.ThinkingStart(
                        slot.ContentIndex, output.Snapshot(blocks)));
                    return slot;
                }

                case "message":
                {
                    ApplyMessagePhaseStopReason(item);
                    var block = new TextStreamBlock();
                    blocks.Add(block);
                    var slot = new ResponsesOutputSlot
                    {
                        Kind = SlotKind.Text,
                        Text = block,
                        ContentIndex = blocks.Count - 1,
                    };
                    outputSlots[outputIndex] = slot;
                    stream.Push(new AssistantMessageEvent.TextStart(slot.ContentIndex, output.Snapshot(blocks)));
                    return slot;
                }

                case "function_call":
                {
                    // pi openai-responses-shared.ts:559：初始 arguments 也走 append，partial 进 ToolCallStart 快照。
                    var arguments = new ToolCallArguments();
                    var initialArguments = JsonStr(item, "arguments") ?? "";
                    var block = new ToolCallStreamBlock
                    {
                        Id = $"{JsonStr(item, "call_id") ?? ""}|{JsonStr(item, "id") ?? ""}",
                        Name = JsonStr(item, "name") ?? "",
                        Arguments = initialArguments.Length > 0
                            ? arguments.Append(initialArguments)
                            : new JsonObject(),
                    };
                    if (JsonStr(item, "namespace") is { } functionNamespace)
                    {
                        block.Namespace = functionNamespace;
                    }

                    blocks.Add(block);
                    unfinishedToolCalls.Add(block);
                    var slot = new ResponsesOutputSlot
                    {
                        Kind = SlotKind.ToolCall,
                        ToolCall = block,
                        ContentIndex = blocks.Count - 1,
                        JsonArguments = arguments,
                    };
                    outputSlots[outputIndex] = slot;
                    stream.Push(new AssistantMessageEvent.ToolCallStart(
                        slot.ContentIndex, output.Snapshot(blocks)));
                    return slot;
                }

                case "custom_tool_call":
                {
                    var name = JsonStr(item, "name") ?? "";
                    var inputProperty = options.GrammarToolInputProperties is { } properties
                        && properties.TryGetValue(name, out var property)
                            ? property
                            : "input";
                    var input = JsonStr(item, "input") ?? "";
                    var block = new ToolCallStreamBlock
                    {
                        Id = $"{JsonStr(item, "call_id") ?? ""}|{JsonStr(item, "id") ?? ""}",
                        Name = name,
                        Arguments = new JsonObject { [inputProperty] = input },
                    };
                    if (JsonStr(item, "namespace") is { } customNamespace)
                    {
                        block.Namespace = customNamespace;
                    }

                    blocks.Add(block);
                    unfinishedToolCalls.Add(block);
                    var buffer = new GrammarToolInputJsonBuffer();
                    var slot = new ResponsesOutputSlot
                    {
                        Kind = SlotKind.ToolCall,
                        ToolCall = block,
                        ContentIndex = blocks.Count - 1,
                        CustomProperty = inputProperty,
                        CustomBuffer = buffer,
                        Unsent = AppendGrammarToolInputJsonDelta(buffer, inputProperty, input) ?? "",
                    };
                    outputSlots[outputIndex] = slot;
                    stream.Push(new AssistantMessageEvent.ToolCallStart(
                        slot.ContentIndex, output.Snapshot(blocks)));
                    return slot;
                }

                default:
                    return null;
            }
        }

        ResponsesOutputSlot? GetOrCreateSlot(int outputIndex, JsonObject item)
            => outputSlots.TryGetValue(outputIndex, out var existing) ? existing : CreateSlot(outputIndex, item);

        // Azure OpenAI 可能在 response.output_item.done 里省略 reasoning.encrypted_content，
        // 只在 response.completed.response.output 里提供：回填持久化的 reasoning 签名，
        // 让 store:false 的多轮重放保持无状态（pi#6409）。
        void BackfillReasoningSignatures(JsonArray? responseOutput)
        {
            if (responseOutput is null) return;
            foreach (var node in responseOutput)
            {
                if (node is not JsonObject item || JsonStr(item, "type") != "reasoning") continue;
                var encryptedContent = JsonStr(item, "encrypted_content");
                if (string.IsNullOrEmpty(encryptedContent)) continue;
                if (!reasoningBlocksById.TryGetValue(JsonStr(item, "id") ?? "", out var block)) continue;
                if (string.IsNullOrEmpty(block.ThinkingSignature)) continue;

                var storedItem = (JsonObject)JsonNode.Parse(block.ThinkingSignature)!;
                if (JsonStr(storedItem, "encrypted_content") is { Length: > 0 }) continue;
                storedItem["encrypted_content"] = encryptedContent;
                block.ThinkingSignature = storedItem.ToJsonString();
            }
        }

        void FinalizeResponse(JsonObject? response)
        {
            sawTerminalResponseEvent = true;
            BackfillReasoningSignatures(response?["output"] as JsonArray);
            if (JsonStr(response, "id") is { } responseId)
            {
                output.ResponseId = responseId;
            }

            if (JsonObj(response, "usage") is { } usage)
            {
                var inputDetails = JsonObj(usage, "input_tokens_details");
                var cachedTokens = (int)(JsonNum(inputDetails, "cached_tokens") ?? 0);
                var cacheWriteTokens = (int)(JsonNum(inputDetails, "cache_write_tokens") ?? 0);
                // OpenAI 把 cached 与 cache-write 计入 input_tokens，两项都要减掉。
                output.Usage = new Usage
                {
                    Input = Math.Max(0, (int)(JsonNum(usage, "input_tokens") ?? 0) - cachedTokens - cacheWriteTokens),
                    Output = (int)(JsonNum(usage, "output_tokens") ?? 0),
                    CacheRead = cachedTokens,
                    CacheWrite = cacheWriteTokens,
                    Reasoning = (int)(JsonNum(JsonObj(usage, "output_tokens_details"), "reasoning_tokens") ?? 0)
                        is { } reasoning && reasoning > 0 ? reasoning : null,
                    TotalTokens = (int)(JsonNum(usage, "total_tokens") ?? 0),
                };
            }

            // pi：calculateCost 不依赖 usage 是否上报（未上报时对零用量计算，成本为零）。
            output.Usage = output.Usage with { Cost = ModelOperations.CalculateCost(model, output.Usage) };
            if (options.ApplyServiceTierPricing is { } applyServiceTierPricing)
            {
                var serviceTier = JsonStr(response, "service_tier") ?? options.ServiceTier;
                output.Usage = applyServiceTierPricing(output.Usage, serviceTier);
            }

            // status → stop reason。incomplete 保留 provider 的具体原因，
            // 让 max-output 截断与内容过滤保持可区分。
            var status = JsonStr(response, "status");
            var incompleteReason = JsonStr(JsonObj(response, "incomplete_details"), "reason");
            output.RawStopReason = incompleteReason is { Length: > 0 } ? $"{status}.{incompleteReason}" : status;
            var mapped = MapStopReason(status, incompleteReason);
            output.StopReason = mapped.StopReason;
            output.ErrorMessage = mapped.ErrorMessage;
            if (blocks.Any(block => block is ToolCallStreamBlock) && output.StopReason == StopReason.Stop)
            {
                output.StopReason = StopReason.ToolUse;
            }
        }

        await foreach (var sse in SseEventReader.ReadAsync(body, cancellationToken).ConfigureAwait(false))
        {
            // OpenAI SDK 吞掉 [DONE]；data 为空的事件也不产 event。
            if (sse.Data == "[DONE]") continue;
            if (string.IsNullOrWhiteSpace(sse.Data)) continue;
            if (JsonNode.Parse(sse.Data) is not JsonObject @event)
            {
                throw new InvalidOperationException("OpenAI Responses SSE data is not a JSON object");
            }

            if (options.OnProviderStreamEvent is { } observer)
            {
                await observer(@event, model).ConfigureAwait(false);
            }

            var outputIndex = JsonNum(@event, "output_index") is { } index ? (int)index : MissingOutputIndex;
            switch (JsonStr(@event, "type"))
            {
                case "response.created":
                {
                    if (JsonObj(@event, "response") is { } createdResponse)
                    {
                        output.ResponseId = JsonStr(createdResponse, "id");
                    }

                    break;
                }

                case "response.output_item.added":
                {
                    if (JsonObj(@event, "item") is { } addedItem)
                    {
                        CreateSlot(outputIndex, addedItem);
                    }

                    break;
                }

                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                {
                    if (!outputSlots.TryGetValue(outputIndex, out var slot) || slot.Kind != SlotKind.Thinking) continue;
                    var delta = JsonStr(@event, "delta") ?? "";
                    slot.Thinking!.Thinking += delta;
                    stream.Push(new AssistantMessageEvent.ThinkingDelta(
                        slot.ContentIndex, delta, output.Snapshot(blocks)));
                    break;
                }

                case "response.reasoning_summary_part.done":
                {
                    if (!outputSlots.TryGetValue(outputIndex, out var slot) || slot.Kind != SlotKind.Thinking) continue;
                    slot.Thinking!.Thinking += "\n\n";
                    stream.Push(new AssistantMessageEvent.ThinkingDelta(
                        slot.ContentIndex, "\n\n", output.Snapshot(blocks)));
                    break;
                }

                case "response.output_text.delta":
                case "response.refusal.delta":
                {
                    if (!outputSlots.TryGetValue(outputIndex, out var slot) || slot.Kind != SlotKind.Text) continue;
                    var delta = JsonStr(@event, "delta") ?? "";
                    slot.Text!.Text += delta;
                    stream.Push(new AssistantMessageEvent.TextDelta(
                        slot.ContentIndex, delta, output.Snapshot(blocks)));
                    break;
                }

                case "response.function_call_arguments.delta":
                {
                    if (!outputSlots.TryGetValue(outputIndex, out var slot)
                        || slot.Kind != SlotKind.ToolCall
                        || slot.JsonArguments is null)
                    {
                        continue;
                    }

                    var delta = JsonStr(@event, "delta") ?? "";
                    // pi openai-responses-shared.ts:734：append 的 partial 参数回填块，partial 消息可见。
                    slot.ToolCall!.Arguments = slot.JsonArguments.Append(delta);
                    PushToolCallDelta(slot, delta);
                    break;
                }

                case "response.function_call_arguments.done":
                {
                    if (!outputSlots.TryGetValue(outputIndex, out var slot)
                        || slot.Kind != SlotKind.ToolCall
                        || slot.JsonArguments is null)
                    {
                        continue;
                    }

                    // pi openai-responses-shared.ts:735：replace 的 partial 参数回填块；
                    // `if (replaced.delta)` 真值判断——全量文本与已累积一致（delta 为空串）不推 delta。
                    var (replacedDelta, replacedArguments) =
                        slot.JsonArguments.Replace(JsonStr(@event, "arguments") ?? "");
                    slot.ToolCall!.Arguments = replacedArguments;
                    PushToolCallDelta(slot, replacedDelta is { Length: > 0 } ? replacedDelta : null);
                    break;
                }

                case "response.custom_tool_call_input.delta":
                {
                    if (!outputSlots.TryGetValue(outputIndex, out var slot)
                        || slot.Kind != SlotKind.ToolCall
                        || slot.CustomBuffer is null)
                    {
                        continue;
                    }

                    PushToolCallDelta(slot, AppendCustomInput(slot, JsonStr(@event, "delta") ?? ""));
                    break;
                }

                case "response.custom_tool_call_input.done":
                {
                    if (!outputSlots.TryGetValue(outputIndex, out var slot)
                        || slot.Kind != SlotKind.ToolCall
                        || slot.CustomBuffer is null)
                    {
                        continue;
                    }

                    PushToolCallDelta(slot, CloseCustomInput(slot, JsonStr(@event, "input")));
                    break;
                }

                case "response.output_item.done":
                {
                    if (JsonObj(@event, "item") is not { } item) continue;
                    ApplyMessagePhaseStopReason(item);
                    var slot = GetOrCreateSlot(outputIndex, item);
                    switch (JsonStr(item, "type"))
                    {
                        case "reasoning" when slot?.Kind == SlotKind.Thinking:
                        {
                            var summaryText = JoinItemTextArray(item["summary"] as JsonArray, "\n\n");
                            var contentText = JoinItemTextArray(item["content"] as JsonArray, "\n\n");
                            var block = slot.Thinking!;
                            block.Thinking = summaryText is { Length: > 0 }
                                ? summaryText
                                : contentText is { Length: > 0 } ? contentText : block.Thinking;
                            block.ItemId = JsonStr(item, "id") ?? "";
                            block.ThinkingSignature = item.ToJsonString();
                            reasoningBlocksById[block.ItemId] = block;
                            stream.Push(new AssistantMessageEvent.ThinkingEnd(
                                slot.ContentIndex, block.Thinking, output.Snapshot(blocks)));
                            outputSlots.Remove(outputIndex);
                            break;
                        }

                        case "message" when slot?.Kind == SlotKind.Text:
                        {
                            var block = slot.Text!;
                            block.Text = JoinOutputTextArray(item["content"] as JsonArray);
                            block.TextSignature = EncodeTextSignatureV1(JsonStr(item, "id"), JsonStr(item, "phase"));
                            stream.Push(new AssistantMessageEvent.TextEnd(
                                slot.ContentIndex, block.Text, output.Snapshot(blocks)));
                            outputSlots.Remove(outputIndex);
                            break;
                        }

                        case "function_call" when slot?.Kind == SlotKind.ToolCall && slot.JsonArguments is not null:
                        {
                            if (JsonStr(item, "arguments") is { } finalArguments)
                            {
                                // pi：replace 的返回 delta 在此处不推送，partial 也不回填块（只保 Text 接续，
                                // 随后的 finish 才给块终值）。
                                slot.JsonArguments.Replace(finalArguments);
                            }

                            var block = slot.ToolCall!;
                            // pi：Object.assign(block, arguments.finish())；非法 JSON 不失败流，落 argumentsError。
                            var (arguments, argumentsError) = slot.JsonArguments.Finish();
                            block.Arguments = arguments;
                            block.ArgumentsError = argumentsError;

                            if (JsonStr(item, "namespace") is { } functionNamespace)
                            {
                                block.Namespace = functionNamespace;
                            }

                            unfinishedToolCalls.Remove(block);
                            stream.Push(new AssistantMessageEvent.ToolCallEnd(
                                slot.ContentIndex, block.ToToolCall(), output.Snapshot(blocks)));
                            outputSlots.Remove(outputIndex);
                            break;
                        }

                        case "custom_tool_call" when slot?.Kind == SlotKind.ToolCall && slot.CustomBuffer is not null:
                        {
                            var block = slot.ToolCall!;
                            PushToolCallDelta(slot, CloseCustomInput(slot, JsonStr(item, "input")));
                            if (JsonStr(item, "namespace") is { } customNamespace)
                            {
                                block.Namespace = customNamespace;
                            }

                            unfinishedToolCalls.Remove(block);
                            stream.Push(new AssistantMessageEvent.ToolCallEnd(
                                slot.ContentIndex, block.ToToolCall(), output.Snapshot(blocks)));
                            outputSlots.Remove(outputIndex);
                            break;
                        }
                    }

                    break;
                }

                case "response.completed":
                case "response.incomplete":
                {
                    FinalizeResponse(JsonObj(@event, "response"));
                    break;
                }

                case "error":
                {
                    throw new InvalidOperationException(
                        $"Error Code {JsonStr(@event, "code") ?? "undefined"}: {JsonStr(@event, "message") ?? "undefined"}");
                }

                case "response.failed":
                {
                    sawTerminalResponseEvent = true;
                    var response = JsonObj(@event, "response");
                    output.RawStopReason = JsonStr(response, "status");
                    var error = JsonObj(response, "error");
                    var details = JsonObj(response, "incomplete_details");
                    string message;
                    if (error is not null)
                    {
                        message = $"{JsonStr(error, "code") switch { { Length: > 0 } code => code, _ => "unknown" }}"
                            + $": {JsonStr(error, "message") switch { { Length: > 0 } text => text, _ => "no message" }}";
                    }
                    else if (JsonStr(details, "reason") is { Length: > 0 } reason)
                    {
                        message = $"incomplete: {reason}";
                    }
                    else
                    {
                        message = "Unknown error (no error details in response)";
                    }

                    throw new InvalidOperationException(message);
                }
            }
        }

        if (!sawTerminalResponseEvent)
        {
            throw new InvalidOperationException("OpenAI Responses stream ended before a terminal response event");
        }

        // agent 会执行最终消息里的每个 tool call：output_item.done 未到达的调用拒不交付——
        // 参数可能被截断或错位（不合规服务端漏 output_index 时）。
        if (output.StopReason == StopReason.ToolUse)
        {
            foreach (var toolCall in unfinishedToolCalls)
            {
                throw new InvalidOperationException(
                    $"OpenAI Responses stream completed with an unfinished tool call: "
                    + $"{toolCall.Name} ({toolCall.Id})");
            }
        }
    }

    /// <summary>pi reasoning item summary/content 数组的 text 合并（join 对 undefined 等价空串）。</summary>
    private static string JoinItemTextArray(JsonArray? array, string separator)
    {
        if (array is null) return "";
        return string.Join(
            separator,
            array.Select(node => node is JsonObject entry && JsonStr(entry, "text") is { } text ? text : ""));
    }

    /// <summary>pi message item content 合并：output_text 取 text，其余（refusal）取 refusal。</summary>
    private static string JoinOutputTextArray(JsonArray? array)
    {
        if (array is null) return "";
        return string.Concat(array.Select(node =>
        {
            if (node is not JsonObject entry) return "";
            return JsonStr(entry, "type") == "output_text" ? JsonStr(entry, "text") ?? "" : JsonStr(entry, "refusal") ?? "";
        }));
    }

    /// <summary>流式 custom（grammar）工具输入追加；返回其 JSON 参数文本的对应片段。</summary>
    private static string? AppendCustomInput(ResponsesOutputSlot input, string delta)
        => SendCustomInput(input, AppendGrammarToolInputJsonDelta(input.CustomBuffer!, input.CustomProperty!, delta));

    /// <summary>以 provider 的完整输入收口 custom 工具输入（provider 发过就校验接续）。</summary>
    private static string? CloseCustomInput(ResponsesOutputSlot input, string? finalInput)
        => SendCustomInput(input, CloseGrammarToolInputJson(input.CustomBuffer!, input.CustomProperty!, finalInput));

    private static string? SendCustomInput(ResponsesOutputSlot input, string? jsonDelta)
    {
        input.ToolCall!.Arguments = new JsonObject { [input.CustomProperty!] = input.CustomBuffer!.Input };
        if (input.Unsent.Length == 0) return jsonDelta;
        var sent = input.Unsent + (jsonDelta ?? "");
        input.Unsent = "";
        return sent;
    }

    /// <summary>pi mapStopReason 861–891：未知 status 照 pi 抛错（经外层 catch 转终态 Error）。</summary>
    internal static (StopReason StopReason, string? ErrorMessage) MapStopReason(
        string? status, string? incompleteReason)
    {
        if (string.IsNullOrEmpty(status)) return (StopReason.Stop, null);
        switch (status)
        {
            case "completed":
                return (StopReason.Stop, null);
            case "incomplete":
                if (incompleteReason == "max_output_tokens")
                {
                    return (StopReason.Length, null);
                }

                return (StopReason.Error, incompleteReason is { Length: > 0 }
                    ? $"Response incomplete: {incompleteReason}"
                    : "Response incomplete without a provider reason");
            case "failed":
            case "cancelled":
                return (StopReason.Error, null);
            // 这两个状态很怪：视作正常收尾。
            case "in_progress":
            case "queued":
                return (StopReason.Stop, null);
            default:
                throw new InvalidOperationException($"Unhandled stop reason: {status}");
        }
    }

    // ---------------------------------------------------------------------
    // simple options（pi simple-options.ts + estimate.ts 子集）
    // ---------------------------------------------------------------------

    private static OpenAiResponsesOptions BuildSimpleOptions(
        Model model, TranscriptContext context, SimpleStreamOptions? options)
    {
        var clampedReasoning = options?.Reasoning is { } reasoning
            ? ModelOperations.ClampThinkingLevel(model, ToModelThinkingLevel(reasoning))
            : (ModelThinkingLevel?)null;
        var reasoningEffort = clampedReasoning is { } level && level != ModelThinkingLevel.Off
            ? ToThinkingLevel(level)
            : (ThinkingLevel?)null;

        return new OpenAiResponsesOptions
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

    private static int ClampMaxTokensToContext(Model model, TranscriptContext context, int maxTokens)
    {
        if (model.ContextWindow <= 0) return Math.Max(MinMaxTokens, maxTokens);
        var available = model.ContextWindow - (int)EstimateContextTokens(context.Messages) - ContextSafetyTokens;
        return Math.Min(maxTokens, Math.Max(MinMaxTokens, available));
    }

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
    // transform-messages.ts（Responses 变体：normalizeToolCallId 感知来源 assistant 消息）
    // ---------------------------------------------------------------------

    private static IReadOnlyList<Message> TransformMessages(
        IReadOnlyList<Message> messages,
        Model model,
        Func<string, AssistantMessage, string> normalizeToolCallId)
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
                                    var normalizedCallId = normalizeToolCallId(toolCall.Id, assistant);
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
    // constrained-sampling.ts 子集（strict / grammar 工具）
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // pi utils/hash.ts shortHash
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // auth / env / 杂项（pi 49–89、provider-env.ts、openai-prompt-cache.ts）
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
    // 错误格式化（pi error-body.ts 的 C# 例外形状等价，与 Completions 同形）
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
        public string? ProviderThinkingLevel { get; init; }
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
                ProviderThinkingLevel = ProviderThinkingLevel,
                Usage = Usage,
                StopReason = StopReason,
                ErrorMessage = ErrorMessage,
                RawStopReason = RawStopReason,
                ResponseId = ResponseId,
                Timestamp = Timestamp,
            };
    }

    // ---------------------------------------------------------------------
    // 流内块模型（pi ResponsesOutputSlot 的块载荷 = ThinkingContent | TextContent | ToolCall）
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

        /// <summary>reasoning item id（reasoningBlocksById 回填用）。</summary>
        public string ItemId { get; set; } = "";

        public override ContentBlock ToContentBlock()
            => new ThinkingContent
            {
                Thinking = Thinking,
                ThinkingSignature = string.IsNullOrEmpty(ThinkingSignature) ? null : ThinkingSignature,
            };
    }

    private sealed class ToolCallStreamBlock : StreamBlock
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public JsonObject Arguments { get; set; } = new();
        public string? ArgumentsError { get; set; }
        public string? Namespace { get; set; }

        public override ContentBlock ToContentBlock()
            => new ToolCall
            {
                Id = Id,
                Name = Name,
                Arguments = Arguments,
                ArgumentsError = ArgumentsError,
                Namespace = Namespace,
            };

        public ToolCall ToToolCall()
            => new()
            {
                Id = Id,
                Name = Name,
                Arguments = Arguments,
                ArgumentsError = ArgumentsError,
                Namespace = Namespace,
            };
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

    /// <summary>pi：key 缺失时回落请求档位；key 存在但值为 null 时不发参数（返回 null）。</summary>
    private static string? MappedOrRequested(Model model, ThinkingLevel level)
    {
        if (model.ThinkingLevelMap is not { } map) return ToThinkingLevelWire(level);
        return map.TryGetValue(ToModelThinkingLevel(level), out var mapped) ? mapped : ToThinkingLevelWire(level);
    }

    private static string? OffMapping(Model model)
        => model.ThinkingLevelMap is { } map && map.TryGetValue(ModelThinkingLevel.Off, out var off)
            ? off
            : null;

    private static bool OffExplicitlyDisabled(Model model)
        => model.ThinkingLevelMap is { } map
            && map.TryGetValue(ModelThinkingLevel.Off, out var off)
            && off is null;
}
