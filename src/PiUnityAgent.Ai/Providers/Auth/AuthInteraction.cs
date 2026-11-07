using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Providers.Auth;

public sealed record AuthPromptOption(string Id, string Label, string? Description = null);

public abstract record AuthPrompt
{
    public string Message { get; init; } = string.Empty;
    public string? Placeholder { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public sealed record Text : AuthPrompt
    {
    }

    public sealed record Secret : AuthPrompt
    {
    }

    public sealed record Select : AuthPrompt
    {
        public required IReadOnlyList<AuthPromptOption> Options { get; init; }
    }

    public sealed record ManualCode : AuthPrompt
    {
    }
}

public abstract record AuthEvent
{
    public sealed record Info(string Message, IReadOnlyList<AuthInfoLink>? Links = null) : AuthEvent
    {
    }

    public sealed record AuthUrl(string Url, string? Instructions = null) : AuthEvent
    {
    }

    public sealed record DeviceCode(string UserCode, string VerificationUri) : AuthEvent
    {
        public bool OpenBrowserOnConfirm { get; init; } = true;
        public bool CopyCodeOnConfirm { get; init; } = true;
        public int? IntervalSeconds { get; init; }
        public int? ExpiresInSeconds { get; init; }
    }

    public sealed record Progress(string Message) : AuthEvent
    {
    }
}

public interface AuthInteraction
{
    Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default);
    void Notify(AuthEvent @event);
}

public sealed class ProviderAuthInteraction
{
    public ProviderAuthInteraction(AuthInteraction interaction, CancellationToken cancellationToken)
    {
        Interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        CancellationToken = cancellationToken;
    }

    public AuthInteraction Interaction { get; }
    public CancellationToken CancellationToken { get; }

    public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
        => Interaction.PromptAsync(prompt, cancellationToken.CanBeCanceled
            ? cancellationToken
            : CancellationToken);

    public void Notify(AuthEvent @event) => Interaction.Notify(@event);
}
