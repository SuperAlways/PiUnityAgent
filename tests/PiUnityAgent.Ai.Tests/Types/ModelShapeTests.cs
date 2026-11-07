using System.Text.Json;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class ModelShapeTests
{
    [Fact]
    public void Model_Is_A_Chat_ProviderModel_With_New_Auxiliary_Fields()
    {
        var model = new Model
        {
            Id = "m1",
            Name = "Model One",
            Api = "openai-completions",
            Provider = "openai",
            BaseUrl = "https://api.example.test",
            Reasoning = true,
            InputLimits = new ModelInputLimits
            {
                MaxRequestBytes = 1024,
                Images = new ModelImageInputLimits
                {
                    MaxPerMessage = 2,
                    MaxPerRequest = 4,
                    Resize = new ModelImageResizeOptions
                    {
                        MaxWidth = 1024,
                        MaxHeight = 1024,
                        MaxBytes = 512000,
                        JpegQuality = 80,
                    },
                },
            },
            SamplingParams = new JsonObject { ["top_p"] = 0.9 },
            SamplingParamsByThinkingLevel = new Dictionary<ModelThinkingLevel, JsonObject>
            {
                [ModelThinkingLevel.High] = new JsonObject { ["temperature"] = 0.2 },
            },
            Compat = new JsonObject { ["supportsStrictMode"] = true },
            PromptCache = new ModelPromptCache { Short = 300, Long = 3600 },
            Cost = new ModelCost(0.01, 0.02, 0.003, 0.004)
            {
                Tiers = new[]
                {
                    new ModelCostTier(1000, 0.008, 0.016, 0.002, 0.003),
                },
            },
        };

        Assert.Equal(ModelType.Chat, model.Type);
        Assert.Equal(1024, model.InputLimits!.MaxRequestBytes);
        Assert.Equal(80, model.InputLimits.Images!.Resize!.JpegQuality);
        Assert.Equal(0.9, model.SamplingParams!["top_p"]!.GetValue<double>());
        Assert.Equal(0.2, model.SamplingParamsByThinkingLevel![ModelThinkingLevel.High]["temperature"]!.GetValue<double>());
        Assert.True(model.Compat!["supportsStrictMode"]!.GetValue<bool>());
        Assert.Equal(3600, model.PromptCache!.Long);
        Assert.Single(model.Cost.Tiers!);
    }

    [Fact]
    public void Image_And_Classifier_Models_Report_Their_Types()
    {
        var image = new ImageModel
        {
            Id = "img",
            Name = "Image",
            Api = "openrouter-images",
            Provider = "openrouter",
            BaseUrl = "https://api.example.test",
        };
        var classifier = new ClassifierModel
        {
            Id = "cls",
            Name = "Classifier",
            Api = "typesafe-system-one",
            Provider = "typesafe",
            BaseUrl = "https://api.example.test",
            ContextWindow = 4096,
        };

        Assert.Equal(ModelType.Image, image.Type);
        Assert.Equal(ModelOutputModality.Image, image.Output[0]);
        Assert.Equal(ModelType.Classifier, classifier.Type);
        Assert.Equal(4096, classifier.ContextWindow);
    }

    [Fact]
    public void ProviderModel_Defaults_Are_Stable()
    {
        var model = new Model
        {
            Id = "m1",
            Name = "Model One",
            Api = "faux",
            Provider = "faux",
            BaseUrl = "http://localhost:0",
        };

        Assert.Equal(new[] { ModelInputModality.Text }, model.Input);
        Assert.Equal(128000, model.ContextWindow);
        Assert.Equal(16384, model.MaxTokens);
        Assert.Equal(0, model.Cost.Input);
        Assert.Null(model.InputLimits);
    }

    [Theory]
    [InlineData(ModelType.Chat, "\"chat\"")]
    [InlineData(ModelType.Image, "\"image\"")]
    [InlineData(ModelType.Classifier, "\"classifier\"")]
    public void ModelType_Uses_Pi_Wire_Names(ModelType value, string wire)
    {
        Assert.Equal(wire, JsonSerializer.Serialize(value));
        Assert.Equal(value, JsonSerializer.Deserialize<ModelType>(wire));
    }
}
