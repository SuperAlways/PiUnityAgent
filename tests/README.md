# tests · 测试策略（待建）

> 当前无测试代码。测试面随 Core 实现同步建立。

## 分层

| 层 | 可测性 | 方式 |
|---|---|---|
| `PiAgent.Core` | ✅ 必须单测 | 纯逻辑，无 Unity/游戏依赖；xUnit/NUnit 均可 |
| `PiAgent.Hosts.*` | ⚠️ 靠契约 + 实机 | 胶质层，游戏更新只改这里 |
| 扩展 | 视业务 | 纯逻辑部分下沉到 Core 风格；胶质部分实机 |

## 必须覆盖的 Core 场景（MVP）

1. Agent Loop：单轮文本、多轮工具调用、工具失败、中止。
2. 流式事件：text / reasoning / tool call start-delta-end / usage / done 的顺序与组装。
3. Provider 适配：OpenAI-compatible 与 Anthropic 的消息/工具协议互转。
4. 会话树：branch、fork、active branch 重建、JSONL 读写。
5. 压缩：阈值触发、摘要条目替换、原文保留、压缩后上下文正确。
6. 技能：摘要→全文的按需加载。
7. 工具注册：schema 校验、执行位路由、超时、错误回传。

## 一致性基准

- 用 pi（Node）跑同一组 prompt/工具场景，记录事件流作为 golden transcript；
- C# 版输出与 golden 做结构 diff，作为契约测试；
- 不要求逐字节一致，要求语义一致（消息序列、工具调用、终止条件）。

## 门禁

- Core：`dotnet test` 全绿。
- Release 构建：0 error。
- 新逻辑无单测不合并。
