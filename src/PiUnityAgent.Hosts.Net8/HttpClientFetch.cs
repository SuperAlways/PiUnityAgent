using PiUnityAgent.Ai.Types;
using UnityLlmTransport;

namespace PiUnityAgent.Hosts.Net8;

/// <summary>Hosts.Net8 默认 HTTP 实现；Unity 实机由 Hosts.Unity 的 UWR 适配器替换。</summary>
public static class HttpClientFetch
{
    public static FetchFunction Create(HttpClientTransportOptions? options = null)
        => new TransportFetch(new HttpClientTransport(options ?? new HttpClientTransportOptions())).Fetch;
}
