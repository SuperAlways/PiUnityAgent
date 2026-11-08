using System;
using System.Collections.Generic;
using System.Linq;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

/// <summary>
/// pi CreateModelsOptions：Models 门面的构造选项；全部可空，缺省即内存实现（D；等价于 new Models(null)）。
/// </summary>
public sealed record CreateModelsOptions
{
    public CredentialStore? Credentials { get; init; }
    public IModelsStore? ModelsStore { get; init; }
    public IAuthContext? AuthContext { get; init; }
    public ClientIdentity? Identity { get; init; }
}

public sealed partial class Models
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IProvider> _providers = new(StringComparer.Ordinal);
    private readonly CredentialStore _credentials;
    private readonly IAuthContext _authContext;
    private readonly IModelsStore _modelsStore;
    private readonly ClientIdentity? _identity;

    public Models(CreateModelsOptions? options = null)
    {
        _credentials = options?.Credentials ?? new InMemoryCredentialStore();
        _modelsStore = options?.ModelsStore ?? new InMemoryModelsStore();
        _authContext = options?.AuthContext ?? new DefaultAuthContext();
        _identity = options?.Identity;
    }

    /// <summary>pi Models.identity：门面级客户端身份；请求级 identity 逐字段覆盖它。</summary>
    public ClientIdentity? Identity => _identity;

    public void SetProvider(IProvider provider)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
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

    public IReadOnlyList<IProvider> GetProviders()
    {
        lock (_gate) return _providers.Values.ToList();
    }

    public IProvider? GetProvider(string id)
    {
        lock (_gate) return _providers.TryGetValue(id, out var provider) ? provider : null;
    }

    public IReadOnlyList<Model> GetModels(string? provider = null)
    {
        if (provider is not null)
        {
            var found = GetProvider(provider);
            return found is null ? Array.Empty<Model>() : SafeChatModels(found);
        }

        return GetProviders().SelectMany(SafeChatModels).ToList();
    }

    public Model? GetModel(string provider, string id)
    {
        var found = GetProvider(provider);
        return found is null
            ? null
            : SafeChatModels(found).FirstOrDefault(model => model.Id == id);
    }

    public IReadOnlyList<ProviderModel> GetModelsOfType(ModelType type, string? provider = null)
        => GetAllModels(provider).Where(model => model.Type == type).ToList();

    public ProviderModel? GetModelOfType(ModelType type, string provider, string id)
        => GetAllModels(provider).FirstOrDefault(model => model.Type == type && model.Id == id);

    public IReadOnlyList<ProviderModel> GetAllModels(string? provider = null)
    {
        if (provider is not null)
        {
            var found = GetProvider(provider);
            return found is null ? Array.Empty<ProviderModel>() : SafeAllModels(found);
        }

        return GetProviders().SelectMany(SafeAllModels).ToList();
    }

    private static IReadOnlyList<Model> SafeChatModels(IProvider provider)
    {
        try { return provider.GetModels().ToList(); }
        catch { return Array.Empty<Model>(); }
    }

    private static IReadOnlyList<ProviderModel> SafeAllModels(IProvider provider)
    {
        try { return provider.GetAllModels().ToList(); }
        catch { return Array.Empty<ProviderModel>(); }
    }
}
