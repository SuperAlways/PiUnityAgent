using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

public class FauxStreamTests
{
    private static async Task<List<AssistantMessageEvent>> CollectAsync(
        IAssistantMessageEventStream stream, CancellationToken cancellationToken = default)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream.WithCancellation(cancellationToken))
        {
            events.Add(@event);
        }
        return events;
    }

    [Fact]
    public async Task Text_Response_Emits_Start_Deltas_End_Done()
    {
        var faux = new FauxProvider(new FauxProviderOptions { MinTokenSize = 3, MaxTokenSize = 3 });
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("abcdef")),
        });

        var model = faux.GetModel();
        var stream = await faux.Provider.Stream(
            model,
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var events = await CollectAsync(stream);

        Assert.IsType<AssistantMessageEvent.Start>(events[0]);
        Assert.Equal(2, events.OfType<AssistantMessageEvent.TextDelta>().Count());
        Assert.IsType<AssistantMessageEvent.TextEnd>(events[^2]);
        Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(1, faux.State.CallCount);
    }

    [Fact]
    public async Task Thinking_And_ToolCall_Emit_Their_Events()
    {
        var faux = new FauxProvider(new FauxProviderOptions { MinTokenSize = 2, MaxTokenSize = 2 });
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(new ContentBlock[]
            {
                Faux.Thinking("hmm"),
                Faux.ToolCall("query_hp", new JsonObject { ["target"] = "player" }, "call_1"),
            }, StopReason.ToolUse)),
        });

        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var events = await CollectAsync(stream);

        Assert.Contains(events, e => e is AssistantMessageEvent.ThinkingStart);
        Assert.Contains(events, e => e is AssistantMessageEvent.ThinkingDelta);
        Assert.Contains(events, e => e is AssistantMessageEvent.ToolCallStart);
        Assert.Contains(events, e => e is AssistantMessageEvent.ToolCallDelta);
        Assert.Contains(events, e => e is AssistantMessageEvent.ToolCallEnd);
        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(StopReason.ToolUse, done.Reason);
    }

    [Fact]
    public async Task Empty_Queue_Emits_Error()
    {
        var faux = new FauxProvider();
        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Contains("No more faux responses", done.ErrorMessage);
    }

    [Fact]
    public async Task Error_Message_Emits_Error_Event()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("bad", StopReason.Error, "boom")),
        });

        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var events = await CollectAsync(stream);

        Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Error, (await stream.WaitForDoneAsync()).StopReason);
    }

    [Fact]
    public async Task Factory_Receives_Context_And_State()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Factory((context, options, state, model) =>
                Task.FromResult(Faux.AssistantMessage("from-factory"))),
        });

        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            CancellationToken.None);
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Stop, done.StopReason);
        Assert.Equal(1, faux.State.CallCount);
    }

    [Fact]
    public async Task Cancellation_Emits_Aborted()
    {
        var faux = new FauxProvider(new FauxProviderOptions
        {
            MinTokenSize = 1,
            MaxTokenSize = 1,
            TokensPerSecond = 1000,
        });
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(new string('x', 50))),
        });

        using var cts = new CancellationTokenSource();
        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            new TranscriptContext { Messages = Array.Empty<PiUnityAgent.Ai.Types.Message>() },
            null,
            cts.Token);
        cts.Cancel();

        var done = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Aborted, done.StopReason);
    }
}
