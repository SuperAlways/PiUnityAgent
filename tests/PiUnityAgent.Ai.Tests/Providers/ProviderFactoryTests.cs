using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class ProviderFactoryTests
{
    private static ProviderAuth Keyless() => new()
    {
        ApiKey = new ApiKeyAuth
        {
            Name = "keyless",
            Resolve = (_, _) => Task.FromResult<AuthResult?>(new AuthResult(new ModelAuth(), null, "keyless")),
        },
    };

    private static ProviderStreams Streams() => new()
    {
        Stream = (model, context, options) => ProviderTestSupport.DoneStream(model, context, options),
        StreamSimple = (model, context, options) => ProviderTestSupport.DoneStream(model, context, options),
    };

    [Fact]
    public void Factory_Creates_Single_Api_Provider()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p1",
            Auth = Keyless(),
            Models = new[] { ProviderTestSupport.CreateModel("m1", "p1") },
            Api = Streams(),
        });

        Assert.Equal("p1", provider.Id);
        Assert.Single(provider.GetModels());
    }

    [Fact]
    public async Task Factory_Dispatches_Api_Map_And_Errors_On_Missing()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p1",
            Auth = Keyless(),
            Models = new[] { ProviderTestSupport.CreateModel("m1", "p1") },
            ApiByApi = new Dictionary<string, ProviderStreams> { ["faux"] = Streams() },
        });

        var stream = provider.Stream(
            ProviderTestSupport.CreateModel("m1", "p1"),
            new TranscriptContext { Messages = Array.Empty<Message>() },
            null);
        Assert.IsType<AssistantMessageEvent.Done>((await ProviderTestSupport.CollectAsync(stream))[^1]);

        var missing = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p2",
            Auth = Keyless(),
            Models = new[] { ProviderTestSupport.CreateModel("m1", "p2") with { Api = "missing" } },
            ApiByApi = new Dictionary<string, ProviderStreams>(),
        });
        var missingStream = missing.Stream(
            ProviderTestSupport.CreateModel("m1", "p2") with { Api = "missing" },
            new TranscriptContext { Messages = Array.Empty<Message>() },
            null);
        Assert.Equal(StopReason.Error, (await missingStream.WaitForDoneAsync()).StopReason);
    }

    [Fact]
    public void Factory_Exposes_Images_And_Classifier_Capabilities()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p1",
            Auth = Keyless(),
            Images = new Dictionary<string, ProviderImages>
            {
                ["img"] = new()
                {
                    GenerateImages = (model, context, options) =>
                        Task.FromResult(new AssistantImages { Api = model.Api, Provider = model.Provider, Model = model.Id }),
                },
            },
            Classifiers = new Dictionary<string, ProviderClassifier>
            {
                ["cls"] = new()
                {
                    Classify = (model, context, options) =>
                        Task.FromResult(new ClassifierResult { Api = model.Api, Provider = model.Provider, Model = model.Id }),
                },
            },
        });

        Assert.IsAssignableFrom<IImagesProvider>(provider);
        Assert.IsAssignableFrom<IClassifierProvider>(provider);
    }

    [Fact]
    public void Factory_Rejects_No_Implementation_And_Both_Api_Forms()
    {
        Assert.Throws<ModelsError>(() => ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p1",
            Auth = Keyless(),
            Models = Array.Empty<ProviderModel>(),
        }));
        Assert.Throws<ModelsError>(() => ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p1",
            Auth = Keyless(),
            Api = Streams(),
            ApiByApi = new Dictionary<string, ProviderStreams> { ["faux"] = Streams() },
        }));
        Assert.Throws<ModelsError>(() => ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p1",
            Auth = new ProviderAuth(),
            Api = Streams(),
        }));
    }
}
