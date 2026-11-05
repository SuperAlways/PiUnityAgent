using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class ModelsTests
{
    private static Model CreateModel(string id = "m1", string provider = "p1")
        => new()
        {
            Id = id,
            Name = id,
            Api = "test",
            Provider = provider,
            BaseUrl = "http://localhost:0",
        };

    private static Provider CreateProvider(string id = "p1", StreamFn? stream = null)
        => new()
        {
            Id = id,
            Name = id,
            Models = new[] { CreateModel("m1", id) },
            Stream = stream ?? ((model, context, options, ct) =>
            {
                var stream = new AssistantMessageEventStream();
                var message = new AssistantMessage
                {
                    Content = Array.Empty<ContentBlock>(),
                    Api = model.Api,
                    Provider = model.Provider,
                    Model = model.Id,
                    Usage = new Usage(),
                    StopReason = StopReason.Stop,
                    Timestamp = 1,
                };
                stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
                stream.End(message);
                return Task.FromResult<IAssistantMessageEventStream>(stream);
            }),
        };

    [Fact]
    public void Set_Get_Replace_Delete_Provider()
    {
        var models = new Models();
        var first = CreateProvider();
        models.SetProvider(first);

        Assert.Same(first, models.GetProvider("p1"));
        Assert.Single(models.GetModels());
        Assert.NotNull(models.GetModel("p1", "m1"));

        var replacement = CreateProvider();
        models.SetProvider(replacement);
        Assert.Same(replacement, models.GetProvider("p1"));
        Assert.True(models.DeleteProvider("p1"));
        Assert.Null(models.GetProvider("p1"));
    }

    [Fact]
    public void GetModels_Filters_By_Provider()
    {
        var models = new Models();
        models.SetProvider(CreateProvider("p1"));
        models.SetProvider(CreateProvider("p2"));

        Assert.Single(models.GetModels("p1"));
        Assert.Equal(2, models.GetModels().Count);
    }

    [Fact]
    public async Task StreamSimple_Dispatches_To_Provider()
    {
        var models = new Models();
        var called = false;
        models.SetProvider(CreateProvider(stream: (model, context, options, ct) =>
        {
            called = true;
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage
            {
                Content = Array.Empty<ContentBlock>(),
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                Usage = new Usage(),
                StopReason = StopReason.Stop,
                Timestamp = 1,
            };
            stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
            stream.End(message);
            return Task.FromResult<IAssistantMessageEventStream>(stream);
        }));

        var result = await models.StreamSimpleAsync(
            CreateModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var done = await result.WaitForDoneAsync();

        Assert.True(called);
        Assert.Equal(StopReason.Stop, done.StopReason);
    }

    [Fact]
    public async Task StreamSimple_Unknown_Provider_Returns_Error_Event()
    {
        var models = new Models();
        var stream = await models.StreamSimpleAsync(
            CreateModel(provider: "missing"),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Contains("Unknown provider", done.ErrorMessage);
    }

    [Fact]
    public async Task StreamSimple_Provider_Throws_Returns_Error_Event()
    {
        var models = new Models();
        models.SetProvider(CreateProvider(stream: (model, context, options, ct) =>
            throw new InvalidOperationException("boom")));

        var stream = await models.StreamSimpleAsync(
            CreateModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Contains("boom", done.ErrorMessage);
    }

    [Fact]
    public async Task StreamSimple_Provider_Throws_OperationCanceled_Returns_Aborted()
    {
        var models = new Models();
        models.SetProvider(CreateProvider(stream: (model, context, options, ct) =>
            throw new OperationCanceledException()));

        var stream = await models.StreamSimpleAsync(
            CreateModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var done = await stream.WaitForDoneAsync();

        // 取消 ≠ 失败：OCE 不得落入通用 catch，终态必须是 Aborted。
        Assert.Equal(StopReason.Aborted, done.StopReason);
        Assert.Equal("Request was aborted", done.ErrorMessage);
    }
}
