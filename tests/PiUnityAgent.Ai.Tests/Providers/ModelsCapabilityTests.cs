using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class ModelsCapabilityTests
{
    private static readonly Model Model = ProviderTestSupport.CreateModel();

    [Fact]
    public void Deferred_Methods_Are_Deferred_To_S8()
    {
        var models = new Models();
        var handle = new DeferredHandle
        {
            Provider = "p1",
            ModelId = "m1",
            Api = "faux",
            Id = "deferred-1",
        };

        var streamException = Assert.Throws<NotSupportedException>(() =>
            models.StreamDeferred(Model, handle));
        var fetchException = Assert.Throws<NotSupportedException>(() =>
        {
            _ = models.FetchDeferredAsync(Model, handle);
        });
        var cancelException = Assert.Throws<NotSupportedException>(() =>
        {
            _ = models.CancelDeferredAsync(Model, handle);
        });

        Assert.Contains("S8", streamException.Message);
        Assert.Contains("D3", streamException.Message);
        Assert.Contains("D3", fetchException.Message);
        Assert.Contains("D3", cancelException.Message);
    }

    [Fact]
    public void Images_And_Classify_Are_Deferred_To_2B()
    {
        var models = new Models();
        var image = new ImageModel
        {
            Id = "img",
            Name = "Image",
            Api = "openrouter-images",
            Provider = "openrouter",
            BaseUrl = "http://localhost:0",
        };
        var classifier = new ClassifierModel
        {
            Id = "cls",
            Name = "Classifier",
            Api = "typesafe-system-one",
            Provider = "typesafe",
            BaseUrl = "http://localhost:0",
        };

        var imageException = Assert.Throws<NotSupportedException>(() =>
        {
            _ = models.GenerateImagesAsync(image, new ImagesContext());
        });
        var classifierException = Assert.Throws<NotSupportedException>(() =>
        {
            _ = models.ClassifyAsync(classifier, new ClassifierContext());
        });

        Assert.Contains("2B", imageException.Message);
        Assert.Contains("D4", imageException.Message);
        Assert.Contains("2B", classifierException.Message);
        Assert.Contains("D5", classifierException.Message);
    }
}
