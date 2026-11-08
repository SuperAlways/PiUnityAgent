using System.Collections.Generic;

namespace PiUnityAgent.Ai.Types;

/// <summary>pi UserAgentMode：Detailed / Short / Custom(value) 三态。</summary>
public sealed record UserAgentMode
{
    private UserAgentMode() { }
    public static UserAgentMode Detailed { get; } = new();
    public static UserAgentMode Short { get; } = new() { IsShort = true };
    public static UserAgentMode Custom(string value) => new() { CustomValue = value };
    internal bool IsShort { get; private init; }
    internal string? CustomValue { get; private init; }
}

/// <summary>pi client.app 引用：名称 + URL + 可选类别。</summary>
public sealed record ClientAttribution(
    string Name,
    string Url,
    IReadOnlyList<string>? Categories = null);

/// <summary>
/// pi ClientIdentity：客户端身份（user-agent 模式 / client.app 归属 / 版本）。
/// Attribution 用 AttributionSet 哨兵区分"未提供"与"显式置 null（关闭归属）"。
/// </summary>
public sealed record ClientIdentity
{
    public UserAgentMode? UserAgent { get; init; }

    public ClientAttribution? Attribution
    {
        get => _attribution;
        init
        {
            _attribution = value;
            AttributionSet = true;
        }
    }

    public bool AttributionSet { get; private init; }
    public string? Version { get; init; }
    private readonly ClientAttribution? _attribution;
}

/// <summary>pi mergeClientIdentity：逐字段合并（Attribution 看 AttributionSet 哨兵，不是 `??`）。</summary>
public static class ClientIdentityMerger
{
    public static ClientIdentity? Merge(params ClientIdentity?[] sources)
    {
        ClientIdentity? merged = null;
        foreach (var source in sources)
        {
            if (source is null) continue;
            merged ??= new ClientIdentity();
            if (source.UserAgent is not null) merged = merged with { UserAgent = source.UserAgent };
            if (source.AttributionSet) merged = merged with { Attribution = source.Attribution };
            if (source.Version is not null) merged = merged with { Version = source.Version };
        }
        return merged;
    }
}
