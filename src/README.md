# src · 工程规划（待建）

> 当前无代码。以下是拟定结构，实现前先过 `docs/00-项目定位与边界.md`。

```
src/
├─ PiAgent.Core/            # netstandard2.1，纯逻辑，必须单测
│  ├─ Ai/                  # 消息/内容块/工具调用/provider 抽象/流式事件/模型目录/重试
│  ├─ Agent/               # AgentLoop/Agent(steering/follow-up)/ToolRegistry/ITransport
│  ├─ Session/             # JSONL 会话树/branch/fork/active branch 重建
│  ├─ Compaction/          # 摘要条目/阈值/原文保留
│  ├─ Skills/              # 按需加载的指令/素材
│  └─ Extensions/          # IExtension/tools/commands/hooks/context transform
├─ PiAgent.Hosts.Unity/     # netstandard2.1
│  ├─ UnityWebRequestTransport（SSE）
│  ├─ MainThreadDispatcher
│  └─ UnityToolExecutor
└─ PiAgent.Hosts.Net8/      # net8.0
   ├─ HttpClientTransport（SSE）
   ├─ BackgroundRunner
   ├─ AlcExtensionLoader
   └─ RpcToolExecutor（与 Unity 宿主通信）
```

## 依赖规则（强制）

1. `PiAgent.Core` **禁止**引用 `UnityEngine` / `GameData.*` / 任何游戏 DLL。
2. `Hosts.*` 可以引用 Core 与平台 API；**禁止**包含业务判断。
3. 扩展只引用 `PiAgent.Core` 的接口，不直接引用 Hosts。
4. 游戏更新时，只允许改 `Hosts.*` 与游戏适配器；Core 与扩展零改动。
5. Core 的新增逻辑必须配单测；Hosts 靠契约 + 实机验证。

## 命名约定

| 项 | 名称 |
|---|---|
| 解决方案 | `UnityGameAiModPiAgentCore.slnx` |
| Core 程序集 | `PiAgent.Core.dll` |
| Unity 宿主 | `PiAgent.Hosts.Unity.dll` |
| .NET 8 宿主 | `PiAgent.Hosts.Net8.dll` |
| 扩展接口 | `PiAgent.Extensions.IExtension` |
