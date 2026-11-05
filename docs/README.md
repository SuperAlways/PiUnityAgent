# docs · 索引

| 文档 | 内容 | 状态 |
|---|---|---|
| [00-项目定位与边界](00-项目定位与边界.md) | 是什么/不是什么、参照 pi、TFM 与 Unity 兼容、分层、硬约束、能力清单、路线 | v1 |
| [01-调研简报-CSharp生态](01-调研简报-CSharp生态.md) | 交给外部联网 agent 的调研简报（含可粘贴 Prompt） | v1 |
| [02-Pi.NET复用评估](02-Pi.NET复用评估.md) | Pi.NET 实测：net8 可用（6 处小改）、netstandard2.1 全量移植不划算；三条复用路线 | v1 |
| [03-开发方法-拆解学习与移植约定](03-开发方法-拆解学习与移植约定.md) | 参考源顺序（先原版 pi，后 Pi.NET）、参考源同步与版本打钉、三方对照、开发循环、讲解卡模板、教学文档体系、M1/2A/2B 里程碑、移植范围与 2.1 约束 | v1.1 |
| [04-开发流程分轨与模型路由](04-开发流程分轨与模型路由.md) | 设计/移植/探索三类工作、轻量移植流程、模型档位分配、成本纪律、失败升级阶梯 | v1 |
| [05-教学阶段路线](05-教学阶段路线.md) | S0–S13 教学阶段、参考索引、与 M1/2A/2B 的映射 | v1 |
| [06-工程架构与包依赖](06-工程架构与包依赖.md) | 当前 solution/工程/依赖方向/TFM/职责/边界/构建命令 | v1 |
| [superpowers/specs/2026-11-05-S1-消息与内容模型-design.md](superpowers/specs/2026-11-05-S1-消息与内容模型-design.md) | S1 实现设计 spec（pi 原版 → pi.net → Unity/2.1 → 最小偏离） | 待 review |
| [superpowers/plans/2026-11-05-S1-消息与内容模型.md](superpowers/plans/2026-11-05-S1-消息与内容模型.md) | S1 实现计划（TDD 任务、文件、测试、门禁） | 待执行 |
| [superpowers/specs/2026-11-05-S2-流式事件协议-design.md](superpowers/specs/2026-11-05-S2-流式事件协议-design.md) | S2 流式事件协议 spec（事件类型、EventStream、partial/终态/计时） | 讨论稿 v0.1 |
| [superpowers/plans/2026-11-05-S2-流式事件协议.md](superpowers/plans/2026-11-05-S2-流式事件协议.md) | S2 实现计划（EventStream/事件类型/流容器/门禁） | 待执行 |
| [superpowers/specs/2026-11-05-S3-Provider抽象与Faux-design.md](superpowers/specs/2026-11-05-S3-Provider抽象与Faux-design.md) | S3 Provider 抽象 + Faux spec（Model/Models/StreamFn/Faux） | 讨论稿 v0.1 |
| [superpowers/plans/2026-11-05-S3-Provider抽象与Faux.md](superpowers/plans/2026-11-05-S3-Provider抽象与Faux.md) | S3 实现计划（Model/Provider/Models/Faux/门禁） | 待执行 |
| [learning/](learning/README.md) | 教学与心智模型：导读、Q&A、逐阶段教学篇 | 骨架 |
| [learning/REFERENCES.md](learning/REFERENCES.md) | pi 教学项目逐章参考索引（按 S0–S13） | v1 |
| [../PORTING.md](../PORTING.md) | 上游文件 → PiUnityAgent 的逐文件映射与版本卡 | 骨架 |
| [../sop/02-参考源同步](../sop/02-参考源同步-2026-11-05.md) | 参考源同步与版本打钉的操作手册 | v1 |

## 待补

- `07-Core-MVP设计.md`：消息/事件/工具/会话的类型契约与状态机
- `08-宿主适配设计.md`：Unity 主线程、SSE、双进程 RPC
- `09-扩展SDK设计.md`：IExtension 生命周期、执行位、权限
- `10-选型结论.md`：外部调研 + pi.net 实验回收后的最终决定
- `model/`：心智模型系列（总览/循环/事件/工具/上下文/会话压缩/技能扩展/provider）
- `decisions/`：决策日志
