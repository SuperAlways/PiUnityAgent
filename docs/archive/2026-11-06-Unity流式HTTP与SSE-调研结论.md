# 调研结论：Unity 流式 HTTP / SSE 开源方案

> **历史归档（2026-11-07）**：本文是通讯组件立项前的调研结论，已落地为独立仓库 `UnityLlmTransport`（Phase 0/1/2 完成）。当前事实以 `UnityLlmTransport/docs/` 为准；本文仅作决策历史保留。

> 对应问题文档：[2026-11-06-Unity流式HTTP与SSE-调研问题.md](./2026-11-06-Unity流式HTTP与SSE-调研问题.md)
> 调研日期：2026-11-06
> 结论口径：面向 PiUnityAgent（C# `netstandard2.1`，Unity 2021.2+ 锁定，Mono/IL2CPP 为主，WebGL 非 M1）

---

## 一、结论速览

**没有一个开源库能一次性满足全部约束**（Unity 2021 + 真流式 + 可取消 + 分层超时 + 代理/TLS + WebGL）。现有项目可以拆成三类“零件”：

| 零件 | 首选 | 说明 |
|---|---|---|
| 纯 SSE 解析 | `System.Net.ServerSentEvents`（MIT） | 与传输无关，官方按 WHATWG 实现，带 `netstandard2.0` 目标 |
| Unity 传输 | 自研 `UnityWebRequest + DownloadHandlerScript` 薄适配 | 无现成开源库可用；mikito 示例仅作思路参考（无 LICENSE） |
| 桌面/后端传输 | `HttpClient + ResponseHeadersRead`，可参考 `LaunchDarkly.EventSource` | 显式代理/证书/HTTP2 都在这条路上 |

推荐落地形态：

```
核心层（netstandard2.1，零 UnityEngine，可单测）
  ├─ ISseParser（复用/移植 System.Net.ServerSentEvents）
  ├─ Utf8ChunkDecoder（跨 chunk 增量解码，禁止逐块 Encoding.GetString）
  ├─ TimeoutPolicy（首字节 / 空闲，两个独立看门狗）
  └─ OpenAI 事件聚合（content / reasoning / tool_calls / usage / [DONE]）

传输层（只吐原始字节，不解析、不判断）
  ├─ UwrSseTransport          Unity 主路径（Mono / IL2CPP）
  ├─ HttpClientSseTransport   桌面 / .NET 8 后端（代理、自签、HTTP2）
  └─ WebGlFetchSseTransport   M2 再做（.jslib + fetch）
```

---

## 二、候选项目逐个介绍

### 1. System.Net.ServerSentEvents（dotnet/runtime）

- **仓库**：https://github.com/dotnet/runtime/tree/main/src/libraries/System.Net.ServerSentEvents
- **许可证**：MIT（.NET Foundation）
- **形态**：`SseParser` / `SseParser<T>` / `SseItem<T>` / `SseFormatter`，纯解析，不含 HTTP 传输
- **目标框架**：csproj 里明确包含 `netstandard2.0`（另有 NetCoreApp 各版本），非 .NET Core 分支会带入 `Microsoft.Bcl.AsyncInterfaces`、`System.Memory`、`System.Threading.Tasks.Extensions`
- **SSE 覆盖**：按 WHATWG 规范处理 `\r` / `\n` / `\r\n`、多行 `data:` 拼接、注释行（`:` 开头）、`event:` / `id:` / `retry:` 字段、跨底层 chunk 的增量读取
- **优点**：解析正确性最高、无第三方依赖、API 稳定；`SseParser.Create(stream)` + 异步枚举即可接入任意 `Stream`
- **缺点**：不解决连接、取消、超时、代理；包版本号跟 .NET 9/10/11 走，但程序集本身兼容 netstandard2.0
- **对我们的用法**：**核心层首选**。两种接入方式：
  1. 引 NuGet `System.Net.ServerSentEvents`（9.x 起有 netstandard2.0 资产），但要确认 Unity 侧不会因依赖解析出问题；
  2. 直接把 `SseParser*.cs`、`SseItem.cs`、`Helpers.cs` 等 vendor 进核心库（MIT 允许），去掉不需要的 `SseFormatter`，可完全控依赖。
