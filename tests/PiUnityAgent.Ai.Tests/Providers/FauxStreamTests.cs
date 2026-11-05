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

    private static TranscriptContext EmptyContext()
        => new() { Messages = Array.Empty<Message>() };

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
        TranscriptContext? seenContext = null;
        SimpleStreamOptions? seenOptions = null;
        FauxProviderState? seenState = null;
        Model? seenModel = null;
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Factory((context, options, state, model) =>
            {
                seenContext = context;
                seenOptions = options;
                seenState = state;
                seenModel = model;
                return Task.FromResult(Faux.AssistantMessage("from-factory"));
            }),
        });

        var model = faux.GetModel();
        var context = EmptyContext();
        var options = new SimpleStreamOptions { Temperature = 0.5 };
        var stream = await faux.Provider.Stream(model, context, options, CancellationToken.None);
        var done = await stream.WaitForDoneAsync();

        Assert.Equal(StopReason.Stop, done.StopReason);
        Assert.Equal(1, faux.State.CallCount);
        Assert.Same(context, seenContext);
        Assert.Same(options, seenOptions);
        Assert.Same(faux.State, seenState);
        Assert.Same(model, seenModel);
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
            EmptyContext(),
            null,
            cts.Token);
        cts.Cancel();

        var events = await CollectAsync(stream);
        var terminal = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Aborted, terminal.Reason);
        Assert.Equal("Request was aborted", terminal.ErrorMessage.ErrorMessage);

        var done = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Aborted, done.StopReason);
    }

    [Fact]
    public async Task PreCancelled_Token_Emits_Error_Only()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("hello")),
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stream = await faux.Provider.Stream(faux.GetModel(), EmptyContext(), null, cts.Token);
        var events = await CollectAsync(stream);

        // 预取消：仅 [Error] 一个事件，没有 Start。
        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Aborted, error.Reason);
        Assert.Equal("Request was aborted", error.ErrorMessage.ErrorMessage);
        Assert.NotEqual(0, error.ErrorMessage.Timestamp);
        Assert.Equal(StopReason.Aborted, error.ErrorMessage.StopReason);
    }

    [Fact]
    public async Task Options_CancellationToken_Is_Honored()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("hello")),
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stream = await faux.Provider.Stream(
            faux.GetModel(),
            EmptyContext(),
            new SimpleStreamOptions { CancellationToken = cts.Token },
            CancellationToken.None);
        var events = await CollectAsync(stream);

        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Aborted, error.Reason);
    }

    [Fact]
    public async Task MidStream_Cancel_Preserves_Accumulated_Content()
    {
        var faux = new FauxProvider(new FauxProviderOptions
        {
            MinTokenSize = 1,
            MaxTokenSize = 1,
            TokensPerSecond = 1000,
        });
        var scripted = new string('x', 100);
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(scripted)),
        });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(20);
        var stream = await faux.Provider.Stream(faux.GetModel(), EmptyContext(), null, cts.Token);
        var events = await CollectAsync(stream);

        Assert.IsType<AssistantMessageEvent.Start>(events[0]);
        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Aborted, error.Reason);
        Assert.Equal("Request was aborted", error.ErrorMessage.ErrorMessage);

        // Aborted 终态消息取流上最新 Partial 快照：其首块文本 == 已推送 delta 的拼接
        // （0 个 delta 时首块为 TextStart 的空文本，断言同样成立）。
        var deltas = events.OfType<AssistantMessageEvent.TextDelta>().Select(e => e.Delta).ToList();
        var accumulated = string.Concat(deltas);
        var text = Assert.IsType<TextContent>(error.ErrorMessage.Content[0]).Text;
        Assert.Equal(accumulated, text);
    }

    [Fact]
    public async Task Text_Chunks_Reassemble_To_Scripted_Text()
    {
        var faux = new FauxProvider(new FauxProviderOptions { MinTokenSize = 3, MaxTokenSize = 3 });
        const string scripted = "abcdefghij";
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(scripted)),
        });

        var stream = await faux.Provider.Stream(faux.GetModel(), EmptyContext(), null, CancellationToken.None);
        var events = await CollectAsync(stream);

        var concatenated = string.Concat(
            events.OfType<AssistantMessageEvent.TextDelta>().Select(e => e.Delta));
        Assert.Equal(scripted, concatenated);

        var done = Assert.IsType<AssistantMessageEvent.Done>(events[^1]);
        Assert.Equal(scripted, Assert.IsType<TextContent>(done.Message.Content[0]).Text);
    }

    [Fact]
    public async Task TextDelta_Partials_Accumulate()
    {
        var faux = new FauxProvider(new FauxProviderOptions { MinTokenSize = 1, MaxTokenSize = 1 });
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("abc")),
        });

        var stream = await faux.Provider.Stream(faux.GetModel(), EmptyContext(), null, CancellationToken.None);
        var events = await CollectAsync(stream);

        var deltas = events.OfType<AssistantMessageEvent.TextDelta>().ToList();
        Assert.Equal(3, deltas.Count);

        var accumulated = string.Empty;
        foreach (var delta in deltas)
        {
            accumulated += delta.Delta;
            var partialText = Assert.IsType<TextContent>(delta.Partial.Content[^1]).Text;
            Assert.Equal(accumulated, partialText);
        }
    }

    [Fact]
    public async Task Two_Instances_With_Same_Script_Produce_Identical_Events()
    {
        static FauxResponseStep[] Script() => new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage(new ContentBlock[]
            {
                Faux.Text("hello"),
                Faux.ToolCall("query_hp", new JsonObject { ["target"] = "player" }),
            }, StopReason.ToolUse)),
        };

        static async Task<List<AssistantMessageEvent>> RunAsync(FauxProvider faux)
        {
            faux.SetResponses(Script());
            var stream = await faux.Provider.Stream(
                faux.GetModel(),
                new TranscriptContext { Messages = Array.Empty<Message>() },
                null,
                CancellationToken.None);
            return await CollectAsync(stream);
        }

        var options = new FauxProviderOptions { MinTokenSize = 2, MaxTokenSize = 2 };
        var firstEvents = await RunAsync(new FauxProvider(options));
        var secondEvents = await RunAsync(new FauxProvider(options));

        Assert.Equal(
            firstEvents.Select(e => e.Type),
            secondEvents.Select(e => e.Type));
        Assert.Equal(
            firstEvents.OfType<AssistantMessageEvent.TextDelta>().Select(e => e.Delta),
            secondEvents.OfType<AssistantMessageEvent.TextDelta>().Select(e => e.Delta));
        Assert.Equal(
            firstEvents.OfType<AssistantMessageEvent.ToolCallDelta>().Select(e => e.Delta),
            secondEvents.OfType<AssistantMessageEvent.ToolCallDelta>().Select(e => e.Delta));

        // 无 id 的 ToolCall 由各自 provider 的单调计数器补齐，实例间结果一致。
        var firstCall = firstEvents.OfType<AssistantMessageEvent.ToolCallEnd>().Single().ToolCall;
        var secondCall = secondEvents.OfType<AssistantMessageEvent.ToolCallEnd>().Single().ToolCall;
        Assert.Equal(firstCall.Id, secondCall.Id);
        Assert.Equal("tool:1", firstCall.Id);
        Assert.Equal("query_hp", firstCall.Name);
    }

    [Fact]
    public async Task Usage_Estimate_Ceils_Text_Lengths_Over_Four()
    {
        var faux = new FauxProvider();
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("abcdefghi")),
        });

        var context = new TranscriptContext
        {
            Messages = new Message[]
            {
                new SystemMessage { Timestamp = 1, Content = MessageContent.From("123") },
                new UserMessage { Timestamp = 2, Content = MessageContent.From("12") },
            },
        };
        var stream = await faux.Provider.Stream(faux.GetModel(), context, null, CancellationToken.None);
        var done = await stream.WaitForDoneAsync();

        // 输入 3+2=5 字符 → ceil(5/4)=2；输出 9 字符 → ceil(9/4)=3。
        Assert.Equal(2, done.Usage.Input);
        Assert.Equal(3, done.Usage.Output);
        Assert.Equal(5, done.Usage.TotalTokens);
        Assert.Equal(0, done.Usage.Cost.Input);
        Assert.Equal(0, done.Usage.Cost.Output);
        Assert.Equal(0, done.Usage.Cost.CacheRead);
        Assert.Equal(0, done.Usage.Cost.CacheWrite);
        Assert.Equal(0, done.Usage.Cost.Total);
    }

    [Fact]
    public async Task Scripted_Aborted_Message_Emits_Error_After_Content()
    {
        var faux = new FauxProvider(new FauxProviderOptions { MinTokenSize = 3, MaxTokenSize = 3 });
        faux.SetResponses(new[]
        {
            new FauxResponseStep.Response(Faux.AssistantMessage("hello", StopReason.Aborted)),
        });

        var stream = await faux.Provider.Stream(faux.GetModel(), EmptyContext(), null, CancellationToken.None);
        var events = await CollectAsync(stream);

        // 脚本消息先照常重放内容，再以 Error(Aborted) 收场。
        Assert.Contains(events, e => e is AssistantMessageEvent.TextDelta);
        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Aborted, error.Reason);
        Assert.Equal(StopReason.Aborted, error.ErrorMessage.StopReason);
        Assert.Equal("hello", Assert.IsType<TextContent>(error.ErrorMessage.Content[0]).Text);
    }
}
