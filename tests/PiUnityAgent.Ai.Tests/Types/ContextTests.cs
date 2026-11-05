using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class ContextTests
{
    [Fact]
    public void Tool_Requires_Core_Fields_And_ConstrainedSampling()
    {
        var tool = new Tool
        {
            Name = "query_hp",
            Description = "Query current HP",
            Parameters = new JsonObject { ["type"] = "object" },
            ConstrainedSampling = new ConstrainedSampling.Config(
                new ConstrainedSamplingConfig.JsonSchema(StrictMode.Require)),
        };

        Assert.Equal("query_hp", tool.Name);
        Assert.IsType<ConstrainedSampling.Config>(tool.ConstrainedSampling);
    }

    [Fact]
    public void Context_Requires_Messages()
    {
        var context = new Context { Messages = Array.Empty<Message>() };
        var transcript = new TranscriptContext { Messages = Array.Empty<Message>() };

        Assert.Empty(context.Messages);
        Assert.Empty(transcript.Messages);
    }
}
