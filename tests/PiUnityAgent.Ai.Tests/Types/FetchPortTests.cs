using System.Text;
using System.Text.Json;
using PiUnityAgent.Ai.Tests.Fakes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class FetchPortTests
{
    [Fact]
    public void FetchHeaders_Are_CaseInsensitive_And_Null_Suppresses()
    {
        var headers = new FetchHeaders { ["Authorization"] = null };
        Assert.True(headers.TryGetValue("authorization", out var value));
        Assert.Null(value);

        headers["Content-Type"] = "application/json";
        Assert.Equal("application/json", headers["content-type"]);
    }

    [Fact]
    public void FetchRequest_Carries_Body_And_BodyFactory()
    {
        Func<CancellationToken, Task<Stream>> factory = _ =>
            Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }));

        var request = new FetchRequest(
            "POST",
            "https://example.test/v1/chat",
            new FetchHeaders(),
            default)
        {
            BodyFactory = factory,
        };

        Assert.Equal("POST", request.Method);
        Assert.Equal("https://example.test/v1/chat", request.Url);
        Assert.Same(factory, request.BodyFactory);
    }

    [Fact]
    public void FetchOptions_Carry_Transport_Timeouts_And_Env()
    {
        var options = new FetchOptions
        {
            Kind = TransportKind.Sse,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            FirstByteTimeout = TimeSpan.FromSeconds(10),
            IdleTimeout = TimeSpan.FromSeconds(30),
            MaxResponseBytes = 1024,
            Env = new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://proxy" },
            SessionId = "session-1",
        };

        Assert.Equal(TransportKind.Sse, options.Kind);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.FirstByteTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.IdleTimeout);
        Assert.Equal(1024, options.MaxResponseBytes);
        Assert.Equal("http://proxy", options.Env!["HTTPS_PROXY"]);
        Assert.Equal("session-1", options.SessionId);
    }

    [Fact]
    public async Task MockFetch_Exposes_Status_Headers_And_Streaming_Body()
    {
        var mock = new MockFetch();
        mock.EnqueueText("hello", 200, new Dictionary<string, string?>
        {
            ["content-type"] = "text/plain",
        });

        await using var response = await mock.Fetch(
            new FetchRequest("GET", "https://example.test", new FetchHeaders(), default),
            null,
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("text/plain", response.Headers["Content-Type"]);

        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        Assert.Equal("hello", await reader.ReadToEndAsync());
        Assert.Equal(1, mock.CallCount);
        Assert.Single(mock.Requests);
    }

    [Fact]
    public async Task MockFetch_Can_Return_Http_Error_Body()
    {
        var mock = new MockFetch();
        mock.EnqueueText("busy", 503);

        await using var response = await mock.Fetch(
            new FetchRequest("GET", "https://example.test", new FetchHeaders(), default),
            null,
            CancellationToken.None);

        Assert.Equal(503, response.StatusCode);
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        Assert.Equal("busy", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task MockFetch_Propagates_Cancellation()
    {
        var mock = new MockFetch();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mock.Fetch(
                new FetchRequest("GET", "https://example.test", new FetchHeaders(), default),
                null,
                cts.Token));
    }

    [Fact]
    public async Task MockFetch_Abort_Is_Idempotent()
    {
        var mock = new MockFetch();
        mock.EnqueueText("hello");

        var response = Assert.IsType<MockFetchResponse>(await mock.Fetch(
            new FetchRequest("GET", "https://example.test", new FetchHeaders(), default),
            null,
            CancellationToken.None));

        response.Abort();
        response.Abort();
        Assert.Equal(1, response.AbortCount);

        await response.DisposeAsync();
        Assert.Equal(1, response.AbortCount);
    }

    [Fact]
    public void FetchException_Carries_Classification_And_Response_Metadata()
    {
        var exception = new FetchException(FetchErrorKind.Proxy, "proxy failed")
        {
            StatusCode = 502,
            Headers = new FetchHeaders { ["X-Reason"] = "tunnel" },
            ErrorBody = "bad gateway",
            IsRetryable = true,
        };

        Assert.Equal(FetchErrorKind.Proxy, exception.Kind);
        Assert.Equal(502, exception.StatusCode);
        Assert.Equal("tunnel", exception.Headers!["x-reason"]);
        Assert.Equal("bad gateway", exception.ErrorBody);
        Assert.True(exception.IsRetryable);
        Assert.Equal("proxy failed", exception.Message);
    }

    [Theory]
    [InlineData(TransportKind.Auto, "\"auto\"")]
    [InlineData(TransportKind.Sse, "\"sse\"")]
    [InlineData(TransportKind.WebSocket, "\"websocket\"")]
    [InlineData(TransportKind.WebSocketCached, "\"websocket-cached\"")]
    public void TransportKind_Uses_Pi_Wire_Names(TransportKind value, string wire)
    {
        Assert.Equal(wire, JsonSerializer.Serialize(value));
        Assert.Equal(value, JsonSerializer.Deserialize<TransportKind>(wire));
    }
}
