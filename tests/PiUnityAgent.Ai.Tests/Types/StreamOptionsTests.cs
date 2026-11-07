using System.Text.Json;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Tests.Fakes;
using PiUnityAgent.Ai.Types;
using CacheRetentionValue = PiUnityAgent.Ai.Types.CacheRetention;
using ToolChoiceValue = PiUnityAgent.Ai.Types.ToolChoice;

namespace PiUnityAgent.Ai.Tests.Types;

public class StreamOptionsTests
{
    [Fact]
    public void StreamOptions_Carries_The_Complete_Request_Shape()
    {
        var mock = new MockFetch();
        // mock.Fetch 是 get-only 属性（每次访问新建委托实例）；先取一次再断言同一实例。
        var fetch = mock.Fetch;
        var options = new StreamOptions
        {
            ApiKey = "key",
            BaseUrl = "https://example.test",
            MaxTokens = 100,
            Temperature = 0.7,
            StopSequences = new[] { "END" },
            SessionId = "session",
            Headers = new Dictionary<string, string?> { ["x-test"] = "1" },
            SamplingParams = new JsonObject { ["top_p"] = 0.8 },
            TimeoutMs = 10000,
            MaxRetries = 2,
            MaxRetryDelayMs = 5000,
            CancellationToken = CancellationToken.None,
            Fetch = fetch,
            Identity = new ClientIdentity { UserAgent = "PiUnityAgent/1.0" },
            Metadata = new JsonObject { ["user_id"] = "u1" },
            Env = new Dictionary<string, string> { ["REGION"] = "cn" },
            CacheRetention = CacheRetentionValue.Long,
            Transport = TransportKind.Sse,
            WebsocketConnectTimeoutMs = 3000,
            Telemetry = new TelemetrySpan { Name = "model.request" },
            OnPayload = (payload, model) => Task.FromResult<JsonObject?>(payload),
            OnResponse = (response, model) => Task.CompletedTask,
            OnProviderStreamEvent = (node, model) => Task.CompletedTask,
        };

        Assert.Equal("key", options.ApiKey);
        Assert.Same(fetch, options.Fetch);
        Assert.Equal(CacheRetentionValue.Long, options.CacheRetention);
        Assert.Equal(TransportKind.Sse, options.Transport);
        Assert.Equal("model.request", options.Telemetry!.Name);
        Assert.NotNull(options.OnPayload);
        Assert.NotNull(options.OnResponse);
        Assert.NotNull(options.OnProviderStreamEvent);
        Assert.NotNull(options.Headers);
        Assert.Equal("1", options.Headers!["x-test"]);
    }

    [Fact]
    public void SimpleStreamOptions_Inherits_Request_Options_And_Adds_Simple_Fields()
    {
        var options = new SimpleStreamOptions
        {
            MaxRetryDelayMs = 1000,
            Fetch = new MockFetch().Fetch,
            ToolChoice = ToolChoiceValue.Auto,
            ThinkingLevel = ModelThinkingLevel.High,
            Deferred = new DeferredRequest.Enabled("1h"),
            ThinkingBudgets = new ThinkingBudgets { Minimal = 1, Low = 2, Medium = 3, High = 4 },
        };

        Assert.Equal(1000, options.MaxRetryDelayMs);
        Assert.NotNull(options.Fetch);
        Assert.Equal(ToolChoiceValue.Auto, options.ToolChoice);
        Assert.Equal(ModelThinkingLevel.High, options.ThinkingLevel);
        Assert.IsType<DeferredRequest.Enabled>(options.Deferred);
        Assert.Equal(4, options.ThinkingBudgets!.High);
    }

    [Fact]
    public void StreamOptions_Does_Not_Expose_HttpClient()
    {
        Assert.Null(typeof(StreamOptions).GetProperty("HttpClient"));
    }

    [Fact]
    public void ProviderResponse_And_ClientIdentity_Are_Present()
    {
        var response = new ProviderResponse(
            200,
            new Dictionary<string, string?> { ["x-request-id"] = "r1" });
        var identity = new ClientIdentity
        {
            UserAgent = "PiUnityAgent/1.0",
            Headers = new Dictionary<string, string> { ["x-app"] = "taiwu" },
        };

        Assert.Equal(200, response.Status);
        Assert.Equal("r1", response.Headers["x-request-id"]);
        Assert.Equal("taiwu", identity.Headers!["x-app"]);
    }

    [Theory]
    [InlineData(CacheRetentionValue.None, "\"none\"")]
    [InlineData(CacheRetentionValue.Short, "\"short\"")]
    [InlineData(CacheRetentionValue.Long, "\"long\"")]
    public void CacheRetention_Uses_Pi_Wire_Names(CacheRetentionValue value, string wire)
    {
        Assert.Equal(wire, JsonSerializer.Serialize(value));
        Assert.Equal(value, JsonSerializer.Deserialize<CacheRetentionValue>(wire));
    }

    [Theory]
    [InlineData(ToolChoiceValue.Auto, "\"auto\"")]
    [InlineData(ToolChoiceValue.None, "\"none\"")]
    public void ToolChoice_Uses_Pi_Wire_Names(ToolChoiceValue value, string wire)
    {
        Assert.Equal(wire, JsonSerializer.Serialize(value));
        Assert.Equal(value, JsonSerializer.Deserialize<ToolChoiceValue>(wire));
    }

    [Fact]
    public void DeferredRequest_And_DeferredOptions_Are_Present()
    {
        var disabled = new DeferredRequest.Disabled();
        var enabled = new DeferredRequest.Enabled("24h");
        var fetch = new DeferredFetchOptions { Wait = 1000 };
        var cancel = new DeferredCancelOptions();

        Assert.IsType<DeferredRequest.Disabled>(disabled);
        Assert.IsType<DeferredRequest.Enabled>(enabled);
        Assert.Equal("24h", enabled.Window);
        Assert.Equal(1000, fetch.Wait);
        Assert.NotNull(cancel);
    }
}
