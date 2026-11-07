using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiUnityAgent.Ai.Providers.Auth;

[JsonConverter(typeof(AuthTypeJsonConverter))]
public enum AuthType
{
    ApiKey,
    OAuth
}

public sealed record ModelAuth
{
    public string? ApiKey { get; init; }
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    public string? BaseUrl { get; init; }
}

public sealed record AuthResult(Credential Credential, string? Source = null);

public sealed record AuthCheck(bool Configured, AuthType? Type = null);

public sealed record AuthOperationOptions
{
}

public sealed record AuthResolutionOverrides
{
    public string? ApiKey { get; init; }
    public Credential? Credential { get; init; }
}

public sealed record AuthContext
{
    public IReadOnlyDictionary<string, string>? Env { get; init; }
}

public sealed record AuthInfoLink(string Url, string? Label = null);

public sealed record LoginOptions
{
    public Func<string>? GetDeviceId { get; init; }
    public string? AgentName { get; init; }
}

public sealed class AuthTypeJsonConverter : JsonConverter<AuthType>
{
    public override AuthType Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "api_key" => AuthType.ApiKey,
            "oauth" => AuthType.OAuth,
            var value => throw new JsonException($"Unknown auth type: {value}")
        };

    public override void Write(
        Utf8JsonWriter writer,
        AuthType value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            AuthType.ApiKey => "api_key",
            AuthType.OAuth => "oauth",
            _ => throw new JsonException($"Unknown auth type: {value}")
        });
}
