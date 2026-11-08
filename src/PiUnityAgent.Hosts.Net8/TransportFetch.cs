using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;
using UnityLlmTransport;
using AiFetchOptions = PiUnityAgent.Ai.Types.FetchOptions;
using AiTransportKind = PiUnityAgent.Ai.Types.TransportKind;
using TransportRequest = UnityLlmTransport.TransportRequest;
using TransportOptions = UnityLlmTransport.TransportOptions;
using TransportHeaders = UnityLlmTransport.TransportHeaders;
using TransportException = UnityLlmTransport.TransportException;

namespace PiUnityAgent.Hosts.Net8;

/// <summary>B 方案适配器：把 PiUnityAgent 自有 Fetch 端口映射到 UnityLlmTransport。</summary>
public sealed class TransportFetch
{
    private readonly IStreamingHttpTransport _transport;

    public TransportFetch(IStreamingHttpTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public FetchFunction Fetch => FetchAsync;

    public async Task<IFetchResponse> FetchAsync(
        FetchRequest request,
        AiFetchOptions? options,
        CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        try
        {
            var session = await _transport.OpenAsync(
                ToTransportRequest(request),
                ToTransportOptions(options),
                cancellationToken).ConfigureAwait(false);
            return new TransportFetchResponse(session);
        }
        catch (TransportException exception)
        {
            throw ToFetchException(exception);
        }
    }

    private static TransportRequest ToTransportRequest(FetchRequest request)
    {
        var headers = new TransportHeaders();
        foreach (var entry in request.Headers)
        {
            headers[entry.Key] = entry.Value;
        }
        return new TransportRequest(request.Method, request.Url, headers, request.Body)
        {
            BodyFactory = request.BodyFactory,
        };
    }

    private static TransportOptions ToTransportOptions(AiFetchOptions? options) => new()
    {
        ConnectTimeout = options?.ConnectTimeout,
        FirstByteTimeout = options?.FirstByteTimeout,
        IdleTimeout = options?.IdleTimeout,
        MaxResponseBytes = options?.MaxResponseBytes,
        Kind = MapKind(options?.Kind),
        Env = options?.Env,
        SessionId = options?.SessionId,
    };

    private static UnityLlmTransport.TransportKind MapKind(AiTransportKind? kind) => kind switch
    {
        AiTransportKind.Auto => UnityLlmTransport.TransportKind.Auto,
        AiTransportKind.Sse => UnityLlmTransport.TransportKind.Sse,
        AiTransportKind.WebSocket => UnityLlmTransport.TransportKind.WebSocket,
        AiTransportKind.WebSocketCached => UnityLlmTransport.TransportKind.WebSocketCached,
        _ => UnityLlmTransport.TransportKind.Auto,
    };

    internal static FetchException ToFetchException(TransportException exception)
    {
        var kind = exception.Kind switch
        {
            TransportErrorKind.Connect => FetchErrorKind.Connect,
            TransportErrorKind.Tls => FetchErrorKind.Tls,
            TransportErrorKind.Proxy => FetchErrorKind.Proxy,
            TransportErrorKind.HttpStatus => FetchErrorKind.Protocol,
            TransportErrorKind.Handshake => FetchErrorKind.Protocol,
            TransportErrorKind.Protocol => FetchErrorKind.Protocol,
            TransportErrorKind.FirstByteTimeout => FetchErrorKind.FirstByteTimeout,
            TransportErrorKind.IdleTimeout => FetchErrorKind.IdleTimeout,
            TransportErrorKind.Cancelled => FetchErrorKind.Cancelled,
            TransportErrorKind.Aborted => FetchErrorKind.Aborted,
            TransportErrorKind.Overflow => FetchErrorKind.Overflow,
            TransportErrorKind.Unsupported => FetchErrorKind.Unsupported,
            _ => FetchErrorKind.Protocol,
        };
        return new FetchException(kind, exception.Message, exception)
        {
            StatusCode = exception.StatusCode,
            Headers = ToFetchHeaders(exception.Headers),
            ErrorBody = exception.ErrorBody,
            IsRetryable = exception.IsRetryable,
        };
    }

    private static FetchHeaders? ToFetchHeaders(TransportHeaders? headers)
    {
        if (headers is null) return null;
        var result = new FetchHeaders();
        foreach (var entry in headers) result[entry.Key] = entry.Value;
        return result;
    }

    private sealed class TransportFetchResponse : IFetchResponse
    {
        private readonly ITransportSession _session;

        public TransportFetchResponse(ITransportSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            Headers = ToFetchHeaders(session.Headers) ?? new FetchHeaders();
        }

        public int StatusCode => _session.StatusCode;
        public FetchHeaders Headers { get; }
        public Stream Body => _session.Body;
        public void Abort() => _session.Abort();
        public ValueTask DisposeAsync() => _session.DisposeAsync();
    }
}
