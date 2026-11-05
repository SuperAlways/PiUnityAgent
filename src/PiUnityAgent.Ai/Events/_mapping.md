# Events · 上游映射（pi v1.5.0）

| 上游 | 我们 | 说明 |
|---|---|---|
| `packages/ai/src/types.ts:823-840` `AssistantMessageEvent` | `AssistantMessageEvent.cs` | 12 变体；wire `Type` 名 |
| `packages/ai/src/utils/event-stream.ts` `EventStream` | `EventStream.cs` | Channel 单消费者；`Push/End/Fail/WaitForResultAsync` |
| `packages/ai/src/utils/event-stream.ts` `AssistantMessageEventStream` | `AssistantMessageEventStream.cs` | partial + 终态 + durationMs |
| `pi.net Stream/AssistantMessageEventStream.cs` | 同名 | pi.net 额外字段（Sequence/Index/ErrorMessage）未采用 |
| `pi.net Utils/EventStream.cs` | `EventStream.cs` | pi.net 的 `Task.WaitAsync`/多消费者说明不采用；单消费者 |
| pi TS `EventStream`（`AsyncIterable`）/ pi.net `GetAsyncEnumerator` | `EventStream<TEvent,TResult> : IAsyncEnumerable<TEvent>` | 单消费者按序迭代 |
| 终态标志（pi TS 无公开位；我们显式暴露） | `EventStream.IsEnded` | 收场后 `Push` 被忽略；`Push` 单生产者（`_ended` check-then-act 非原子） |

## 2.1 适配

- `System.Threading.Channels` 8.0.0；
- `Task.WaitAsync` 不存在 → `Task.WhenAny` + `CancellationToken.Register`；
- `Stopwatch.GetElapsedTime` 不存在 → `GetTimestamp`/`Frequency`；
- 事件不落 JSON；`AssistantMessage.DurationMs` 改为 `get; set;` 以便终态计时；
- `Push` 终态回调（`isComplete`/`extractResult`）抛异常 → 流按失败收场：通道以原异常完成（事件可能已入队、对消费者可见），`WaitForResultAsync` 抛原异常；
- `Fail` 仅限基础设施错误：迭代侧与 `WaitForResultAsync` 都抛原异常（Channels 完成错误不包 `ChannelClosedException`，net10.0.9 框架实现 `AsyncOperation.GetResult` 走 `ExceptionDispatchInfo.Throw()` 已核实）。
