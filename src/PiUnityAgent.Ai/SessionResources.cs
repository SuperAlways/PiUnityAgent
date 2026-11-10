using System;
using System.Collections.Generic;

namespace PiUnityAgent.Ai;

/// <summary>pi session-resources.ts：会话资源清理回调；sessionId 可空（全局清理）。</summary>
public delegate void SessionResourceCleanup(string? sessionId);

/// <summary>
/// pi session-resources.ts：进程级会话资源清理注册表。
/// RegisterSessionResourceCleanup 注册回调并返回可注销句柄（pi 返回注销函数）；
/// CleanupSessionResources 遍历全部回调逐个调用，任一异常聚合成 AggregateException 抛出。
/// </summary>
public static class SessionResources
{
    private static readonly object Gate = new();
    private static readonly HashSet<SessionResourceCleanup> Cleanups = new();

    /// <summary>注册清理回调；Dispose 返回的句柄即注销（pi 的注销闭包）。</summary>
    public static IDisposable RegisterSessionResourceCleanup(SessionResourceCleanup cleanup)
    {
        if (cleanup is null)
        {
            throw new ArgumentNullException(nameof(cleanup));
        }
        lock (Gate)
        {
            Cleanups.Add(cleanup);
        }
        return new Unregister(cleanup);
    }

    /// <summary>
    /// 清理全部会话资源：快照遍历（回调里再注册/注销不影响本轮），
    /// 单个回调的异常不中断其他回调，最后聚合抛出——与 pi AggregateError 行为一致。
    /// </summary>
    public static void CleanupSessionResources(string? sessionId = null)
    {
        SessionResourceCleanup[] snapshot;
        lock (Gate)
        {
            var cleanups = new SessionResourceCleanup[Cleanups.Count];
            Cleanups.CopyTo(cleanups);
            snapshot = cleanups;
        }

        var errors = new List<Exception>();
        foreach (var cleanup in snapshot)
        {
            try
            {
                cleanup(sessionId);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        if (errors.Count > 0)
        {
            throw new AggregateException("Failed to cleanup session resources", errors);
        }
    }

    /// <summary>注销句柄：Dispose 后该回调不再被后续清理调用。</summary>
    private sealed class Unregister : IDisposable
    {
        private readonly SessionResourceCleanup _cleanup;

        public Unregister(SessionResourceCleanup cleanup)
        {
            _cleanup = cleanup;
        }

        public void Dispose()
        {
            lock (Gate)
            {
                Cleanups.Remove(_cleanup);
            }
        }
    }
}
