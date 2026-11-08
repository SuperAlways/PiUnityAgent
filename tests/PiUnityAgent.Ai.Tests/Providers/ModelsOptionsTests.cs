using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class ModelsOptionsTests
{
    [Fact]
    public void Constructor_Accepts_All_Four_Options()
    {
        var models = new Models(new CreateModelsOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsStore = new InMemoryModelsStore(),
            AuthContext = new DefaultAuthContext(),
            Identity = new ClientIdentity { UserAgent = UserAgentMode.Custom("PiUnityAgent/test") },
        });

        Assert.NotNull(models.Identity!.UserAgent);
    }

    [Fact]
    public async Task Stream_Applies_TransformHeaders_And_Identity()
    {
        IReadOnlyDictionary<string, string?>? seenHeaders = null;
        ClientIdentity? seenIdentity = null;
        var models = new Models(new CreateModelsOptions
        {
            Identity = new ClientIdentity { UserAgent = UserAgentMode.Custom("default") },
        });
        models.SetProvider(ProviderTestSupport.CreateProvider(stream: (model, context, options) =>
        {
            seenHeaders = options?.Headers;
            seenIdentity = options?.Identity;
            return ProviderTestSupport.DoneStream(model, context, options);
        }));

        var options = new StreamOptions
        {
            Headers = new Dictionary<string, string?> { ["x-request"] = "1" },
            Identity = new ClientIdentity { Version = "request" },
            TransformHeaders = headers =>
            {
                var merged = new Dictionary<string, string?>(headers ?? new Dictionary<string, string?>());
                merged["x-transformed"] = "1";
                return Task.FromResult<IReadOnlyDictionary<string, string?>>(merged);
            },
        };
        var stream = models.Stream(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() },
            options);
        await stream.WaitForDoneAsync();

        Assert.Equal("1", seenHeaders!["x-transformed"]);
        Assert.Equal("1", seenHeaders["x-request"]);
        Assert.NotNull(seenIdentity!.UserAgent);   // 默认 UserAgent 保留
        Assert.Equal("request", seenIdentity.Version); // 请求级字段覆盖
    }
}