- **风险**：Unity 2021 的 C# 语言版本/BCL 对 `Span`、`IAsyncEnumerable` 支持需要用一次编译验证；`System.Memory` 在 Unity 里可能与内置版本冲突。

---

### 2. LaunchDarkly.EventSource（launchdarkly/dotnet-eventsource）

- **仓库**：https://github.com/launchdarkly/dotnet-eventsource
- **许可证**：Apache-2.0
- **目标框架**：`.NET Framework 4.6.2` + `.NET Standard 2.0`
- **活跃度**：非常活跃（仓库最近提交 2026-09；GitHub topic 显示 2026-09-15 更新）
- **能力**：
  - 完整 SSE 客户端：连接、解析、事件回调、`ReadyState`、`Close()`
  - **分层超时**：`ResponseStartTimeout`（发请求→收到响应头）、`ReadTimeout`（流空闲，超时断开并重连），正好对应需求 Must#3
  - 可注入 `HttpClient`（`ConfigurationBuilder.HttpClient`）→ 代理、证书、连接池可在外层 `HttpClientHandler` 配置
  - `HttpRequestModifier`（`Action<HttpRequestMessage>`）→ 自定义 header / 认证
  - 指数退避重连、`retry:` 字段、临时退避边界
- **优点**：超时语义与错误模型成熟；代码质量高；Apache-2.0 可商用
- **缺点**：
  - **基于 `HttpClient`**，Unity Mono/IL2CPP 下流式与 TLS 行为需要实机验证；WebGL 完全不可用
  - 默认“断线自动重连”语义不适合一次性 LLM 流：收到 `[DONE]` 后必须显式 `Close()`，否则空闲超时会触发重连并可能重复计费
  - 会引入一个完整 SSE 客户端（含重连/退避），与“只需解析”的核心层职责重叠
- **对我们的用法**：**桌面/后端传输的可选实现或参考实现**。如果 `HttpClientSseTransport` 自己写太费劲，可以直接包一层 `EventSource`，把 `ReadTimeout` 当空闲超时，禁止重连（`Close` on done）；但不要把它放进 Unity 核心库。

---

### 3. com.openai.unity / OpenAI-DotNet（RageAgainstThePixel）

- **仓库**：https://github.com/RageAgainstThePixel/com.openai.unity
- **底座**：https://github.com/RageAgainstThePixel/OpenAI-DotNet
- **许可证**：MIT（已核实 `LICENSE.md`，Copyright 2021 Stephen Hodgson）
- **活跃度**：活跃（约 530 stars，持续更新）
- **Unity 支持**：README 明确 “Requires Unity 2021.3 LTS or higher”；通过 OpenUPM 分发
- **能力**：OpenAI REST 全量封装（Chat / Responses / Assistants / Audio / Images / Realtime）；**流式通过回调**，例如：
  ```csharp
  async Task StreamCallback(string @event, IServerSentEvent sseEvent) { ... }
  var response = await api.ResponsesEndpoint.CreateModelResponseAsync(request, StreamCallback);
  ```
  Chat 侧提供 `StreamCompletionAsync`；文档覆盖 “Streaming Response with Function Calling”
- **依赖**：`com.utilities.async`、`com.utilities.websockets`、`com.utilities.extensions`、`com.utilities.rest`、`com.utilities.audio`、`com.utilities.encoder.wav`
  - 其中 `com.utilities.rest` 是 UnityWebRequest 封装（API 里有 `disposeDownloadHandler` / `certificateHandler` / `timeout` 参数），说明 Unity 侧的 HTTP 走 UWR 路线
- **优点**：**事件聚合与回调 API 形状可以直接抄**（`IServerSentEvent`、按 event type 分发、函数调用增量拼装）；证明 “UWR 之上做 SSE” 在产品里可行
- **缺点**：
  - 6 个 UPM 依赖，耦合重；引入会把核心库绑死在它的抽象上
  - OpenAI 专用，不是通用 OpenAI 兼容（DeepSeek/自建网关）底座
  - 流式实现细节需读源码确认（README 只展示用法）
- **对我们的用法**：**只作参考，不作依赖**。重点看三件事：
  1. 回调/事件类型如何分层（`IServerSentEvent` 的设计）
  2. tool_calls 增量如何按 index 拼装
  3. UWR 的 `Abort` 与 `Task` 取消如何桥接

