using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Hosts.Net8;
using UnityLlmTransport;
using Xunit;

namespace PiUnityAgent.Hosts.Net8.Tests;

public class TransportFetchTests
{
    [Fact]
    public async Task Fetch_Maps_Request_And_Response()
    {
        var mock = new MockTransport(request => new MockResponse
        {
            StatusCode = 200,
            Headers = new TransportHeaders { ["content-type"] = "text/event-stream" },
            Chunks = new[] { new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("data: {}\n\n")) },
        });
        var fetch = new TransportFetch(mock);
        var request = new FetchRequest(
            "POST",
            "https://example.test/v1/chat/completions",
            new FetchHeaders { ["x-test"] = "1" },
            Encoding.UTF8.GetBytes("{\"model\":\"m\"}"));

        await using var response = await fetch.FetchAsync(
            request,
            new FetchOptions { FirstByteTimeout = System.TimeSpan.FromSeconds(5) },
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("text/event-stream", response.Headers["content-type"]);
        Assert.Equal("{\"model\":\"m\"}", Encoding.UTF8.GetString(mock.Requests[0].Body.ToArray()));
        Assert.Equal("1", mock.Requests[0].Headers["x-test"]);
    }

    [Fact]
    public async Task Fetch_Maps_TransportException_To_FetchException()
    {
        var mock = new MockTransport(_ => new MockResponse
        {
            Error = new TransportException(
                TransportErrorKind.FirstByteTimeout,
                "first byte timeout",
                isRetryable: true),
        });
        var fetch = new TransportFetch(mock);

        var exception = await Assert.ThrowsAsync<FetchException>(() => fetch.FetchAsync(
            new FetchRequest("POST", "https://example.test", new FetchHeaders(), ReadOnlyMemory<byte>.Empty),
            null,
            CancellationToken.None));

        Assert.Equal(FetchErrorKind.FirstByteTimeout, exception.Kind);
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task Fetch_Forwards_Abort()
    {
        var tracking = new TrackingTransport();
        var fetch = new TransportFetch(tracking);
        await using var response = await fetch.FetchAsync(
            new FetchRequest("POST", "https://example.test", new FetchHeaders(), ReadOnlyMemory<byte>.Empty),
            null,
            CancellationToken.None);

        response.Abort();
        Assert.Equal(1, tracking.Session.AbortCount);
    }

    private sealed class TrackingTransport : IStreamingHttpTransport
    {
        public TrackingSession Session { get; } = new();
        public TransportCapabilities Capabilities => TransportCapabilities.StreamingBody;

        public Task<ITransportSession> OpenAsync(
            UnityLlmTransport.TransportRequest request,
            UnityLlmTransport.TransportOptions options,
            CancellationToken cancellationToken)
            => Task.FromResult<ITransportSession>(Session);

        public sealed class TrackingSession : ITransportSession
        {
            public int AbortCount { get; private set; }
            public int StatusCode => 200;
            public TransportHeaders Headers { get; } = new();
            public Stream Body { get; } = new MemoryStream();
            public void Abort() => AbortCount++;
            public ValueTask DisposeAsync() => default;
        }
    }
}
