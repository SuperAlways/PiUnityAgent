using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class ModelsTests
{
    [Fact]
    public void Set_Get_Replace_Delete_Provider()
    {
        var models = new Models();
        var first = ProviderTestSupport.CreateProvider();
        models.SetProvider(first);

        Assert.Same(first, models.GetProvider("p1"));
        Assert.Single(models.GetModels());
        Assert.NotNull(models.GetModel("p1", "m1"));

        var replacement = ProviderTestSupport.CreateProvider();
        models.SetProvider(replacement);
        Assert.Same(replacement, models.GetProvider("p1"));
        Assert.True(models.DeleteProvider("p1"));
        Assert.Null(models.GetProvider("p1"));
    }

    [Fact]
    public void GetModels_Filters_By_Provider()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider("p1"));
        models.SetProvider(ProviderTestSupport.CreateProvider("p2"));

        Assert.Single(models.GetModels("p1"));
        Assert.Equal(2, models.GetModels().Count);
    }

    [Fact]
    public void GetModelsOfType_And_GetModelOfType_Filter_Chat_Models()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider("p1"));

        Assert.Single(models.GetModelsOfType(ModelType.Chat));
        Assert.NotNull(models.GetModelOfType(ModelType.Chat, "p1", "m1"));
        Assert.Empty(models.GetModelsOfType(ModelType.Image));
    }

    [Fact]
    public void GetAllModels_Returns_All_Provider_Models()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider("p1"));

        Assert.Single(models.GetAllModels());
        Assert.Equal("m1", models.GetAllModels("p1")[0].Id);
    }

    [Fact]
    public void ClearProviders_Empties_Registry()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider());
        models.ClearProviders();

        Assert.Empty(models.GetProviders());
        Assert.Empty(models.GetModels());
    }

    [Fact]
    public async Task Stream_Dispatches_And_Normalizes_Context()
    {
        TranscriptContext? seenContext = null;
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(stream: (model, context, options) =>
        {
            seenContext = context;
            return ProviderTestSupport.DoneStream(model, context, options);
        }));
        var model = ProviderTestSupport.CreateModel();
        var tool = new Tool
        {
            Name = "query_hp",
            Description = "Query HP",
            Parameters = new System.Text.Json.Nodes.JsonObject { ["type"] = "object" },
        };
        var context = new Context
        {
            SystemPrompt = "sys",
            Messages = new Message[] { new UserMessage { Timestamp = 1, Content = MessageContent.From("hi") } },
            Tools = new[] { tool },
        };

        var stream = models.Stream(model, context);
        var events = await ProviderTestSupport.CollectAsync(stream);

        var system = Assert.IsType<SystemMessage>(seenContext!.Messages[0]);
        Assert.Equal("sys", Assert.IsType<MessageContent.Text>(system.Content).Value);
        Assert.Single(system.ToolsAdded!);
        Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task Stream_Returns_Before_Setup_Completes()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(stream: (model, context, options) =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return ProviderTestSupport.DoneStream(model, context, options);
        }));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var stream = models.Stream(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() });
        stopwatch.Stop();

        Assert.NotNull(stream);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            "Models.Stream must not block on setup");
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)),
            "LazyStream must start setup eagerly");

        release.Set();
        var done = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Stop, done.StopReason);
    }

    [Fact]
    public async Task Unknown_Provider_Returns_Error_Stream()
    {
        var models = new Models();
        var stream = models.Stream(
            ProviderTestSupport.CreateModel(provider: "missing"),
            new Context { Messages = Array.Empty<Message>() });
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Contains("Unknown provider", done.ErrorMessage);
    }

    [Fact]
    public async Task Provider_Throws_Returns_Error_Stream()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(stream: (_, _, _) =>
            throw new InvalidOperationException("boom")));

        var stream = models.Stream(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() });
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Contains("boom", done.ErrorMessage);
    }

    [Fact]
    public async Task Provider_Throws_OperationCanceled_Returns_Error_Like_Pi()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider(stream: (_, _, _) =>
            throw new OperationCanceledException()));

        var stream = models.Stream(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() });
        var done = await stream.WaitForDoneAsync();

        // LazyStream setup 异常统一 Error；Aborted 由 provider 内层流负责。
        Assert.Equal(StopReason.Error, done.StopReason);
    }

    [Fact]
    public async Task StreamSimple_Passes_SimpleOptions_And_Dispatches()
    {
        SimpleStreamOptions? seenOptions = null;
        var models = new Models();
        var provider = ProviderTestSupport.CreateProvider(stream: (model, context, options) =>
        {
            seenOptions = options as SimpleStreamOptions;
            return ProviderTestSupport.DoneStream(model, context, options);
        });
        models.SetProvider(provider);
        var simple = new SimpleStreamOptions { ThinkingLevel = ModelThinkingLevel.High };

        var stream = models.StreamSimple(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() },
            simple);
        var events = await ProviderTestSupport.CollectAsync(stream);

        Assert.Equal(ModelThinkingLevel.High, seenOptions!.ThinkingLevel);
        Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
    }

    [Fact]
    public async Task CompleteAsync_Returns_Final_Message()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider());

        var message = await models.CompleteAsync(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() });

        Assert.Equal(StopReason.Stop, message.StopReason);
    }

    [Fact]
    public async Task CompleteSimpleAsync_Returns_Final_Message()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider());

        var message = await models.CompleteSimpleAsync(
            ProviderTestSupport.CreateModel(),
            new Context { Messages = Array.Empty<Message>() },
            new SimpleStreamOptions());

        Assert.Equal(StopReason.Stop, message.StopReason);
    }

    [Fact]
    public async Task GetAvailableAll_Fallback_Preserves_Order_And_Filters_Chat_Only()
    {
        var models = new Models();
        models.SetProvider(new FilterableProvider(
            new ProviderModel[]
            {
                ProviderTestSupport.CreateModel("chatA"),
                new ImageModel
                {
                    Id = "img1", Name = "img1", Api = "faux", Provider = "p1",
                    BaseUrl = "http://localhost:0",
                },
                ProviderTestSupport.CreateModel("chatB"),
                new ClassifierModel
                {
                    Id = "cls1", Name = "cls1", Api = "faux", Provider = "p1",
                    BaseUrl = "http://localhost:0",
                },
            },
            keptChatIds: new[] { "chatB" },
            filteredAll: null));

        var available = await models.GetAllAvailableAsync("p1");

        // 原顺序保留：chatA 被剔除，非 chat 类别原样保留。
        Assert.Equal(new[] { "img1", "chatB", "cls1" },
            available.Select(model => model.Id).ToArray());
    }

    [Fact]
    public async Task GetAvailableAll_FilterAllModels_Result_Wins_When_Not_Null()
    {
        var models = new Models();
        var winner = new ProviderModel[] { ProviderTestSupport.CreateModel("only") };
        models.SetProvider(new FilterableProvider(
            new ProviderModel[] { ProviderTestSupport.CreateModel("chatA") },
            keptChatIds: Array.Empty<string>(),
            filteredAll: winner));

        var available = await models.GetAllAvailableAsync("p1");

        // fallback 会把 chatA 剔成空；非 null 的 FilterAllModels 结果必须整体胜出。
        Assert.Equal(new[] { "only" }, available.Select(model => model.Id).ToArray());
    }

    /// <summary>Step 7 fallback 行为的测试载体：可过滤 provider，auth 走 keyless。</summary>
    private sealed class FilterableProvider : IProvider, IFilterableProvider
    {
        private readonly IReadOnlyList<ProviderModel> _all;
        private readonly IReadOnlySet<string> _keptChatIds;
        private readonly IReadOnlyList<ProviderModel>? _filteredAll;

        public FilterableProvider(
            IReadOnlyList<ProviderModel> all,
            IReadOnlyList<string> keptChatIds,
            IReadOnlyList<ProviderModel>? filteredAll)
        {
            _all = all;
            _keptChatIds = new HashSet<string>(keptChatIds, StringComparer.Ordinal);
            _filteredAll = filteredAll;
            Auth = ProviderTestSupport.KeylessAuth();
        }

        public string Id => "p1";
        public string Name => "p1";
        public string? BaseUrl => null;
        public IReadOnlyDictionary<string, string?>? Headers => null;
        public ProviderAuth Auth { get; }

        public IReadOnlyList<Model> GetModels()
            => _all.OfType<Model>().Where(model => _keptChatIds.Contains(model.Id)).ToList();

        public IReadOnlyList<ProviderModel> GetAllModels() => _all;

        public IReadOnlyList<Model> FilterModels(IReadOnlyList<Model> models, Credential? credential)
            => models.Where(model => _keptChatIds.Contains(model.Id)).ToList();

        public IReadOnlyList<ProviderModel>? FilterAllModels(
            IReadOnlyList<ProviderModel> models, Credential? credential)
            => _filteredAll;

        public IAssistantMessageEventStream Stream(
            Model model, TranscriptContext context, StreamOptions? options)
            => throw new NotSupportedException("Not used in this test.");

        public IAssistantMessageEventStream StreamSimple(
            Model model, TranscriptContext context, SimpleStreamOptions? options)
            => throw new NotSupportedException("Not used in this test.");
    }
}
