# src · 工程规划（待建）

> 当前无代码。以下是拟定结构，实现前先过 `docs/00-项目定位与边界.md` 与 `docs/03-开发方法-拆解学习与移植约定.md`。

```
src/
├─ PiUnityAgent.Ai/              # netstandard2.1，协议层
│  ├─ Types/                     # Model/Message/Content/ToolCall/Usage/StopReason/事件
│  ├─ Api/                       # OpenAiCompatible/Anthropic/Faux
│  └─ Providers/                 # provider 注册/模型目录
├─ PiUnityAgent.Core/            # netstandard2.1，循环层（引用 .Ai）
│  ├─ AgentLoop.cs
│  ├─ Agent.cs
│  ├─ ToolExecution.cs
│  ├─ Types/                     # AgentTool/AgentEvent/AgentContext
│  └─ Extensions/IExtension.cs   # 扩展契约（实现/加载在 Runtime）
├─ PiUnityAgent.Runtime/         # netstandard2.1，运行时（引用 .Core）
│  ├─ Session/                   # JSONL 会话树/branch/fork/active branch 重建
│  ├─ Context/                   # system prompt/上下文组装
│  ├─ Compaction/                # 摘要条目/阈值/原文保留
│  ├─ Skills/                    # 按需加载的指令/素材
│  ├─ Extensions/                # runner/loader/生命周期
│  └─ Resources/                 # 资源发现/加载
├─ PiUnityAgent.Hosts.Unity/     # netstandard2.1
│  ├─ UnityWebRequestTransport（SSE）
│  ├─ MainThreadDispatcher
│  └─ UnityToolExecutor
└─ PiUnityAgent.Hosts.Net8/      # net8.0
   ├─ HttpClientTransport（SSE）
   ├─ BackgroundRunner
   ├─ AlcExtensionLoader
   └─ RpcToolExecutor（与 Unity 宿主通信）
```

## 依赖规则（强制）

1. `PiUnityAgent.Ai` / `PiUnityAgent.Core` / `PiUnityAgent.Runtime` **禁止**引用 `UnityEngine` / `GameData.*` / 任何游戏 DLL。
2. 依赖方向：`Core → Ai`；`Runtime → Core / Ai`；`Hosts.* → 三者 + 平台 API`。
3. `Hosts.*` **禁止**包含业务判断；只做适配、线程、传输、加载。
4. 扩展只引用 `PiUnityAgent.Core` / `PiUnityAgent.Runtime` 的接口，不直接引用 Hosts。
5. 游戏更新时，只允许改 `Hosts.*` 与游戏适配器；`Ai/Core/Runtime` 与扩展零改动。
6. `Ai/Core/Runtime` 的新增逻辑必须配单测；Hosts 靠契约 + 实机验证。

## 命名约定

| 项 | 名称 |
|---|---|
| 解决方案 | `PiUnityAgent.slnx` |
| 协议程序集 | `PiUnityAgent.Ai.dll` |
| 循环程序集 | `PiUnityAgent.Core.dll` |
| 运行时程序集 | `PiUnityAgent.Runtime.dll` |
| Unity 宿主 | `PiUnityAgent.Hosts.Unity.dll` |
| .NET 8 宿主 | `PiUnityAgent.Hosts.Net8.dll` |
| 扩展接口 | `PiUnityAgent.Core.Extensions.IExtension` |