---

### 4. mikito/unity-chat-gpt-sample（stream 分支）

- **仓库**：https://github.com/mikito/unity-chat-gpt-sample/tree/stream
- **许可证**：**仓库根目录没有 LICENSE 文件** → 默认保留全部权利，**不能复制代码**
- **活跃度**：2023-03 后停更（8 stars）
- **形态**：最小 Unity 工程，`UnityWebRequest + DownloadHandlerScript` 接收 SSE，逐行解析 `data:`，演示 ChatGPT 流式输出；另有 Coroutine 版与 UniTask 版分支
- **优点**：结构最简单，适合快速理解 “UWR 真流式” 的最小闭环；配套日文文章讲清了思路
- **缺点**：无授权、无测试、无错误分类、无分层超时、无取消后的连接中止保证
- **对我们的用法**：**只读思路，不复制代码**。可核对这几处：`ReceiveData` 里如何做行缓冲、如何切 `data:` 前缀、如何在主线程更新 UI。

---

### 5. SsePulse.Client（claudiom248）

- **仓库**：https://github.com/claudiom248/SsePulse.Client
- **许可证**：MIT
- **目标框架**：`net8.0`（README “Modern .NET — targets net8.0 and runs on .NET 8, 9, 10 and later”）
- **活跃度**：2026-11 仍在更新
- **能力**：fluent handler 注册、强类型 JSON 反序列化、可插拔认证、重试/重连、last-event-replay、`IServiceCollection` 集成
- **对我们的用法**：**不可用于 Unity 2021**（TFM 直接排除，且依赖 DI）。仅在设计 API 时参考它的 “按事件类型注册 handler” 形状。

---

### 6. Azure/fetch-event-source（JS）

- **仓库**：https://github.com/Azure/fetch-event-source
- **许可证**：MIT（Microsoft）
- **形态**：浏览器 `fetch` + `ReadableStream` 的 SSE 客户端，替代原生 `EventSource`
- **优点**：可自定义 method/header/body（原生 `EventSource` 不能带 Authorization）；支持 POST；可中断
- **对我们的用法**：**WebGL 分支的 JS 侧算法参考**。Unity 侧需要写 `.jslib`：JS 读流 → 每收到一段就 `SendMessage` 回 Unity；C# 侧复用同一套核心解析器。注意 Unity WebGL 无线程，`Task.Run` / `Task.Delay` / `Timer` 都会导致浏览器挂起。

---

### 7. NativeWebSocket（endel）

- **仓库**：https://github.com/endel/NativeWebSocket
- **许可证**：Apache-2.0（已核实 LICENSE 文本）
- **能力**：Unity 跨平台 WebSocket，WebGL 走浏览器 WebSocket，原生平台走 `ClientWebSocket`
- **对我们的用法**：**仅当某些厂商提供 WebSocket 传输时作为备选**；不解决 SSE。可作为“传输接口多实现”形状的参考。

---

### 8. Best Server-Sent Events（Unity Asset Store）

- **链接**：https://assetstore.unity.com/packages/tools/network/best-server-sent-events-268758
- **许可证**：付费闭源（Asset Store EULA）
- **支持**：Unity 2021.1.28+，宣称跨平台 SSE/EventSource
- **对我们的用法**：**不采用**（违反“开源/可审计”约束）。仅作商业方案对照：如果未来不想自研，这是最省事的付费路径。

---

### 9. com.kirbyrawr.launchdarkly.eventsource（非官方 Unity 编译包）

- **链接**：https://www.jsdelivr.com/package/npm/com.kirbyrawr.launchdarkly.eventsource
- **形态**：把 LaunchDarkly.EventSource 3.3.2 编译成 Unity 可用的 DLL 包
- **许可证**：上游 Apache-2.0
- **风险**：非官方维护、版本停留在 3.3.2、DLL 形式不可审计、Unity 兼容性未知
- **对我们的用法**：**不推荐**。要 LaunchDarkly 就直接用源码/NuGet，或自己移植。

---

## 三、候选对比表

