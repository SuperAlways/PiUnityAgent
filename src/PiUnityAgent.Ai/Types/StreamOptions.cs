using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Types;

/// <summary>所有 provider 请求共有的选项；对应 pi ProviderRequestOptions + StreamOptions。</summary>
public record StreamOptions
{
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public int? MaxTokens { get; init; }
    public double? Temperature { get; init; }
    public IReadOnlyList<string>? StopSequences { get; init; }
    public string? SessionId { get; init; }
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    public JsonObject? SamplingParams { get; init; }
    public int? TimeoutMs { get; init; }
    public int? MaxRetries { get; init; }
    public int? MaxRetryDelayMs { get; init; }

    /// <summary>唯一取消入口；provider/宿主不得再另收 token。</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>pi `fetch` 的 C# 等价物；由宿主注入（B 方案）。null 时用宿主默认。</summary>
    public FetchFunction? Fetch { get; init; }

    public ClientIdentity? Identity { get; init; }
    public JsonObject? Metadata { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public CacheRetention? CacheRetention { get; init; }
    public TransportKind? Transport { get; init; }
    public int? WebsocketConnectTimeoutMs { get; init; }
    public TelemetrySpan? Telemetry { get; init; }

    public Func<JsonObject, ProviderModel, Task<JsonObject?>>? OnPayload { get; init; }
    public Func<ProviderResponse, ProviderModel, Task>? OnResponse { get; init; }
    public Func<JsonNode, ProviderModel, Task>? OnProviderStreamEvent { get; init; }
}
