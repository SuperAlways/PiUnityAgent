using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Providers;

internal static class ProviderTestSupport
{
    public static Model CreateModel(string id = "m1", string provider = "p1")
        => new()
        {
            Id = id,
            Name = id,
            Api = "faux",
            Provider = provider,
            BaseUrl = "http://localhost:0",
        };

    public static IAssistantMessageEventStream DoneStream(
        Model model,
        TranscriptContext context,
        StreamOptions? options)
    {
        var stream = new AssistantMessageEventStream();
        var message = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = StopReason.Stop,
            Timestamp = 1,
        };
        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
        stream.End(message);
        return stream;
    }

    public static StaticProvider CreateProvider(
        string id = "p1",
        StreamFn? stream = null,
        ProviderAuth? auth = null,
        IReadOnlyList<Model>? models = null,
        string? baseUrl = null,
        IReadOnlyDictionary<string, string?>? headers = null)
        => new(
            id,
            id,
            models ?? new[] { CreateModel("m1", id) },
            stream ?? DoneStream,
            baseUrl ?? "http://localhost:0",
            headers,
            auth);

    public static async Task<List<AssistantMessageEvent>> CollectAsync(
        IAssistantMessageEventStream stream,
        CancellationToken cancellationToken = default)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream.WithCancellation(cancellationToken))
        {
            events.Add(@event);
        }
        return events;
    }
}
