# docs · 索引

> **状态约定**：✅ 已完成/已合 main · 🟡 待 review/待执行 · ⬜ 未开始 · 🗄 历史归档
> **最后同步**：2026-11-07（S3 返工 v2 已实现 + 后置能力交接治理 + LazyStream 对齐 pi/pi.net）

| 文档 | 内容 | 状态 |
|---|---|---|
| [00-项目定位与边界](00-项目定位与边界.md) | 是什么/不是什么、参照 pi、TFM 与 Unity 兼容、分层、硬约束、能力清单、路线 | v1 |
| [01-调研简报-CSharp生态](01-调研简报-CSharp生态.md) | 交给外部联网 agent 的调研简报（含可粘贴 Prompt） | v1 |
| [02-Pi.NET复用评估](02-Pi.NET复用评估.md) | Pi.NET 实测：net8 可用（6 处小改）、netstandard2.1 全量移植不划算；三条复用路线 | v1 |
| [03-开发方法-拆解学习与移植约定](03-开发方法-拆解学习与移植约定.md) | 参考源顺序、版本打钉、三方对照、开发循环、教学体系、里程碑、**§1.4 跨组件通讯契约**、**§1.5 后置能力交接治理** | v1.4 |
| [04-开发流程分轨与模型路由](04-开发流程分轨与模型路由.md) | 设计/移植/探索三类工作、轻量移植流程、模型档位、成本纪律、失败升级 | v1 |
| [05-教学阶段路线](05-教学阶段路线.md) | S0–S13 教学阶段、参考索引、与 M1/2A/2B 的映射；S3 返工 v2 已实现；后置能力阶段门禁 | v1.3 |
| [06-工程架构与包依赖](06-工程架构与包依赖.md) | 当前 solution/工程/依赖方向/TFM/职责/边界/构建命令；B 通讯端口 | v3 |
| [07-通讯层集成契约](07-通讯层集成契约.md) | **PiUnityAgent ↔ UnityLlmTransport 的唯一接缝**：Fetch 端口、适配器、映射、版本锁、Phase 2 证据 | v1 |
| [SOP/后置能力交接治理](SOP/后置能力交接治理-SOP-2026-11-07.md) | 后置能力台账字段、代码锚点、阶段开工/收尾门禁、`check-deferred.py` 规则 | v1 |
| [待办-后置能力](待办-后置能力.md) | D1–D11 唯一权威台账（形状锚点/验收测试/状态/完成提交） | v2 |
| [decisions/2026-11-07-通讯端口归属](decisions/2026-11-07-通讯端口归属.md) | A/B 决策记录：选 B（PiUnityAgent 自有 Fetch 端口） | 已接受 |
| [superpowers/specs/2026-11-05-S1-消息与内容模型-design.md](superpowers/specs/2026-11-05-S1-消息与内容模型-design.md) | S1 实现设计 spec | ✅ 已实现并合 main |
| [superpowers/plans/2026-11-05-S1-消息与内容模型.md](superpowers/plans/2026-11-05-S1-消息与内容模型.md) | S1 实现计划 | ✅ 已执行 |
| [superpowers/specs/2026-11-05-S2-流式事件协议-design.md](superpowers/specs/2026-11-05-S2-流式事件协议-design.md) | S2 流式事件协议 spec | ✅ 已实现并合 main |
| [superpowers/plans/2026-11-05-S2-流式事件协议.md](superpowers/plans/2026-11-05-S2-流式事件协议.md) | S2 实现计划 | ✅ 已执行 |
| [superpowers/specs/2026-11-05-S3-Provider抽象与Faux-design.md](superpowers/specs/2026-11-05-S3-Provider抽象与Faux-design.md) | S3 Provider + Faux 原 spec | ✅ 初版已合 main |
| [superpowers/plans/2026-11-05-S3-Provider抽象与Faux.md](superpowers/plans/2026-11-05-S3-Provider抽象与Faux.md) | S3 实现计划 | ✅ 已执行 |
| [superpowers/specs/2026-11-07-S3-对齐返工-design-v2.md](superpowers/specs/2026-11-07-S3-对齐返工-design-v2.md) | S3 形状对齐返工 spec v2（B 通讯端口：Fetch 端口 + Host 适配器；LazyStream 已对齐 pi/pi.net） | ✅ 已实现（`feature/s3-rework-v2`） |
| [superpowers/plans/2026-11-07-S3-对齐返工-v2.md](superpowers/plans/2026-11-07-S3-对齐返工-v2.md) | S3 返工 v2 实现计划（Fetch/Model/Options/Auth/LazyStream/Models/Faux + 文档门禁） | ✅ 已执行 |
| [learning/](learning/README.md) | 教学与心智模型：导读、Q&A、逐阶段教学篇 | 骨架 |
| [learning/REFERENCES.md](learning/REFERENCES.md) | pi 教学项目逐章参考索引（按 S0–S13） | v1 |
| [archive/2026-11-06-Unity流式HTTP与SSE-调研问题](archive/2026-11-06-Unity流式HTTP与SSE-调研问题.md) | 通讯组件立项前的调研问题（历史） | 🗄 已被 UnityLlmTransport docs 取代 |
| [archive/2026-11-06-Unity流式HTTP与SSE-调研结论](archive/2026-11-06-Unity流式HTTP与SSE-调研结论.md) | 通讯组件立项前的调研结论（历史） | 🗄 同上 |
| [../PORTING.md](../PORTING.md) | 上游文件 → PiUnityAgent 的逐文件映射与版本卡 | 持续更新 |
| [../sop/02-参考源同步](../sop/02-参考源同步-2026-11-05.md) | 参考源同步与版本打钉的操作手册 | v1 |

## 待补

- `08-Core-MVP设计.md`：消息/事件/工具/会话的类型契约与状态机
- `09-宿主适配设计.md`：Unity 主线程、Fetch 适配器、双进程 RPC
- `10-扩展SDK设计.md`：IExtension 生命周期、执行位、权限
- `11-选型结论.md`：外部调研 + pi.net 实验回收后的最终决定
- `model/`：心智模型系列（总览/循环/事件/工具/上下文/会话压缩/技能扩展/provider）
- `decisions/`：后续决策日志（首条已落 `2026-11-07-通讯端口归属`）
