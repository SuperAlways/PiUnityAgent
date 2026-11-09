using PiUnityAgent.Ai.Types;

// TODO(后续扩展, D22): proxy.ts 服务端代理流函数未移植（server RPC 路线）。

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// 默认流式函数的注册点。对应 TS <c>stream-fn.ts</c>：宿主可安装默认模型运行时的
/// stream 函数，而无需让 Core 依赖具体 provider 目录。
/// </summary>
public static class DefaultStreamFn
{
    private static StreamFn? _default;

    /// <summary>设置（或清除）默认流式函数。</summary>
    public static void Set(StreamFn? streamFn) => _default = streamFn;

    /// <summary>取默认流式函数；未配置时抛出（要求显式传入 streamFn）。文案逐字同 pi stream-fn.ts。</summary>
    public static StreamFn Get()
        => _default
           ?? throw new InvalidOperationException(
               "No default stream function configured. Pass streamFn explicitly or call setDefaultStreamFn().");

    /// <summary>是否已配置默认流式函数。</summary>
    public static bool IsConfigured => _default is not null;
}
