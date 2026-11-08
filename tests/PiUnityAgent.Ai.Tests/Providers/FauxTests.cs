using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class FauxTests
{
    [Fact]
    public void Default_Model_And_Provider_Are_Registered()
    {
        var faux = new FauxProvider();
        var model = faux.GetModel();

        Assert.Equal("faux", faux.Provider.Id);
        Assert.Equal("faux-1", model.Id);
        Assert.Equal("faux", model.Api);
        Assert.Contains(model, faux.Models);
    }

    [Fact]
    public void Provider_Is_IProvider_And_Lists_Chat_Models()
    {
        var faux = new FauxProvider();
        var provider = Assert.IsAssignableFrom<IProvider>(faux.Provider);

        Assert.Single(provider.GetModels());
        Assert.Single(provider.GetAllModels());
        Assert.Equal(Faux.DefaultModelId, provider.GetModels()[0].Id);
    }

    [Fact]
    public void Set_Append_And_Pending_Responses()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("a")),
        });
        faux.AppendResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("b")),
        });

        Assert.Equal(2, faux.PendingResponseCount);
    }

    [Fact]
    public async Task Provider_Has_Keyless_Auth()
    {
        var faux = new FauxProvider();
        var auth = faux.Provider.Auth;

        Assert.NotNull(faux.Provider.Auth);
        Assert.NotNull(auth.ApiKey);
        Assert.Equal("Faux", auth.ApiKey!.Name);
        var resolved = await auth.ApiKey!.Resolve(
            new ApiKeyResolveInput(new DefaultAuthContext(), null), default);

        Assert.NotNull(resolved);
    }

    [Fact]
    public void Model_Definition_Carries_InputLimits()
    {
        var faux = new FauxProvider(new FauxProviderOptions
        {
            Models = new[]
            {
                new FauxModelDefinition
                {
                    Id = "m1",
                    InputLimits = new ModelInputLimits { MaxRequestBytes = 100 },
                },
            },
        });

        Assert.Equal(100, faux.GetModel("m1").InputLimits!.MaxRequestBytes);
    }

    [Fact]
    public void Provider_State_Exposes_Deferred_Shape()
    {
        // D3 形状锚点：DeferredFetchCount/CancelledDeferred 仅保形状，行为后置 S8。
        var faux = new FauxProvider();

        Assert.Equal(0, faux.State.DeferredFetchCount);
        Assert.NotNull(faux.State.CancelledDeferred);
        Assert.Empty(faux.State.CancelledDeferred);
    }

    [Fact]
    public void Helpers_Create_Content_And_Messages()
    {
        var text = Faux.Text("hi");
        var thinking = Faux.Thinking("hmm");
        var call = Faux.ToolCall("query_hp", new JsonObject { ["target"] = "player" }, "call_1");
        var message = Faux.AssistantMessage(new ContentBlock[] { text, thinking, call }, StopReason.ToolUse);

        Assert.Equal("text", text.Type);
        Assert.Equal("thinking", thinking.Type);
        Assert.Equal("toolCall", call.Type);
        Assert.Equal(StopReason.ToolUse, message.StopReason);
        Assert.Equal(3, message.Content.Count);
    }
}
