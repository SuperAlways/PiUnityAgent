using System.Text;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Fakes;

public sealed class MockFetch
{
    private readonly object _gate = new();
    private readonly Queue<Func<FetchRequest, FetchOptions?, CancellationToken, Task<IFetchResponse>>> _queue = new();
    private int _callCount;

    public List<FetchRequest> Requests { get; } = new();
    public int CallCount => Volatile.Read(ref _callCount);
    public FetchFunction Fetch => FetchAsync;

    public void Enqueue(Func<FetchRequest, FetchOptions?, CancellationToken, Task<IFetchResponse>> handler)
    {
        lock (_gate) _queue.Enqueue(handler);
    }

    public void EnqueueText(string text, int status = 200, IReadOnlyDictionary<string, string?>? headers = null)
    {
        var responseHeaders = new FetchHeaders();
        if (headers is not null)
        {
            foreach (var entry in headers) responseHeaders[entry.Key] = entry.Value;
        }

        EnqueueBytes(status, Encoding.UTF8.GetBytes(text), responseHeaders);
    }

    public void EnqueueBytes(int status, byte[] body, FetchHeaders? headers = null)
        => Enqueue((_, _, _) => Task.FromResult<IFetchResponse>(new MockFetchResponse(status, body, headers)));

    public void EnqueueException(FetchException exception)
        => Enqueue((_, _, _) => Task.FromException<IFetchResponse>(exception));

    private async Task<IFetchResponse> FetchAsync(
        FetchRequest request,
        FetchOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Func<FetchRequest, FetchOptions?, CancellationToken, Task<IFetchResponse>> handler;
        lock (_gate)
        {
            if (_queue.Count == 0)
                throw new InvalidOperationException("No mock responses queued.");
            handler = _queue.Dequeue();
            Requests.Add(request);
            Interlocked.Increment(ref _callCount);
        }

        return await handler(request, options, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MockFetchResponse : IFetchResponse
{
    private readonly MemoryStream _body;
    private int _disposed;
    private int _abortCount;

    public MockFetchResponse(int statusCode, byte[] body, FetchHeaders? headers = null)
    {
        StatusCode = statusCode;
        Headers = headers ?? new FetchHeaders();
        _body = new MemoryStream(body, writable: false);
    }

    public int StatusCode { get; }
    public FetchHeaders Headers { get; }
    public Stream Body => _body;
    public int AbortCount => Volatile.Read(ref _abortCount);

    public void Abort()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Increment(ref _abortCount);
            _body.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Abort();
        return default;
    }
}
