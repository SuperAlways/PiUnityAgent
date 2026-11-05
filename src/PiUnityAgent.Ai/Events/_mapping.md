# Events · 上游映射（pi v1.5.0）

| 上游 | 我们 | 说明 |
|---|---|---|
| `packages/ai/src/types.ts:823-840` `AssistantMessageEvent` | `AssistantMessageEvent.cs` | 12 变体；wire `Type` 名 |
| `packages/ai/src/utils/event-stream.ts` `EventStream` | `EventStream.cs` | Channel 单消费者；`Push/End/Fail/WaitForResultAsync` |
| `packages/ai/src/utils/event-stream.ts` `AssistantMessageEventStream` | `AssistantMessageEventStream.cs` | partial + 终态 + durationMs |
| `pi.net Stream/AssistantMessageEventStream.cs` | 同名 | pi.net 额外字段（Sequence/Index/ErrorMessage）未采用 |
| `pi.net Utils/EventStream.cs` | `EventStream.cs` | pi.net 的 `Task.WaitAsync`/多消费者说明不采用；单消费者 |

## 2.1 适配

- `System.Threading.Channels` 8.0.0；
- `Task.WaitAsync` 不存在 → `Task.WhenAny` + `CancellationToken.Register`；
- `Stopwatch.GetElapsedTime` 不存在 → `GetTimestamp`/`Frequency`；
- 事件不落 JSON；`AssistantMessage.DurationMs` 改为 `get; set;` 以便终态计时。
