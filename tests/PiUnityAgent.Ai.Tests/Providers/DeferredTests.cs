using System.Collections.Generic;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class DeferredProviderCapabilityTests
{
    private static Model Model()
        => ProviderTestSupport.CreateModel("m1", "p1");

    private static IAssistantMessageEventStream Done(Model model, StopReason reason, DeferredHandle? handle = null)
    {
        var stream = new AssistantMessageEventStream();
        var message = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = reason,
            Deferred = handle,
            Timestamp = 1,
        };
        stream.Push(new AssistantMessageEvent.Done(reason, message));
        stream.End(message);
        return stream;
    }

    private static DeferredHandle Handle(string id = "d1") => new()
    {
        Provider = "p1",
        ModelId = "m1",
        Api = "faux",
        Id = id,
    };

    [Fact]
    public async Task StaticProvider_Reports_Capability_From_Delegates_And_Throws_When_Missing()
    {
        var model = Model();
        var without = ProviderTestSupport.CreateProvider("p1");
        Assert.False(Assert.IsAssignableFrom<IDeferredProvider>(without).SupportsFetchDeferred);
        Assert.False(Assert.IsAssignableFrom<IDeferredProvider>(without).SupportsCancelDeferred);

        var with = new StaticProvider(
            "p1", "p1", new[] { model }, ProviderTestSupport.DoneStream,
            ProviderTestSupport.KeylessAuth(),
            fetchDeferred: (requestModel, handle, options) => Done(requestModel, StopReason.Deferred, handle),
            cancelDeferred: (requestModel, handle, options, signal) => Task.CompletedTask);

        var deferred = Assert.IsAssignableFrom<IDeferredProvider>(with);
        Assert.True(deferred.SupportsFetchDeferred);
        Assert.True(deferred.SupportsCancelDeferred);
        var message = Assert.IsType<AssistantMessage>(await deferred.StreamDeferred(model, Handle(), null)
            .WaitForDoneAsync());
        Assert.Equal(StopReason.Deferred, message.StopReason);
        Assert.Equal("d1", message.Deferred!.Id);
    }

    [Fact]
    public async Task FactoryProvider_Exposes_Deferred_From_Api_Streams()
    {
        var model = Model();
        var capturedHandle = Handle("d2");
        var options = new CreateProviderOptions
        {
            Id = "p1",
            Auth = ProviderTestSupport.KeylessAuth(),
            Models = new ProviderModel[] { model },
            Api = new ProviderStreams
            {
                Stream = ProviderTestSupport.DoneStream,
                StreamSimple = (requestModel, context, streamOptions) =>
                    ProviderTestSupport.DoneStream(requestModel, context, streamOptions),
                FetchDeferred = (requestModel, handle, fetchOptions) =>
                    Done(requestModel, StopReason.Deferred, handle),
                CancelDeferred = (requestModel, handle, cancelOptions, signal) =>
                {
                    capturedHandle = handle;
                    return Task.CompletedTask;
                },
            },
        };

        var provider = ProviderFactory.Create(options);
        var deferred = Assert.IsAssignableFrom<IDeferredProvider>(provider);
        Assert.True(deferred.SupportsFetchDeferred);
        Assert.True(deferred.SupportsCancelDeferred);

        var message = Assert.IsType<AssistantMessage>(await deferred.StreamDeferred(model, Handle("d2"), null)
            .WaitForDoneAsync());
        Assert.Equal(StopReason.Deferred, message.StopReason);

        await deferred.CancelDeferredAsync(model, Handle("d3"), null);
        Assert.Equal("d3", capturedHandle.Id);
    }

    [Fact]
    public async Task FactoryProvider_Without_Deferred_Reports_Unsupported()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p1",
            Auth = ProviderTestSupport.KeylessAuth(),
            Models = new ProviderModel[] { Model() },
            Api = new ProviderStreams
            {
                Stream = ProviderTestSupport.DoneStream,
                StreamSimple = (requestModel, context, streamOptions) =>
                    ProviderTestSupport.DoneStream(requestModel, context, streamOptions),
            },
        });

        var deferred = Assert.IsAssignableFrom<IDeferredProvider>(provider);
        Assert.False(deferred.SupportsFetchDeferred);
        Assert.False(deferred.SupportsCancelDeferred);
        Assert.Throws<ModelsError>(() => deferred.StreamDeferred(Model(), Handle(), null));
        await Assert.ThrowsAsync<ModelsError>(() => deferred.CancelDeferredAsync(Model(), Handle(), null));
    }

    [Fact]
    public void FactoryProvider_Capability_Scan_Tolerates_Null_ApiByApi_Entry()
    {
        // 工厂校验把 null 条目当"无实现"容忍；能力扫描不得在 null 条目上炸 NRE。
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p1",
            Auth = ProviderTestSupport.KeylessAuth(),
            Models = new ProviderModel[] { Model() },
            ApiByApi = new Dictionary<string, ProviderStreams>
            {
                ["faux"] = new ProviderStreams
                {
                    Stream = ProviderTestSupport.DoneStream,
                    StreamSimple = (requestModel, context, streamOptions) =>
                        ProviderTestSupport.DoneStream(requestModel, context, streamOptions),
                    FetchDeferred = (requestModel, handle, fetchOptions) =>
                        Done(requestModel, StopReason.Deferred, handle),
                },
                ["broken"] = null!,
            },
        });

        var deferred = Assert.IsAssignableFrom<IDeferredProvider>(provider);
        Assert.True(deferred.SupportsFetchDeferred);
        Assert.False(deferred.SupportsCancelDeferred);
    }
}

