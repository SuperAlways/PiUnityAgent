using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Providers;

namespace PiUnityAgent.Ai.Providers.Auth;

/// <summary>
/// D7 文件凭据存储：单个 JSON 文件持久化全部 provider 凭据。
/// 文件形状固定：{"version":1,"credentials":{"&lt;providerId&gt;":{...}}}，
/// api_key 条目 <c>{"type":"api_key","key":...,"env":{...}}</c>，
/// oauth 条目 <c>{"type":"oauth","refresh":...,"access":...,"expires":...,"&lt;额外字段&gt;":...}</c>。
/// 额外字段与 pi 一样平铺在条目顶层；旧版嵌套的 <c>"extra"</c> 对象读入时兼容合并。
/// 内存语义与 <see cref="InMemoryCredentialStore"/> 完全一致（per-provider 串行 / null=不变），
/// 叠加：构造时全量载入、修改成功后原子写回（temp + File.Replace/File.Move）。
/// </summary>
public sealed class FileCredentialStore : CredentialStore
{
    private const int FileVersion = 1;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly object _ioGate = new();
    private readonly Dictionary<string, Credential> _credentials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public FileCredentialStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Credential store path is required.", nameof(path));

        _path = Path.GetFullPath(ExpandHome(path));
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        Load();
    }

    public Task<Credential?> ReadAsync(string providerId, AuthOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        lock (_gate)
        {
            return Task.FromResult<Credential?>(
                _credentials.TryGetValue(providerId, out var credential) ? credential : null);
        }
    }

    public Task<IReadOnlyList<CredentialInfo>> ListAsync(AuthOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<CredentialInfo>>(
                MapInfos().ToList());
        }
    }

    public async Task<Credential?> ModifyAsync(
        string providerId,
        Func<Credential?, Task<Credential?>> modify,
        AuthOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        if (modify is null) throw new ArgumentNullException(nameof(modify));

        var gate = LockFor(providerId);
        await gate.WaitAsync(options?.CancellationToken ?? default).ConfigureAwait(false);
        try
        {
            ThrowIfCancelled(options);
            Credential? current;
            lock (_gate) _credentials.TryGetValue(providerId, out current);

            // 回调在 _gate/_ioGate 锁外 await；per-provider 串行由 gate 保证。
            var next = await modify(current).ConfigureAwait(false);
            ThrowIfCancelled(options);
            if (next is null)
            {
                return current;
            }

            lock (_gate) _credentials[providerId] = next;
            Persist();
            return next;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string providerId, AuthOperationOptions? options = null)
    {
        ThrowIfCancelled(options);
        var gate = LockFor(providerId);
        await gate.WaitAsync(options?.CancellationToken ?? default).ConfigureAwait(false);
        try
        {
            ThrowIfCancelled(options);
            bool removed;
            lock (_gate) removed = _credentials.Remove(providerId);
            if (removed) Persist();
        }
        finally
        {
            gate.Release();
        }
    }

    private IEnumerable<CredentialInfo> MapInfos()
    {
        foreach (var entry in _credentials)
        {
            yield return new CredentialInfo(
                entry.Key,
                entry.Value is OAuthCredential ? AuthType.OAuth : AuthType.ApiKey);
        }
    }

    private SemaphoreSlim LockFor(string providerId)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(providerId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _locks[providerId] = gate;
            }
            return gate;
        }
    }

    // ---- 启动载入：非法 JSON/结构一律 ModelsError(Auth)，绝不吞错降级为空库 ----

    private void Load()
    {
        if (!File.Exists(_path)) return;

        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception cause)
        {
            throw new ModelsError(ModelsErrorCode.Auth, $"Credential store is unreadable: {_path}", cause);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException cause)
        {
            throw new ModelsError(ModelsErrorCode.Auth, $"Credential store is not valid JSON: {_path}", cause);
        }

        if (root is not JsonObject rootObject)
            throw new ModelsError(ModelsErrorCode.Auth, $"Credential store root must be a JSON object: {_path}");

        if (rootObject.TryGetPropertyValue("version", out var versionNode) && versionNode is not null)
        {
            if (versionNode is not JsonValue versionValue
                || !versionValue.TryGetValue<int>(out var version)
                || version != FileVersion)
            {
                throw new ModelsError(ModelsErrorCode.Auth, $"Credential store version is unsupported: {_path}");
            }
        }

        _credentials.Clear();
        if (!rootObject.TryGetPropertyValue("credentials", out var credentialsNode) || credentialsNode is null)
            return;
        if (credentialsNode is not JsonObject credentials)
            throw new ModelsError(ModelsErrorCode.Auth, $"Credential store 'credentials' must be an object: {_path}");

        foreach (var entryPair in credentials)
        {
            if (entryPair.Value is not JsonObject entry)
                throw new ModelsError(ModelsErrorCode.Auth, $"Credential entry '{entryPair.Key}' must be an object: {_path}");
            _credentials[entryPair.Key] = ParseEntry(entryPair.Key, entry);
        }
    }

    private static Credential ParseEntry(string providerId, JsonObject entry)
    {
        if (entry["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type))
            throw new ModelsError(
                ModelsErrorCode.Auth, $"Credential entry '{providerId}' is missing a string 'type'");

        switch (type)
        {
            case "api_key":
            {
                string? key = null;
                if (entry.TryGetPropertyValue("key", out var keyNode) && keyNode is not null)
                {
                    if (keyNode is not JsonValue keyValue || !keyValue.TryGetValue<string>(out key))
                        throw new ModelsError(
                            ModelsErrorCode.Auth, $"Credential entry '{providerId}' has a non-string 'key'");
                }

                var env = ReadEnv(providerId, entry);
                return new ApiKeyCredential(key) { Env = env };
            }
            case "oauth":
            {
                var refresh = RequireString(providerId, entry, "refresh");
                var access = RequireString(providerId, entry, "access");
                if (entry["expires"] is not JsonValue expiresValue || !expiresValue.TryGetValue<long>(out var expires))
                    throw new ModelsError(
                        ModelsErrorCode.Auth, $"Credential entry '{providerId}' is missing a numeric 'expires'");

                var extra = ReadExtra(providerId, entry);
                return new OAuthCredential
                {
                    RefreshToken = refresh,
                    AccessToken = access,
                    ExpiresAt = expires,
                    Extra = extra,
                };
            }
            default:
                throw new ModelsError(
                    ModelsErrorCode.Auth, $"Credential entry '{providerId}' has unknown type '{type}'");
        }
    }

    private static string RequireString(string providerId, JsonObject entry, string field)
    {
        if (entry[field] is not JsonValue value || !value.TryGetValue<string>(out var text))
            throw new ModelsError(
                ModelsErrorCode.Auth, $"Credential entry '{providerId}' is missing a string '{field}'");
        return text;
    }

    private static IReadOnlyDictionary<string, string>? ReadEnv(string providerId, JsonObject entry)
    {
        if (!entry.TryGetPropertyValue("env", out var envNode) || envNode is null) return null;
        if (envNode is not JsonObject envObject)
            throw new ModelsError(ModelsErrorCode.Auth, $"Credential entry '{providerId}' has a non-object 'env'");

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in envObject)
        {
            if (pair.Value is not JsonValue value || !value.TryGetValue<string>(out var text))
                throw new ModelsError(
                    ModelsErrorCode.Auth, $"Credential entry '{providerId}' has a non-string env value '{pair.Key}'");
            env[pair.Key] = text;
        }
        return env;
    }

    private static IReadOnlyDictionary<string, JsonNode?>? ReadExtra(string providerId, JsonObject entry)
    {
        var extra = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        // 旧版把未知字段嵌在 "extra"：读入时并进平铺结果，写出时不再产生该键。
        if (entry.TryGetPropertyValue("extra", out var legacyNode) && legacyNode is not null)
        {
            if (legacyNode is not JsonObject legacyObject)
                throw new ModelsError(
                    ModelsErrorCode.Auth, $"Credential entry '{providerId}' has a non-object 'extra'");
            foreach (var pair in legacyObject) extra[pair.Key] = pair.Value?.DeepClone();
        }

        foreach (var pair in entry)
        {
            if (pair.Key == "extra" || IsOAuthReservedKey(pair.Key)) continue;
            extra[pair.Key] = pair.Value?.DeepClone();
        }

        return extra.Count == 0 ? null : extra;
    }

    private static bool IsOAuthReservedKey(string key)
        => key is "type" or "refresh" or "access" or "expires";

    // ---- 原子写回：temp 文件 + File.Replace/File.Move；失败清理 temp 并抛 ModelsError(Auth) ----

    private void Persist()
    {
        // _ioGate 串行化整个"快照→序列化→落盘"，保证并发 Modify 不同 provider 时最后一次写包含全部已提交变更。
        lock (_ioGate)
        {
            var root = BuildRoot();
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            var tempPath = _path + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(_path)) File.Replace(tempPath, _path, destinationBackupFileName: null);
                else File.Move(tempPath, _path);
            }
            catch (Exception cause)
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // 清理失败让位于原始落盘错误。
                }

                throw new ModelsError(ModelsErrorCode.Auth, $"Credential store write failed: {_path}", cause);
            }
        }
    }

    private JsonObject BuildRoot()
    {
        lock (_gate)
        {
            var credentials = new JsonObject();
            foreach (var pair in _credentials)
            {
                credentials[pair.Key] = ToEntry(pair.Value);
            }

            return new JsonObject
            {
                ["version"] = FileVersion,
                ["credentials"] = credentials,
            };
        }
    }

    private static JsonObject ToEntry(Credential credential)
    {
        switch (credential)
        {
            case ApiKeyCredential apiKey:
            {
                var entry = new JsonObject { ["type"] = "api_key" };
                if (apiKey.Key is not null) entry["key"] = apiKey.Key;
                if (apiKey.Env is not null)
                {
                    var env = new JsonObject();
                    foreach (var pair in apiKey.Env) env[pair.Key] = pair.Value;
                    entry["env"] = env;
                }
                return entry;
            }
            case OAuthCredential oauth:
            {
                var entry = new JsonObject
                {
                    ["type"] = "oauth",
                    ["refresh"] = oauth.RefreshToken,
                    ["access"] = oauth.AccessToken,
                    ["expires"] = oauth.ExpiresAt,
                };
                if (oauth.Extra is not null)
                {
                    foreach (var pair in oauth.Extra)
                    {
                        if (pair.Key == "extra" || IsOAuthReservedKey(pair.Key)) continue;
                        entry[pair.Key] = pair.Value?.DeepClone();
                    }
                }
                return entry;
            }
            default:
                throw new ModelsError(
                    ModelsErrorCode.Auth,
                    $"Unsupported credential type for persistence: {credential.GetType().Name}");
        }
    }

    private static string ExpandHome(string path)
    {
        if (path == "~") return UserProfile();
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(UserProfile(), path.Substring(2));
        }
        return path;
    }

    private static string UserProfile() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static void ThrowIfCancelled(AuthOperationOptions? options)
        => options?.CancellationToken.ThrowIfCancellationRequested();
}
