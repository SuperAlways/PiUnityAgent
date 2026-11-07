using System;
using System.Threading;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Providers.Auth;

public sealed record ProviderAuth
{
    public ApiKeyAuth? ApiKey { get; init; }
    public OAuthAuth? OAuth { get; init; }
}

public sealed record ApiKeyAuth
{
    public string? Name { get; init; }
    public Func<AuthContext, AuthOperationOptions?, CancellationToken, Task<string?>>? Resolve { get; init; }
}

public sealed record OAuthAuth
{
    public string? Name { get; init; }
    public bool IsSubscription { get; init; }
    public string? LoginLabel { get; init; }

    public Func<LoginOptions, AuthInteraction, CancellationToken, Task<Credential>>? Login { get; init; }
    public Func<Credential, CancellationToken, Task<Credential>>? Refresh { get; init; }
    public Func<string, CancellationToken, Task>? Logout { get; init; }
}