public class DeferredModelsTests
{
    private static Model Model() => ProviderTestSupport.CreateModel("m1", "p1");

    private static DeferredHandle Handle(string id = "d1") => new()
    {
        Provider = "p1",
        ModelId = "m1",
        Api = "faux",
        Id = id,
    };

    private static IAssistantMessageEventStream Done(Model model, StopReason reason)
    {
        var stream = new AssistantMessageEventStream();
        var message = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = reason,
            Timestamp = 1,
        };
        stream.Push(new AssistantMessageEvent.Done(reason, message));
        stream.End(message);
        return stream;
    }

    [Fact]
    public async Task Unsupported_Provider_Becomes_Error_Terminal_And_Cancel_Throws()
    {
        var models = new Models();
        models.SetProvider(ProviderTestSupport.CreateProvider("p1"));
        var handle = Handle();

        var final = await models.StreamDeferred(Model(), handle).WaitForDoneAsync();
        Assert.Equal(StopReason.Error, final.StopReason);
        Assert.Equal("Provider p1 does not support deferred responses", final.ErrorMessage);

        var fetched = await models.FetchDeferredAsync(Model(), handle);
        Assert.Equal(StopReason.Error, fetched.StopReason);
        Assert.Equal("Provider p1 does not support deferred responses", fetched.ErrorMessage);

        var error = await Assert.ThrowsAsync<ModelsError>(
            () => models.CancelDeferredAsync(Model(), handle));
        Assert.Equal(ModelsErrorCode.Provider, error.Code);
        Assert.Equal("Provider p1 does not support deferred responses", error.Message);
    }

    [Fact]
    public async Task Auth_BaseUrl_Overlays_Before_Provider_Fetch()
    {
        Model? seen = null;
        var auth = new PiUnityAgent.Ai.Providers.Auth.ProviderAuth
        {
            ApiKey = new PiUnityAgent.Ai.Providers.Auth.ApiKeyAuth
            {
                Name = "keyless",
                Resolve = (_, _) => Task.FromResult<PiUnityAgent.Ai.Providers.Auth.AuthResult?>(
                    new PiUnityAgent.Ai.Providers.Auth.AuthResult(
                        new PiUnityAgent.Ai.Providers.Auth.ModelAuth
                        {
                            BaseUrl = "https://proxy.example/v1",
                        },
                        null,
                        "test")),
            },
        };

        var model = Model();
        var provider = new StaticProvider(
            "p1", "p1", new[] { model }, ProviderTestSupport.DoneStream, auth,
            fetchDeferred: (requestModel, handle, options) =>
            {
                seen = requestModel;
                return Done(requestModel, StopReason.Deferred);
            });

        var models = new Models();
        models.SetProvider(provider);
        await models.StreamDeferred(model, Handle()).WaitForDoneAsync();

        Assert.NotNull(seen);
        Assert.Equal("https://proxy.example/v1", seen!.BaseUrl);
    }

    [Fact]
    public async Task FetchDeferredAsync_Returns_Stream_Result()
    {
        var model = Model();
        var provider = new StaticProvider(
            "p1", "p1", new[] { model }, ProviderTestSupport.DoneStream,
            ProviderTestSupport.KeylessAuth(),
            fetchDeferred: (requestModel, handle, options) => Done(requestModel, StopReason.Stop));

        var models = new Models();
        models.SetProvider(provider);
        var message = await models.FetchDeferredAsync(model, Handle());
        Assert.Equal(StopReason.Stop, message.StopReason);
    }
}

