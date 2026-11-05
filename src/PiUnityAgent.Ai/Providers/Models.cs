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
    private readonly object _gate = new();
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);

    public IReadOnlyList<Provider> Providers
    {
        get { lock (_gate) return _providers.Values.ToList(); }
    }

    public void SetProvider(Provider provider)
    {
        lock (_gate) _providers[provider.Id] = provider;
    }

    public bool DeleteProvider(string id)
    {
        lock (_gate) return _providers.Remove(id);
    }

    public void ClearProviders()
    {
        lock (_gate) _providers.Clear();
    }

    public Provider? GetProvider(string id)
    {
        lock (_gate) return _providers.TryGetValue(id, out var provider) ? provider : null;
    }

    public IReadOnlyList<Model> GetModels(string? provider = null)
    {
        lock (_gate)
        {
            if (provider is not null)
            {
                return _providers.TryGetValue(provider, out var found)
                    ? found.Models.ToList()
                    : Array.Empty<Model>();
            }

            return _providers.Values.SelectMany(p => p.Models).ToList();
        }
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
        catch (OperationCanceledException)
        {
            // 取消 ≠ 失败（S2 契约）：OCE 编码为 Aborted 终态事件，不得落入通用 catch。
            return ErrorStream(model, "Request was aborted", StopReason.Aborted);
        }
        catch (Exception error)
        {
            return ErrorStream(model, error.Message);
        }
    }

    private static IAssistantMessageEventStream ErrorStream(
        Model model,
        string message,
        StopReason reason = StopReason.Error)
    {
        var stream = new AssistantMessageEventStream();
        var assistant = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = reason,
            ErrorMessage = message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        stream.Push(new AssistantMessageEvent.Error(reason, assistant));
        stream.End(assistant);
        return stream;
    }
}
