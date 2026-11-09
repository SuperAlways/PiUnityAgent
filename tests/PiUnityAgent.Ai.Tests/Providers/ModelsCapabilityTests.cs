using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class ModelsCapabilityTests
{
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
