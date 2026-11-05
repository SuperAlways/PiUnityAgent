# Providers · 上游映射（pi v1.5.0）

| 上游 | 我们 | 说明 |
|---|---|---|
| `packages/ai/src/types.ts` `Model` / `BaseModel` | `Types/Model.cs` | 字段子集；无泛型 api 约束 |
| `packages/ai/src/models.ts` `Models` | `Providers/Models.cs` | 最小 chat 注册表；无 auth/刷新/图片/分类 |
| `packages/ai/src/providers/faux.ts` `createFauxCore` | `Providers/Faux/FauxProvider.cs` + `Faux.cs` | 脚本队列/工厂/状态；无 deferred |
| `pi.net Providers/Faux.cs` | 同名 | pi.net 的 auth/ModelSpec/Deferred 不采用 |
| `pi.net Models/Models.cs` | `Models.cs` | 最小注册表 |

## 2.1 适配

- Faux ID 用计数器，不用时间/随机；
- 时间用 `IClock`；
- 分块固定大小；
- 默认零延迟；`TokensPerSecond` 仅 Faux 选项；
- 无 HTTP/SSE/Auth。
