using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Types;

/// <summary>pi `fetch` 的 C# 等价物；由宿主注入（B 方案）。</summary>
public delegate Task<IFetchResponse> FetchFunction(
    FetchRequest request,
    FetchOptions? options,
    CancellationToken cancellationToken);

public sealed record FetchRequest(
    string Method,
    string Url,
    FetchHeaders Headers,
    ReadOnlyMemory<byte> Body)
{
    /// <summary>大请求（图片/多模态）用工厂重建流；设置后忽略 Body。必须可重放。</summary>
    public Func<CancellationToken, Task<Stream>>? BodyFactory { get; init; }
}

/// <summary>Headers：名称大小写不敏感；请求中 value=null 表示“抑制默认头”。</summary>
public sealed class FetchHeaders : Dictionary<string, string?>
{
    public FetchHeaders() : base(StringComparer.OrdinalIgnoreCase) { }

    public FetchHeaders(IDictionary<string, string?> values)
        : base(values, StringComparer.OrdinalIgnoreCase) { }
}

public sealed record FetchOptions
{
    public TransportKind? Kind { get; init; }
    public TimeSpan? ConnectTimeout { get; init; }
    public TimeSpan? FirstByteTimeout { get; init; }
    public TimeSpan? IdleTimeout { get; init; }
    public int? MaxResponseBytes { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? SessionId { get; init; }
}

public interface IFetchResponse : IAsyncDisposable
{
    int StatusCode { get; }
    FetchHeaders Headers { get; }

    /// <summary>响应体；任意状态码都可读（错误体也是 Body）。</summary>
    Stream Body { get; }

    /// <summary>立即中止底层连接；幂等。</summary>
    void Abort();
}

public enum FetchErrorKind
{
    Connect,
    Tls,
    Proxy,
    Protocol,
    FirstByteTimeout,
    IdleTimeout,
    Cancelled,
    Aborted,
    Overflow,
    Unsupported
}

public sealed class FetchException : Exception
{
    public FetchException(FetchErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public FetchErrorKind Kind { get; }
    public int? StatusCode { get; init; }
    public FetchHeaders? Headers { get; init; }
    public string? ErrorBody { get; init; }
    public bool IsRetryable { get; init; }
}

[JsonConverter(typeof(TransportKindJsonConverter))]
public enum TransportKind
{
    Auto,
    Sse,
    WebSocket,
    WebSocketCached
}

public sealed class TransportKindJsonConverter : JsonConverter<TransportKind>
{
    public override TransportKind Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "auto" => TransportKind.Auto,
            "sse" => TransportKind.Sse,
            "websocket" => TransportKind.WebSocket,
            "websocket-cached" => TransportKind.WebSocketCached,
            var value => throw new JsonException($"Unknown transport kind: {value}")
        };

    public override void Write(
        Utf8JsonWriter writer,
        TransportKind value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            TransportKind.Auto => "auto",
            TransportKind.Sse => "sse",
            TransportKind.WebSocket => "websocket",
            TransportKind.WebSocketCached => "websocket-cached",
            _ => throw new JsonException($"Unknown transport kind: {value}")
        });
}
