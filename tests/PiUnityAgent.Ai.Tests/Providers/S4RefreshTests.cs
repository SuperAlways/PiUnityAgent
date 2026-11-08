using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

/// <summary>
/// S4 Task 8A：D2 动态模型目录刷新（Models.RefreshAsync + IRefreshableProvider + IModelsStore）。
/// 语义对齐 pi models.ts：createProvider refreshModels 包装（models.ts:1129-1160）、
/// currentModels 覆盖（models.ts:1093-1103）、Models.refresh（models.ts:565-623）。
/// </summary>
public class S4RefreshTests
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

    private static Model Chat(string id, string provider, string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Api = "faux",
        Provider = provider,
        BaseUrl = "http://localhost:0",
    };

    private static ImageModel Image(string id, string provider) => new()
    {
        Id = id,
        Name = id,
        Api = "faux",
        Provider = provider,
        BaseUrl = "http://localhost:0",
    };

    private static CreateProviderOptions Options(
        string id,
        Func<RefreshModelsContext, Task<IReadOnlyList<ProviderModel>>>? fetchModels = null,
        IReadOnlyList<ProviderModel>? models = null)
        => new()
        {
            Id = id,
            Auth = Keyless(),
            Models = models ?? new ProviderModel[] { Chat("static", id) },
            Api = Streams(),
            FetchModels = fetchModels,
        };

    private static IReadOnlyList<ProviderModel> NoModels() => Array.Empty<ProviderModel>();

    [Fact]
    public void Factory_With_FetchModels_Is_Refreshable()
    {
        var provider = ProviderFactory.Create(Options("p1", fetchModels: _ => Task.FromResult(NoModels())));

        Assert.IsAssignableFrom<IRefreshableProvider>(provider);
    }

    [Fact]
    public void Factory_Without_FetchModels_Is_Not_Refreshable()
    {
        var provider = ProviderFactory.Create(Options("p1"));

        Assert.IsNotAssignableFrom<IRefreshableProvider>(provider);
    }

    [Fact]
    public async Task Refresh_Restores_Stored_Models_Without_Network()
    {
        var fetchCalls = 0;
        var store = new InMemoryModelsStore();
        await store.WriteAsync("p1", new ModelsStoreEntry
        {
            Models = new ProviderModel[] { Chat("cached", "p1") },
            CheckedAt = 1,
        });
        var models = new Models(new CreateModelsOptions { ModelsStore = store });
        models.SetProvider(ProviderFactory.Create(Options("p1", fetchModels: _ =>
        {
            fetchCalls++;
            return Task.FromResult(NoModels());
        })));

        // AllowNetwork 缺省 false：只走存储恢复阶段（S4 偏离 pi 缺省 true）。
        var result = await models.RefreshAsync();

        Assert.False(result.Aborted);
        Assert.Empty(result.Errors);
        Assert.Equal(0, fetchCalls);
        // 存储条目未被删除/覆盖。
        var entry = await store.ReadAsync("p1");
        Assert.NotNull(entry);
        Assert.Equal(1, entry!.CheckedAt);
        // 覆盖表 = 静态基线 + 恢复的动态条目。
        var chat = models.GetModels("p1");
        Assert.Contains(chat, model => model.Id == "static");
        Assert.Contains(chat, model => model.Id == "cached");
    }

    [Fact]
    public async Task Refresh_Fetches_Persists_And_Overlays_Baseline()
    {
        var store = new InMemoryModelsStore();
        var models = new Models(new CreateModelsOptions { ModelsStore = store });
        models.SetProvider(ProviderFactory.Create(Options("p1", fetchModels: _ => Task.FromResult<IReadOnlyList<ProviderModel>>(
            new ProviderModel[]
            {
                // 同 type+id 替换基线；新 id 追加；图片模型保留（非 chat）。
                Chat("static", "p1", "static-renamed"),
                Chat("fresh", "p1"),
                Image("img", "p1"),
            }))));

        var result = await models.RefreshAsync(new ModelsRefreshOptions { AllowNetwork = true });

        Assert.False(result.Aborted);
        Assert.Empty(result.Errors);
        var entry = await store.ReadAsync("p1");
        Assert.NotNull(entry);
        Assert.Equal(3, entry!.Models.Count);
        Assert.NotNull(entry.CheckedAt);
        Assert.True(entry.CheckedAt > 1);
        // GetModels = 覆盖后的 chat；替换版保留基线位置，新增追加其后。
        var chat = models.GetModels("p1");
        Assert.Equal(2, chat.Count);
        Assert.Equal("static", chat[0].Id);
        Assert.Equal("static-renamed", chat[0].Name);
        Assert.Equal("fresh", chat[1].Id);
        // GetAllModels = 覆盖后的全类型表。
        var all = models.GetAllModels("p1");
        Assert.Equal(3, all.Count);
        Assert.Contains(all, model => model.Type == ModelType.Image && model.Id == "img");
    }

    [Fact]
    public async Task Refresh_Fetch_Failure_Retains_Old_Models_And_Reports_Error()
    {
        var store = new InMemoryModelsStore();
        await store.WriteAsync("p1", new ModelsStoreEntry
        {
            Models = new ProviderModel[] { Chat("cached", "p1") },
            CheckedAt = 1,
        });
        var models = new Models(new CreateModelsOptions { ModelsStore = store });
        models.SetProvider(ProviderFactory.Create(Options("p1", fetchModels: _ =>
            throw new InvalidOperationException("boom"))));

        var result = await models.RefreshAsync(new ModelsRefreshOptions { AllowNetwork = true });

        Assert.False(result.Aborted);
        Assert.True(result.Errors.TryGetValue("p1", out var error));
        Assert.IsType<InvalidOperationException>(error);
        // 旧表保留：恢复阶段点亮的 stored 列表不被网络阶段破坏。
        var chat = models.GetModels("p1");
        Assert.Contains(chat, model => model.Id == "cached");
        // store 未被网络阶段覆盖。
        var entry = await store.ReadAsync("p1");
        Assert.NotNull(entry);
        var kept = Assert.Single(entry!.Models);
        Assert.Equal("cached", kept.Id);
    }

    [Fact]
    public async Task Refresh_Providers_Filter_Only_Touches_Selected()
    {
        var store = new InMemoryModelsStore();
        var calls = new List<string>();
        var models = new Models(new CreateModelsOptions { ModelsStore = store });
        models.SetProvider(ProviderFactory.Create(Options("p1", fetchModels: _ =>
        {
            calls.Add("p1");
            return Task.FromResult<IReadOnlyList<ProviderModel>>(new ProviderModel[] { Chat("fresh1", "p1") });
        })));
        models.SetProvider(ProviderFactory.Create(Options("p2", fetchModels: _ =>
        {
            calls.Add("p2");
            return Task.FromResult<IReadOnlyList<ProviderModel>>(new ProviderModel[] { Chat("fresh2", "p2") });
        })));

        var result = await models.RefreshAsync(new ModelsRefreshOptions
        {
            AllowNetwork = true,
            Providers = new[] { "p1" },
        });

        Assert.False(result.Aborted);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "p1" }, calls);
        Assert.Contains(models.GetModels("p1"), model => model.Id == "fresh1");
        Assert.DoesNotContain(models.GetModels("p2"), model => model.Id == "fresh2");
        Assert.Null(await store.ReadAsync("p2"));
    }

    [Fact]
    public async Task Refresh_Cancelled_Returns_Aborted_Without_Fetch()
    {
        var fetchCalls = 0;
        var models = new Models(new CreateModelsOptions { ModelsStore = new InMemoryModelsStore() });
        models.SetProvider(ProviderFactory.Create(Options("p1", fetchModels: _ =>
        {
            fetchCalls++;
            return Task.FromResult(NoModels());
        })));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await models.RefreshAsync(new ModelsRefreshOptions
        {
            AllowNetwork = true,
            CancellationToken = cts.Token,
        });

        Assert.True(result.Aborted);
        Assert.Equal(0, fetchCalls);
    }

    [Fact]
    public async Task Refresh_Publish_Delete_Removes_Store_Entry()
    {
        var store = new InMemoryModelsStore();
        await store.WriteAsync("p1", new ModelsStoreEntry
        {
            Models = new ProviderModel[] { Chat("cached", "p1") },
            CheckedAt = 1,
        });
        var models = new Models(new CreateModelsOptions { ModelsStore = store });
        models.SetProvider(new RefreshDecorator(
            ProviderFactory.Create(Options("p1")),
            context => context.Publish(new ModelsPublication { Delete = true })));

        var result = await models.RefreshAsync();

        Assert.False(result.Aborted);
        Assert.Empty(result.Errors);
        Assert.Null(await store.ReadAsync("p1"));
    }

    /// <summary>给既有 IProvider 补一个自定义 RefreshModelsAsync，用于验证 Models 侧发布路径（Delete）。</summary>
    private sealed class RefreshDecorator(
        IProvider inner,
        Func<RefreshModelsContext, Task> refresh) : IProvider, IRefreshableProvider
    {
        public string Id => inner.Id;
        public string Name => inner.Name;
        public string? BaseUrl => inner.BaseUrl;
        public IReadOnlyDictionary<string, string?>? Headers => inner.Headers;
        public ProviderAuth Auth => inner.Auth;

        public IReadOnlyList<Model> GetModels() => inner.GetModels();
        public IReadOnlyList<ProviderModel> GetAllModels() => inner.GetAllModels();

        public IAssistantMessageEventStream Stream(Model model, TranscriptContext context, StreamOptions? options)
            => inner.Stream(model, context, options);

        public IAssistantMessageEventStream StreamSimple(Model model, TranscriptContext context, SimpleStreamOptions? options)
            => inner.StreamSimple(model, context, options);

        public Task RefreshModelsAsync(RefreshModelsContext context) => refresh(context);
    }
}
