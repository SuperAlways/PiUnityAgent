using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class ModelOperationsTests
{
    private static Model Chat(string id = "m1", string api = "faux")
        => new()
        {
            Id = id,
            Name = id,
            Api = api,
            Provider = "p1",
            BaseUrl = "http://localhost:0",
            Reasoning = true,
            ThinkingLevelMap = new Dictionary<ModelThinkingLevel, string?>
            {
                [ModelThinkingLevel.Low] = "low",
                [ModelThinkingLevel.XHigh] = "xhigh",
                [ModelThinkingLevel.Max] = null,
            },
        };

    [Fact]
    public void Type_And_Api_Checks_Work()
    {
        var model = Chat();
        Assert.Equal(ModelType.Chat, ModelOperations.GetModelType(model));
        Assert.True(ModelOperations.IsModelType(model, ModelType.Chat));
        Assert.False(ModelOperations.IsModelType(model, ModelType.Image));
        Assert.True(ModelOperations.HasApi(model, "faux"));
        Assert.False(ModelOperations.HasApi(model, "other"));
    }

    [Fact]
    public void ModelsAreEqual_Compares_Type_Id_Provider()
    {
        var a = Chat();
        var b = a with { Name = "different name" };
        var c = a with { Provider = "p2" };

        Assert.True(ModelOperations.ModelsAreEqual(a, b));
        Assert.False(ModelOperations.ModelsAreEqual(a, c));
        Assert.False(ModelOperations.ModelsAreEqual(a, null));
    }

    [Fact]
    public void CalculateCost_Uses_Tier_And_CacheWrite1h()
    {
        var model = Chat() with
        {
            Cost = new ModelCost(1.0, 2.0, 0.5, 3.0)
            {
                Tiers = new[] { new ModelCostTier(100, 2.0, 4.0, 1.0, 6.0) },
            },
        };
        var usage = new Usage
        {
            Input = 100,
            Output = 10,
            CacheRead = 0,
            CacheWrite = 2,
            CacheWrite1h = 1,
            TotalTokens = 112,
        };

        var cost = ModelOperations.CalculateCost(model, usage);

        Assert.Equal(0.0002, cost.Input, 6);
        Assert.Equal(0.00004, cost.Output, 6);
        Assert.Equal(0.00001, cost.CacheWrite, 6);
        Assert.Equal(0.00025, cost.Total, 6);
    }

    [Fact]
    public void ThinkingLevels_Respect_Map()
    {
        var model = Chat();
        var levels = ModelOperations.GetSupportedThinkingLevels(model);

        Assert.Contains(ModelThinkingLevel.Off, levels);
        Assert.Contains(ModelThinkingLevel.Low, levels);
        Assert.Contains(ModelThinkingLevel.XHigh, levels);
        Assert.DoesNotContain(ModelThinkingLevel.Max, levels);
        Assert.Equal(ModelThinkingLevel.XHigh,
            ModelOperations.ClampThinkingLevel(model, ModelThinkingLevel.Max));
    }
}
