using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class StaticProviderTests
{
    [Fact]
    public void GetModels_And_GetAllModels_Return_The_Configured_Catalog()
    {
        var models = new[]
        {
            ProviderTestSupport.CreateModel("m1"),
            ProviderTestSupport.CreateModel("m2"),
        };
        var provider = ProviderTestSupport.CreateProvider(models: models);

        Assert.Equal(2, provider.GetModels().Count);
        Assert.Equal(2, provider.GetAllModels().Count);
        Assert.Equal("m2", provider.GetAllModels()[1].Id);
    }

    [Fact]
    public void Stream_Returns_The_StreamFn_Result_And_Is_Synchronous()
    {
        var model = ProviderTestSupport.CreateModel();
        var context = new TranscriptContext { Messages = Array.Empty<Message>() };
        var expected = ProviderTestSupport.DoneStream(model, context, null);
        var calls = 0;
        var provider = ProviderTestSupport.CreateProvider(stream: (seenModel, seenContext, seenOptions) =>
        {
            calls++;
            Assert.Same(model, seenModel);
            Assert.Same(context, seenContext);
            return expected;
        });

        var actual = provider.Stream(model, context, null);

        Assert.Same(expected, actual);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void StreamSimple_Passes_SimpleOptions_As_StreamOptions()
    {
        var model = ProviderTestSupport.CreateModel();
        var context = new TranscriptContext { Messages = Array.Empty<Message>() };
        StreamOptions? seenOptions = null;
        var provider = ProviderTestSupport.CreateProvider(stream: (_, _, options) =>
        {
            seenOptions = options;
            return ProviderTestSupport.DoneStream(model, context, options);
        });
        var simple = new SimpleStreamOptions { ThinkingLevel = ModelThinkingLevel.High };

        provider.StreamSimple(model, context, simple);

        Assert.IsType<SimpleStreamOptions>(seenOptions);
        Assert.Equal(ModelThinkingLevel.High, ((SimpleStreamOptions)seenOptions!).ThinkingLevel);
    }

    [Fact]
    public void Exposes_Metadata_And_Auth()
    {
        var headers = new Dictionary<string, string?> { ["x-provider"] = "1" };
        var auth = ProviderTestSupport.KeylessAuth();
        var provider = ProviderTestSupport.CreateProvider(
            id: "p1",
            baseUrl: "https://example.test",
            headers: headers,
            auth: auth);

        Assert.Equal("p1", provider.Id);
        Assert.Equal("p1", provider.Name);
        Assert.Equal("https://example.test", provider.BaseUrl);
        Assert.Equal("1", provider.Headers!["x-provider"]);
        Assert.Same(auth, provider.Auth);
    }
}
