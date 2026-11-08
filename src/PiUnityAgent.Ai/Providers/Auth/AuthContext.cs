using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PiUnityAgent.Ai.Providers.Auth;

public interface IAuthContext
{
    Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default);
    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class DefaultAuthContext : IAuthContext
{
    public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(Environment.GetEnvironmentVariable(name));
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult(false);
        var expanded = path.StartsWith("~", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[1..].TrimStart('/', '\\'))
            : path;
        return Task.FromResult(File.Exists(expanded));
    }
}
