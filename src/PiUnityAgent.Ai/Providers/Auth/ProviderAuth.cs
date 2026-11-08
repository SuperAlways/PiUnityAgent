using System;
using System.Threading;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Providers.Auth;

public sealed record ProviderAuth
{
    public ApiKeyAuth? ApiKey { get; init; }
    public OAuthAuth? OAuth { get; init; }
}

public sealed record ApiKeyResolveInput(IAuthContext Context, ApiKeyCredential? Credential);
public sealed record ApiKeyCheckInput(IAuthContext Context, ApiKeyCredential? Credential);

public sealed record ApiKeyAuth
{
    public required string Name { get; init; }
    public Func<ProviderAuthInteraction, CancellationToken, Task<ApiKeyCredential>>? Login { get; init; }
    public Func<ApiKeyCheckInput, CancellationToken, Task<AuthCheck?>>? Check { get; init; }
    public required Func<ApiKeyResolveInput, CancellationToken, Task<AuthResult?>> Resolve { get; init; }
}

public sealed record OAuthAuth
{
    public required string Name { get; init; }
    public bool IsSubscription { get; init; }
    public string? LoginLabel { get; init; }
    public required Func<ProviderAuthInteraction, LoginOptions?, CancellationToken, Task<OAuthCredential>> Login { get; init; }
    public required Func<OAuthCredential, CancellationToken, Task<OAuthCredential>> Refresh { get; init; }
    public required Func<OAuthCredential, CancellationToken, Task<ModelAuth>> ToAuth { get; init; }
}
