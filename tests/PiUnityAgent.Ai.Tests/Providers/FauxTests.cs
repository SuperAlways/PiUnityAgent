using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Providers;
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
