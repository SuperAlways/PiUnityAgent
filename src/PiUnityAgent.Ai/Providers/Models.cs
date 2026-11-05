using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public interface IModels
{
    IReadOnlyList<Provider> Providers { get; }
    Provider? GetProvider(string id);
    IReadOnlyList<Model> GetModels(string? provider = null);
    Model? GetModel(string provider, string id);
    Task<IAssistantMessageEventStream> StreamSimpleAsync(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken);
}

public sealed class Models : IModels
{
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);

    public IReadOnlyList<Provider> Providers => _providers.Values.ToList();

    public void SetProvider(Provider provider) => _providers[provider.Id] = provider;

    public bool DeleteProvider(string id) => _providers.Remove(id);

    public void ClearProviders() => _providers.Clear();

    public Provider? GetProvider(string id)
        => _providers.TryGetValue(id, out var provider) ? provider : null;

    public IReadOnlyList<Model> GetModels(string? provider = null)
    {
        if (provider is not null)
            return GetProvider(provider)?.Models ?? Array.Empty<Model>();
        return _providers.Values.SelectMany(p => p.Models).ToList();
    }

    public Model? GetModel(string provider, string id)
        => GetProvider(provider)?.Models.FirstOrDefault(m => m.Id == id);

    public async Task<IAssistantMessageEventStream> StreamSimpleAsync(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken)
    {
        var provider = GetProvider(model.Provider);
        if (provider is null)
            return ErrorStream(model, $"Unknown provider: {model.Provider}");

        try
        {
            return await provider.Stream(model, context, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return ErrorStream(model, error.Message);
        }
    }

    private static IAssistantMessageEventStream ErrorStream(Model model, string message)
    {
        var stream = new AssistantMessageEventStream();
        var assistant = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = StopReason.Error,
            ErrorMessage = message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        stream.Push(new AssistantMessageEvent.Error(StopReason.Error, assistant));
        stream.End(assistant);
        return stream;
    }
}