| 项目 | 许可证 | 活跃 | Unity 2021 | 传输 | 真流式 | SSE 解析 | 超时分层 | 代理/TLS | AOT | 适配成本 | 结论 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| System.Net.ServerSentEvents | MIT | 高 | 可（netstandard2.0） | 无（纯解析） | 取决于喂流 | 规范级 | 无 | 无 | 纯托管 | 低 | **核心层采用** |
| LaunchDarkly.EventSource | Apache-2.0 | 高 | 需验证 HttpClient | HttpClient | 是 | 规范级 | 首字节+空闲 | 靠 HttpClient | 纯托管 | 中 | 桌面/后端可选 |
| com.openai.unity | MIT | 高 | 是（2021.3+） | UWR（经 com.utilities.rest） | 是 | 有 | 弱 | UWR 能力 | 需验证 | 高（6 依赖） | 只参考 |
| mikito stream 示例 | 无 LICENSE | 低（2023） | 是 | UWR + DownloadHandlerScript | 是 | 简版 | 无 | UWR 能力 | 需验证 | 低（但不能抄） | 只参考 |
| SsePulse.Client | MIT | 中 | 否（net8.0） | HttpClient | 是 | 有 | 有 | 靠 HttpClient | 否 | 不可用 | 排除 |
| Azure/fetch-event-source | MIT | 高 | 仅 WebGL（JS 桥） | fetch | 是 | 是 | 无 | 浏览器 | 不适用 | 中 | M2 WebGL 参考 |
| NativeWebSocket | Apache-2.0 | 中 | 是 | WebSocket | 是 | 无（非 SSE） | 无 | 浏览器/系统 | 需验证 | 中 | WS 备选 |
| Best SSE（Asset Store） | 付费 | 2024 | 是 | 未知 | 宣称是 | 宣称有 | 未知 | 未知 | 未知 | 低 | 不采用 |
| com.kirbyrawr（DLL） | Apache-2.0 | 低 | 未知 | HttpClient | 是 | 上游 | 上游 | 上游 | 未知 | 中 | 不推荐 |

---

## 四、平台事实与坑（已核对 Unity 文档）

1. **`DownloadHandlerScript.ReceiveData` 在主线程回调**
   数据先进入临时 ring buffer，每帧最多回调一次、一帧内可多次；多个 datagram 会合并。
   来源：https://docs.unity.com/en-us/engine/6000.5/script-reference/unityengine/networking/downloadhandler/receivedata
   → 解析不能重；建议 `ReceiveData` 只做 `byte[]` 入队，解析放后台线程。

2. **`UnityWebRequest.timeout` 语义不可靠**
   文档写 “if no response is received”，示例却按“整请求 60 秒”演示。
   来源：https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.UnityWebRequest-timeout.html
   → SSE 场景设 `timeout = 0`，自己实现首字节/空闲两个看门狗。

3. **代理：只有系统代理，没有显式代理 API（2021.3）**
   文档：“If the device ... uses proxy settings, UnityWebRequest applies the proxy settings after the application sends the request.”
   来源：https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.UnityWebRequest.html
   → 企业代理/指定 proxy 只能走 `HttpClient` 路径，或本地起转发。

4. **TLS：证书校验可接管**
   `certificateHandler` 可接受自签/企业根证书，默认校验系统根证书。
   来源：https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.UnityWebRequest-certificateHandler.html

5. **HTTP/2：Unity 2021 没有**
   Unity 6.3 才把 `UnityWebRequest` 默认切到 HTTP/2（Windows/macOS/Linux/Android 等）。
   来源：https://docs.unity.com/en-us/engine/6000.3/manual/whats-new/unity63
   → 2021.3 只能 HTTP/1.1 chunked；需求 Should#9 在本版本无解。

6. **IL2CPP 会剥离 `DownloadHandlerScript` 的虚方法**
   Unity 修复记录：“Prevent virtual method in DownloadHandlerScript from being stripped.”
   来源：https://unityreleases.com/issues?area=Networking&sort=days-desc&status=fixed
   → 需要 `link.xml` 或 `[Preserve]` 保住 `ReceiveData` / `CompleteContent` 重写。

7. **WebGL 是另一套世界**
   `HttpClient` / `WebClient` / `HttpWebRequest` 未实现且产生不可恢复的浏览器挂起；`SslStream`、原始 socket 不支持；`Task.Run` / `Task.Delay` / `Timer` 不工作。
   来源：https://docs.unity.com/en-us/engine/6000.3/manual/platform-specific/webgl/develop/web-dotnet-api-support
   → M1 不承诺 WebGL；M2 用 `.jslib` + fetch。

