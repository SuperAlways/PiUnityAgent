# 调研问题：Unity 中的流式 HTTP / SSE（用于 LLM）

> **历史归档（2026-11-07）**：本文是通讯组件立项前的调研问题，结论已落地到独立仓库 `UnityLlmTransport`。当前事实以 `UnityLlmTransport/docs/`（尤其 Phase 0/2 实机结果）为准；本文仅作决策历史保留。

> **用途**：交给能联网的检索型 agent，寻找可复用的开源方案。
> **项目**：PiUnityAgent —— 面向 Unity 游戏 mod 的 C# `netstandard2.1` LLM agent 底座。
> **日期**：2026-11-06

---

## 一、一句话问题

在 **Unity（Mono / IL2CPP，主线程约束）** 里，如何稳定地做 **OpenAI 兼容接口的流式 HTTP + SSE**，实现真正的增量输出、可取消、可配代理/TLS，并且不把 Unity 依赖泄漏进核心库？

---

## 二、环境约束（硬约束）

- **语言/框架**：C#，`netstandard2.1`；不能使用 .NET 6/7/8/9 专有 API（`Task.WaitAsync`、`Stopwatch.GetElapsedTime`、`System.Threading.Lock`、`JsonStringEnumMemberName` 等）。
- **引擎**：Unity 2021.2+（实际版本由游戏锁定，**不能升级 Unity**）；桌面 Windows 优先；Mono 运行时为主，需考虑 IL2CPP/AOT 兼容性。
- **线程**：Unity API 只能在主线程；网络/解析可以在后台，但事件与 UI 必须 marshaling 回主线程；不能阻塞主线程。
- **进程形态**：可能是 Unity 进程内直连（mod 前端），也可能有独立的 .NET 8 后端进程（说书人形态）。
- **依赖**：尽量零/少第三方依赖；许可证需 MIT/Apache 类；不能引入原生插件或要求 Unity 升级。
- **可选平台**：WebGL（无线程/无原始 socket）暂不作为 M1 目标，但希望方案不把它彻底堵死。

---

## 三、需求清单

### Must

1. **真流式**：SSE 增量到达即处理，不是收完整个响应再解析；支持 `data:` 多行、`\r\n`、注释行、`[DONE]`、跨 TCP chunk 的 UTF-8 字符。
2. **可取消**：取消后尽快中止底层连接；不能只是停止读取而让请求继续跑。
3. **超时分层**：连接/首字节超时与流空闲超时分开；不能只有一个"整请求 timeout"。
4. **TLS**：HTTPS 正常；证书校验可控（企业代理/自签场景可配置，但默认安全）。
5. **代理**：支持系统代理或显式代理配置（HTTP/HTTPS proxy）。
6. **线程安全**：事件/字节可以后台产生，交付到 Unity 主线程；核心库不引用 `UnityEngine`。
7. **错误可分类**：连接失败、HTTP 非 2xx、SSE 解析失败、空闲超时、取消；错误体可读。
8. **可测试**：传输层可注入 mock，不依赖真实网络。

### Should

9. 支持 HTTP/2 或至少不因 HTTP/1.1 分块编码出问题。
10. 支持 gzip/deflate（SSE 通常 identity，但错误体可能压缩）。
11. 支持重定向（可选）。
12. 连接池/并发请求（多个 LLM 调用并存）。
13. IL2CPP/AOT 下不依赖动态代码/反射。
14. 与 Unity 生命周期集成：退出/切场景/域重载时可取消、无泄漏。

### Nice

15. WebGL 可用（fetch/XHR 流式）。
16. 支持 WebSocket 传输（部分厂商）。
17. 现成的 SSE 解析器/事件流封装。

---

## 四、已知方案与失败点（供检索参考）

| 方案 | 实证 | 结果 |
|---|---|---|
| **UnityWebRequest + `DownloadHandlerBuffer`** | TaiWuYaoLao `LlmTransportHost.cs` | ❌ 整包缓冲，假流式；生成期间无输出；长响应占内存 |
| **UnityWebRequest + `DownloadHandlerScript`** | WorldTalk `AiApiClient.cs` | ✅ 真流式：`ReceiveData` 增量 UTF-8 解码、行缓冲、content/reasoning/tool_calls/usage 聚合、`Abort()` 取消；但回调在主线程、timeout 是整请求、TLS/代理受 Unity 后端限制 |
| **HttpClient + `ResponseHeadersRead` + `Stream`** | 江湖有灵非流式用 `HttpClient`；流式**不用** HttpClient | ⚠️ 流式改用 **裸 `TcpClient` + `SslStream` + 手写 HTTP/1.1 + 自解 chunked**；说明其在 Unity/Mono 下不信任 HttpClient 的流式行为 |
| **裸 TCP/SslStream 手写 HTTP** | 江湖有灵 `RawSseStreamAsync` | ✅ 可控、真流式；❌ 无 HTTP/2、无系统代理、TLS/证书/重定向/连接池全部自维护 |
| **.NET 8 后端进程 + HttpClient** | 说书人架构 | ✅ 标准 .NET 流式、代理/TLS/取消都正常；❌ 需要 IPC 把事件送到 Unity |
| **pi.net** | `net9.0;net10.0` | ❌ 不在问题域：它假设完整 .NET 运行时，不解决 Unity 宿主 |

