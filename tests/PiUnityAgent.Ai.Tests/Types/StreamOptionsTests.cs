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
            MaxTokens = 100,
            Temperature = 0.7,
            SessionId = "session",
            Headers = new Dictionary<string, string?> { ["x-test"] = "1" },
            SamplingParams = new JsonObject { ["top_p"] = 0.8 },
            TimeoutMs = 10000,
            MaxRetries = 2,
            MaxRetryDelayMs = 5000,
            CancellationToken = CancellationToken.None,
            Fetch = fetch,
            Identity = new ClientIdentity { UserAgent = UserAgentMode.Custom("PiUnityAgent/1.0") },
            Metadata = new JsonObject { ["user_id"] = "u1" },
            Env = new Dictionary<string, string> { ["REGION"] = "cn" },
            CacheRetention = CacheRetentionValue.Long,
            Transport = TransportKind.Sse,
            WebsocketConnectTimeoutMs = 3000,
            Telemetry = new TelemetrySpan { Name = "model.request" },
            TransformHeaders = headers => Task.FromResult<IReadOnlyDictionary<string, string?>>(headers),
            OnPayload = (payload, model) => Task.FromResult<JsonNode?>(payload),
            OnResponse = (response, model) => Task.CompletedTask,
            OnProviderStreamEvent = (node, model) => Task.CompletedTask,
        };

        Assert.Equal("key", options.ApiKey);
        Assert.Same(fetch, options.Fetch);
        Assert.Equal(CacheRetentionValue.Long, options.CacheRetention);
        Assert.Equal(TransportKind.Sse, options.Transport);
        Assert.Equal("model.request", options.Telemetry!.Name);
        Assert.Equal(UserAgentMode.Custom("PiUnityAgent/1.0"), options.Identity!.UserAgent);
        Assert.NotNull(options.TransformHeaders);
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
            Reasoning = ThinkingLevel.High,
            Deferred = new DeferredRequest.Enabled("1h"),
            ThinkingBudgets = new ThinkingBudgets { Minimal = 1, Low = 2, Medium = 3, High = 4 },
        };

        Assert.Equal(1000, options.MaxRetryDelayMs);
        Assert.NotNull(options.Fetch);
        Assert.Equal(ToolChoiceValue.Auto, options.ToolChoice);
        Assert.Equal(ThinkingLevel.High, options.Reasoning);
        Assert.IsType<DeferredRequest.Enabled>(options.Deferred);
        Assert.Equal(4, options.ThinkingBudgets!.High);
    }

    [Fact]
    public void StreamOptions_Removes_Pi_Absent_BaseUrl_And_StopSequences_And_Adds_Transform()
    {
        var options = new StreamOptions
        {
            TransformHeaders = headers => Task.FromResult<IReadOnlyDictionary<string, string?>>(
                new Dictionary<string, string?>(headers ?? new Dictionary<string, string?>())
                {
                    ["x-model"] = "m1",
                }),
            OnPayload = (payload, model) => Task.FromResult<JsonNode?>(payload),
        };

        Assert.NotNull(options.TransformHeaders);
        Assert.NotNull(options.OnPayload);
        Assert.Null(typeof(StreamOptions).GetProperty("BaseUrl"));
        Assert.Null(typeof(StreamOptions).GetProperty("StopSequences"));
    }

    [Fact]
    public void SimpleOptions_Uses_Reasoning()
    {
        var options = new SimpleStreamOptions { Reasoning = ThinkingLevel.High };
        Assert.Equal(ThinkingLevel.High, options.Reasoning);
        Assert.Null(typeof(SimpleStreamOptions).GetProperty("ThinkingLevel"));
    }

    [Fact]
    public void ClientIdentity_Merges_Field_By_Field()
    {
        var defaults = new ClientIdentity
        {
            UserAgent = UserAgentMode.Custom("default-ua"),
            Attribution = new ClientAttribution("app", "https://example.test"),
        };
        var request = new ClientIdentity { Version = "request-v" };
        var merged = ClientIdentityMerger.Merge(defaults, request);

        Assert.NotNull(merged);
        Assert.Same(defaults.UserAgent, merged!.UserAgent);
        Assert.Equal("request-v", merged.Version);
        Assert.Equal("app", merged.Attribution!.Name);
    }

    [Fact]
    public void ClientIdentity_Can_Explicitly_Disable_Attribution()
    {
        var defaults = new ClientIdentity
        {
            Attribution = new ClientAttribution("app", "https://example.test"),
        };
        var merged = ClientIdentityMerger.Merge(defaults, new ClientIdentity { Attribution = null });

        Assert.NotNull(merged);
        Assert.True(merged!.AttributionSet);
        Assert.Null(merged.Attribution);
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
            new Dictionary<string, string> { ["x-request-id"] = "r1" });
        var identity = new ClientIdentity
        {
            UserAgent = UserAgentMode.Short,
            Attribution = new ClientAttribution("taiwu", "https://example.test"),
            Version = "1.0.0",
        };

        Assert.Equal(200, response.Status);
        Assert.Equal("r1", response.Headers["x-request-id"]);
        Assert.Equal(UserAgentMode.Short, identity.UserAgent);
        Assert.Equal("taiwu", identity.Attribution!.Name);
        Assert.Equal("1.0.0", identity.Version);
        Assert.True(identity.AttributionSet);
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
