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
    public void StaticProvider_Reports_Capability_From_Delegates_And_Throws_When_Missing()
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
        var message = Assert.IsType<AssistantMessage>(deferred.StreamDeferred(model, Handle(), null)
            .WaitForDoneAsync().Result);
        Assert.Equal(StopReason.Deferred, message.StopReason);
        Assert.Equal("d1", message.Deferred!.Id);
    }

    [Fact]
    public void FactoryProvider_Exposes_Deferred_From_Api_Streams()
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

        var message = Assert.IsType<AssistantMessage>(deferred.StreamDeferred(model, Handle("d2"), null)
            .WaitForDoneAsync().Result);
        Assert.Equal(StopReason.Deferred, message.StopReason);

        deferred.CancelDeferredAsync(model, Handle("d3"), null).GetAwaiter().GetResult();
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