public class FauxDeferredTests
{
    private static TranscriptContext Context() => new() { Messages = Array.Empty<Message>() };

    [Fact]
    public async Task Deferred_Request_Emits_Handle_And_Fetch_Returns_Final_After_Pending()
    {
        var faux = new FauxProvider(new FauxProviderOptions
        {
            Deferred = new FauxDeferredOptions { PendingFetches = 1, PollAfterMs = 50 },
        });
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("final"))),
        });
        var model = faux.GetModel();

        var stream = faux.Provider.StreamSimple(model, Context(), new SimpleStreamOptions
        {
            Deferred = new DeferredRequest.Enabled("1h"),
        });
        var first = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Deferred, first.StopReason);
        Assert.NotNull(first.Deferred);
        Assert.StartsWith("deferred:", first.Deferred!.Id);
        Assert.Equal(50, first.Deferred!.PollAfterMs);

        var provider = Assert.IsAssignableFrom<IDeferredProvider>(faux.Provider);
        var pending = await provider.FetchDeferredAsync(model, first.Deferred!, null);
        Assert.Equal(StopReason.Deferred, pending.StopReason);
        Assert.Equal(1, faux.State.DeferredFetchCount);

        var final = await provider.FetchDeferredAsync(model, first.Deferred!, null);
        Assert.Equal(StopReason.Stop, final.StopReason);
        Assert.Equal("final", Assert.IsType<TextContent>(final.Content[0]).Text);
        Assert.Equal(2, faux.State.DeferredFetchCount);

        var cached = await provider.FetchDeferredAsync(model, first.Deferred!, null);
        Assert.Equal("final", Assert.IsType<TextContent>(cached.Content[0]).Text);
        Assert.Equal(3, faux.State.DeferredFetchCount);
    }

    [Fact]
    public async Task Cancel_Marks_Handle_And_Fetch_Reports_Cancelled()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("final"))),
        });
        var model = faux.GetModel();

        var first = await faux.Provider.StreamSimple(model, Context(), new SimpleStreamOptions
        {
            Deferred = new DeferredRequest.Enabled(),
        }).WaitForDoneAsync();
        var handle = first.Deferred!;
        var provider = Assert.IsAssignableFrom<IDeferredProvider>(faux.Provider);

        await provider.CancelDeferredAsync(model, handle, null);
        Assert.Single(faux.State.CancelledDeferred);
        Assert.Equal(handle.Id, faux.State.CancelledDeferred[0].Id);

        var fetched = await provider.FetchDeferredAsync(model, handle, null);
        Assert.Equal(StopReason.Error, fetched.StopReason);
        Assert.Equal($"Faux deferred response was cancelled: {handle.Id}", fetched.ErrorMessage);
    }

    [Fact]
    public async Task Unknown_Handle_Reports_Exact_Error()
    {
        var faux = new FauxProvider();
        var model = faux.GetModel();
        var unknown = new DeferredHandle
        {
            Provider = model.Provider,
            ModelId = model.Id,
            Api = model.Api,
            Id = "missing",
        };

        var provider = Assert.IsAssignableFrom<IDeferredProvider>(faux.Provider);
        var fetched = await provider.FetchDeferredAsync(model, unknown, null);
        Assert.Equal(StopReason.Error, fetched.StopReason);
        Assert.Equal("Unknown faux deferred response: missing", fetched.ErrorMessage);
    }

    [Fact]
    public async Task Disabled_Deferred_Produces_Normal_Response()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("normal"))),
        });
        var message = await faux.Provider.StreamSimple(faux.GetModel(), Context(), new SimpleStreamOptions
        {
            Deferred = new DeferredRequest.Disabled(),
        }).WaitForDoneAsync();

        Assert.Equal(StopReason.Stop, message.StopReason);
        Assert.Null(message.Deferred);
    }

    [Fact]
    public async Task Fetch_With_Cancelled_Token_Ends_Aborted()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new FauxResponseStep[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(Faux.Text("final"))),
        });
        var model = faux.GetModel();
        var first = await faux.Provider.StreamSimple(model, Context(), new SimpleStreamOptions
        {
            Deferred = new DeferredRequest.Enabled(),
        }).WaitForDoneAsync();
        var provider = Assert.IsAssignableFrom<IDeferredProvider>(faux.Provider);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stream = provider.StreamDeferred(model, first.Deferred!, new DeferredFetchOptions
        {
            CancellationToken = cts.Token,
        });
        var final = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Aborted, final.StopReason);
    }
}
