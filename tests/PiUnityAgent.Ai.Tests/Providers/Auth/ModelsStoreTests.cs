using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers.Auth;

public class ModelsStoreTests
{
    [Fact]
    public async Task InMemoryModelsStore_Reads_Writes_And_Deletes()
    {
        var store = new InMemoryModelsStore();
        var model = new Model
        {
            Id = "m1",
            Name = "m1",
            Api = "faux",
            Provider = "p1",
            BaseUrl = "http://localhost:0",
        };
        var entry = new ModelsStoreEntry
        {
            Models = new ProviderModel[] { model },
            LastModified = 100,
            CheckedAt = 200,
            Etag = "\"etag-1\"",
        };

        await store.WriteAsync("p1", entry);
        var loaded = await store.ReadAsync("p1");
        var storedModel = Assert.IsType<Model>(Assert.Single(loaded!.Models));
        Assert.Equal("m1", storedModel.Id);
        Assert.Equal(100, loaded.LastModified);
        Assert.Equal("\"etag-1\"", loaded.Etag);

        await store.DeleteAsync("p1");
        Assert.Null(await store.ReadAsync("p1"));
    }

    [Fact]
    public void Publication_Can_Express_Delete()
    {
        var publication = new ModelsPublication { Delete = true };
        Assert.True(publication.Delete);
        Assert.Null(publication.Persist);
    }
}