8. **连接失败时 `responseCode == 0`**
   不能只看状态码分类；必须结合 `UnityWebRequest.result`（`ConnectionError` / `ProtocolError` / `DataProcessingError`）与 `error` 字符串。

9. **gzip 待实机确认**
   Unity 不建议手动设置 `Accept-Encoding`；`DownloadHandlerScript` 收到的 `ReceiveData` 数据是否已由 libcurl 解压，需要在目标版本抓包验证（SSE 通常 identity，错误体可能 gzip）。

---

## 五、推荐架构与接口草案

### 5.1 分层

```
[胶质层 · 可引用 UnityEngine]
  UwrSseTransport : ISseTransport
    - 组装 UnityWebRequest（POST + Accept: text/event-stream）
    - DownloadHandlerScript 子类：ReceiveData -> ConcurrentQueue<byte[]>
    - timeout = 0；Abort() 实现取消
    - certificateHandler 可注入；系统代理自动生效
    - 完成后把队列封口，交给核心层

[本体层 · netstandard2.1，零 UnityEngine，必须单测]
  ISseTransport / ITransportHandle
  SseStreamDecoder
    - 用 System.Text.Decoder 做增量 UTF-8（跨 chunk 不破字符）
    - 行缓冲 -> 事件边界（空行）
  SseEventParser（vendor 的 SseParser）
  StreamingTimeoutPolicy
    - FirstByteTimeout / IdleTimeout
    - 只调用 handle.Abort()，不碰网络细节
  ChatStreamAggregator
    - content / reasoning / tool_calls(index) / usage / [DONE]
  StreamError 分类：ConnectFailed / HttpStatus / Parse / IdleTimeout / Cancelled / Aborted
```

### 5.2 传输接口（建议形状）

```csharp
public interface ISseTransport
{
    Task<ISseSession> OpenAsync(SseRequest request, CancellationToken ct);
}

public interface ISseSession : IDisposable
{
    // 原始字节流，跨 chunk 边界；由核心层负责解码与解析
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(CancellationToken ct);
    void Abort();          // 立即中止底层连接（UWR: request.Abort()）
    SseResponseInfo Response { get; } // 状态码、响应头、错误体读取器
}
```

要点：
- 传输层**只吐字节**，不判断 `[DONE]`、不拼 JSON（R2）。
- `Abort()` 与 `CancellationToken` 分离：取消令牌只负责“请求取消”，`Abort()` 保证底层连接立刻断（Must#2）。
- 错误体读取单独提供，避免把 SSE 流与错误流混在一起。

### 5.3 超时策略

```csharp
// 伪代码：两个独立看门狗，都在核心层，可单测
// 1) 从 OpenAsync 开始计时，直到收到第一块字节 -> FirstByteTimeout
// 2) 每收到一块字节重置计时 -> IdleTimeout
// 超时 -> session.Abort() -> 抛 StreamError.IdleTimeout / FirstByteTimeout
```

不要依赖 `UnityWebRequest.timeout` 做空闲检测。

### 5.4 WebGL（M2）

```
C#  ->  .jslib: SSE_Start(url, headers, body)
JS  ->  fetch(url, {method, headers, body}) + response.body.getReader()
JS  ->  每段文本 SendMessage(go, "OnSseChunk", base64/utf8)
C#  ->  同一套 SseStreamDecoder + Parser
JS  ->  结束/错误 SendMessage("OnSseDone" / "OnSseError")
```

注意浏览器 CORS、`Accept: text/event-stream`、以及不要在 JS 侧做二次解析（保持单一解析实现）。

---

## 六、明确不推荐