### 关键现象

- Unity 的 `UnityWebRequest` **不是 `Stream`**；默认 handler 缓冲；只有 `DownloadHandlerScript` 能增量接字节。
- `DownloadHandlerScript.ReceiveData` 的回调线程/时序在目标 Unity 版本上需要确认；解析不能太重。
- Unity Mono/IL2CPP 下 `HttpClient` 的流式、TLS、代理行为可能随平台/版本不同；有项目为此绕开 HttpClient。
- UnityWebRequest 的 `timeout` 是整请求，不是空闲超时；SSE 长连接需要额外机制。
- 连接失败时 `responseCode` 可能为 0，不能只按状态码分类。
- WebGL 没有线程/原始 socket，方案完全不同。

---

## 五、请检索的问题

### A. 现成开源库/插件

1. 有没有 **Unity/C# 的 SSE 客户端**库（支持 `netstandard2.1`、Unity 2021+、Mono/IL2CPP）？
2. 有没有 **Unity 里做 OpenAI/DeepSeek 流式对话**的开源项目，能直接参考其传输层？
3. 有没有 **UnityWebRequest + DownloadHandlerScript 的 SSE/流式封装**（开源、可维护）？
4. 有没有 **HttpClient 在 Unity Mono/IL2CPP 下可靠流式**的适配库/补丁？
5. 有没有 **WebGL SSE** 的开源方案（fetch/XHR）？
6. 有没有 **C# 的增量 SSE 解析器**（可独立于传输层复用，MIT/Apache）？

### B. 平台/运行时事实

7. Unity 2021.2+ 的 Mono 与 IL2CPP 下，`HttpClient` + `HttpCompletionOption.ResponseHeadersRead` 是否稳定支持增量 `Stream.ReadAsync`？有哪些已知 issue？
8. `UnityWebRequest` + `DownloadHandlerScript` 在各桌面平台是否真的逐块回调，是否存在整包缓冲的版本/平台？
9. UnityWebRequest 的代理、TLS 证书、HTTP/2 支持现状？
10. UnityWebRequest 的 timeout 语义（整请求 vs 空闲）与取消行为？
11. IL2CPP/AOT 下 `HttpClient`/TLS 需要哪些裁剪/`link.xml` 配置？
12. WebGL 下 SSE/流式 fetch 的可行性与限制？

### C. 架构建议

13. 对"Unity 前端直连 + 未来可能后端"的底座，传输层应如何抽象，才能同时容纳 UWR、HttpClient、裸 TCP、后端 RPC？
14. 是否已有开源项目采用"传输接口 + 多实现"的同类设计，可借鉴接口形状？
15. 在多平台（Windows Mono / IL2CPP / WebGL）下，推荐的优先级/回退链是什么？

---

## 六、候选方案评估标准

请对每个候选给出：

| 维度 | 说明 |
|---|---|
| 仓库/链接 | GitHub/GitLab 等 |
| 许可证 | MIT/Apache/其他 |
| 最近活跃 | 最近 commit / release |
| Unity 支持 | 版本范围、Mono/IL2CPP/WebGL |
| 传输方式 | UWR / HttpClient / 裸 TCP / 其他 |
| 真流式 | 是否增量、是否整包缓冲 |
| SSE 解析 | 多行/CRLF/`[DONE]`/跨 chunk |
| 取消/超时 | 连接、首字节、空闲、取消传播 |
| 代理/TLS | 系统代理、显式代理、证书配置 |
| 依赖 | 是否引入原生插件/大依赖 |
| AOT | IL2CPP 兼容性 |
| 适配成本 | 接到 `netstandard2.1` 核心库的难度 |
| 风险 | 维护性、已知 bug、平台坑 |

---

## 七、输出格式

1. **候选表**：按"适配度"排序，附仓库链接与许可证；
2. **推荐架构**：1–3 种，含取舍；
3. **已知坑/issue 链接**：特别是 Unity HttpClient 流式、UWR chunked、代理/TLS；
4. **可复用代码片段**：SSE 增量解析、UWR handler、取消/超时；
5. **明确不推荐**：哪些方案看似可行但在 Unity 下会踩坑，为什么。

---

## 八、请不要建议

- 升级 Unity 或 .NET 版本；
- 引入需要原生插件/闭源 SDK 的方案（除非明确说明平台与许可证）；
- 把"整包缓冲后再解析"称为流式；
- 依赖 .NET 9/10 专有 API；
- 在非主线程调用 Unity API；
- 只给概念、不给可验证的仓库/文档链接。