| 方案 | 原因 |
|---|---|
| `UnityWebRequest + DownloadHandlerBuffer` | 整包缓冲，假流式；生成期间无输出、长响应占内存 |
| 把 `UnityWebRequest.timeout` 当 SSE 超时 | 语义是整请求（且文档自相矛盾），长连接会被误杀或永不超时 |
| WebGL 上用 `HttpClient` / `SslStream` / `Task.Run` / `Task.Delay` | 不是性能差异，是浏览器不可恢复挂起 |
| 直接依赖 `com.openai.unity` 全家桶 | 6 个 UPM 依赖 + OpenAI 专用，核心库会被绑死 |
| 把 `LaunchDarkly.EventSource` 放进 Unity 核心层 | 自带重连/退避语义不适合一次性 LLM 流；HttpClient 在 IL2CPP 下未验证 |
| 复制 mikito 示例代码 | 仓库无 LICENSE，法律上不可复用 |
| 依赖 `System.Net.ServerSentEvents` 的 .NET 9+ 专有 API | 只能取 netstandard2.0 兼容的那部分源码/程序集；不要用 `Task.WaitAsync` 等 |
| 2021.3 上要求 HTTP/2 | 引擎不支持，除非升级到 Unity 6.3+（违反“不能升级 Unity”） |

---

## 七、下一步最小验证清单

- [ ] 把 `System.Net.ServerSentEvents` 的 `SseParser` 源码在 `netstandard2.1` 工程里编译通过（确认 `Span`/`IAsyncEnumerable`/`System.Memory` 无冲突）
- [ ] 离线单测：跨 chunk UTF-8、`\r\n`、多行 `data:`、注释行、`[DONE]`、空行边界
- [ ] `UwrSseTransport` 实机验证三件事：
  - [ ] `ReceiveData` 是否逐帧增量到达（而非结束后一次性）
  - [ ] `ReceiveData` 收到的数据是否已解 gzip
  - [ ] `Abort()` 后连接是否在一帧内停止、是否还能收到残留回调
- [ ] 验证 `responseCode == 0` 时 `result` / `error` 的实际取值，固化错误分类
- [ ] IL2CPP 构建 + `link.xml` 验证 `DownloadHandlerScript` 重写未被剥离
- [ ] 再评估是否需要 `HttpClientSseTransport`（显式代理/自签证书是否为硬需求）

---

## 八、来源

- System.Net.ServerSentEvents：https://github.com/dotnet/runtime/tree/main/src/libraries/System.Net.ServerSentEvents
- LaunchDarkly dotnet-eventsource：https://github.com/launchdarkly/dotnet-eventsource
- LaunchDarkly ConfigurationBuilder（ResponseStartTimeout / ReadTimeout / HttpClient / HttpRequestModifier）：https://launchdarkly.github.io/dotnet-eventsource/api/LaunchDarkly.EventSource.ConfigurationBuilder.html
- com.openai.unity：https://github.com/RageAgainstThePixel/com.openai.unity
- OpenAI-DotNet：https://github.com/RageAgainstThePixel/OpenAI-DotNet
- com.utilities.rest：https://github.com/RageAgainstThePixel/com.utilities.rest
- mikito/unity-chat-gpt-sample（stream）：https://github.com/mikito/unity-chat-gpt-sample/tree/stream
- SsePulse.Client：https://github.com/claudiom248/SsePulse.Client
- Azure/fetch-event-source：https://github.com/Azure/fetch-event-source
- NativeWebSocket：https://github.com/endel/NativeWebSocket
- Best Server-Sent Events（Asset Store）：https://assetstore.unity.com/packages/tools/network/best-server-sent-events-268758
- com.kirbyrawr.launchdarkly.eventsource：https://www.jsdelivr.com/package/npm/com.kirbyrawr.launchdarkly.eventsource
- Unity DownloadHandler.ReceiveData：https://docs.unity.com/en-us/engine/6000.5/script-reference/unityengine/networking/downloadhandler/receivedata
- Unity DownloadHandlerScript：https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.DownloadHandlerScript.html
- UnityWebRequest.timeout：https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.UnityWebRequest-timeout.html
- UnityWebRequest（2021.3）：https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.UnityWebRequest.html
- UnityWebRequest.certificateHandler：https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.UnityWebRequest-certificateHandler.html
- Unity 6.3 HTTP/2：https://docs.unity.com/en-us/engine/6000.3/manual/whats-new/unity63
- Unity Web 平台 .NET API 支持：https://docs.unity.com/en-us/engine/6000.3/manual/platform-specific/webgl/develop/web-dotnet-api-support
- Unity Networking 已知问题：https://unityreleases.com/issues?area=Networking&sort=days-desc&status=fixed
