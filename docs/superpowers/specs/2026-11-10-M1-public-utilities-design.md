# M1 公共工具补齐设计 spec（2026-11-10）

> **状态**：已实现并合入 `fix/m1-audit-fixes`（提交 `955e7cc`；四文件 + 24 测试用例，Release 0/0、Ai 1157/1157、Core 96/96、`check-deferred S8` OK）。本 spec 为实现后的登记文档（U-S1 采取"补实现"路线，非 Dn）。
> **驱动**：`docs/audits/2026-11-10-M1-S1-S8-cumulative-deviation-audit.md` §3 U-S1（Shape gap，Important）——pi-ai index.ts 四个公共导出（overflow/string-enum/uuidv7/session-resources）在 S1–S8 八个阶段中无人认领，全仓零对应物、零 Dn、零 spec 行、PORTING 未列。
> **参考版本**：pi `v1.5.0` / `f9a263655`（`packages/ai/src/utils/overflow.ts` 195 行、`utils/string-enum.ts` 24 行、`utils/uuid.ts` 45 行、`session-resources.ts` 24 行，逐字对照）；pi.net `main` / `2d6757b`（四件均无 C# 移植，仅写法参考不可用）。
> **上位规则**：[`docs/03-开发方法-拆解学习与移植约定.md`](../../03-开发方法-拆解学习与移植约定.md) §1.3（"公共类型/接口/字段等形状不可裁剪"）；[`docs/SOP/阶段偏离审查-SOP-2026-11-08.md`](../../SOP/阶段偏离审查-SOP-2026-11-08.md) §4 分类。
> **修复动作**：审计三选一中的 (a) 补实现——总量约 280 行 C#（pi 侧 288 行 TS），无外部依赖，不新增 NuGet。

---

## 0. 为什么补实现而不是 Dn

审计 U-S1 的三选一：(a) 补实现 / (b) 补 4 条 Dn 行 / (c) 用户批准裁剪为 D。四件全部是"pi-ai 对外公共面"的一员：overflow 是 2A 压缩（compaction）的核心判据（`isContextOverflow`/`isRecoverableLength`），StringEnum 是工具 schema 作者助手，uuidv7/session-resources 当前仅 codex provider（暂缓）消费但属公共导出。C# 完全可以实现（无生态强制），也不是"行为后置但形状保留"——形状本身此前缺席，因此走 (a) 补实现：一次性消除缺口，不产生新台账行。

---

## 1. 四公共导出与落点

pi `packages/ai/src/index.ts` 导出面（本轮逐行核）：

| pi index 导出 | pi 源文件 | pi 侧消费点 | 我们的落点 | 状态 |
|---|---|---|---|---|
| `index.ts:39` `export * from "./utils/overflow.ts"` | `packages/ai/src/utils/overflow.ts`（`isContextOverflow`/`isRecoverableLength`/`getOverflowPatterns`） | pi `packages/durable/src/harness/generation.ts:12`；2A 压缩（oversight 消费方）；pi 测试 3 处 | `src/PiUnityAgent.Ai/Utils/Overflow.cs` | ✅ |
| `index.ts:42` `export * from "./utils/string-enum.ts"` | `packages/ai/src/utils/string-enum.ts`（`StringEnum`） | 工具 schema 作者助手（pi 测试 2 处消费） | `src/PiUnityAgent.Ai/Utils/StringEnum.cs` | ✅ |
| `index.ts:45` `export { uuidv7 } from "./utils/uuid.ts"` | `packages/ai/src/utils/uuid.ts`（`uuidv7`） | 仅 `api/openai-codex-responses.ts:41`（codex provider，暂缓） | `src/PiUnityAgent.Ai/Utils/Uuid.cs` | ✅ |
| `index.ts:33` `export * from "./session-resources.ts"` | `packages/ai/src/session-resources.ts`（`registerSessionResourceCleanup`/`cleanupSessionResources` + `SessionResourceCleanup` 类型） | 仅 `api/openai-codex-responses.ts:9`（codex provider，暂缓） | `src/PiUnityAgent.Ai/SessionResources.cs` | ✅ |

PORTING.md §1 四行已同步登记（✅，指向本 spec）。

---

## 2. 公共签名与行为

### 2.1 Overflow（`Utils/Overflow.cs`）

```csharp
public static bool IsContextOverflow(AssistantMessage message, int? contextWindow = null);
public static bool IsRecoverableLength(AssistantMessage message, int desiredMaxOutput);
public static IReadOnlyList<Regex> GetOverflowPatterns();
```

- 三段判定与 pi `overflow.ts:144-184` 逐段一致：① 错误文案命中 27 条主溢出模式（`OVERFLOW_PATTERNS` overflow.ts:39-69，逐条照搬，全部 `RegexOptions.IgnoreCase`）；② 静默溢出（z.ai 式：`stopReason=stop` 且 `input+cacheRead > contextWindow`，严格大于）；③ length 止步零输出且 `input+cacheRead >= 99%` 窗口（Xiaomi MiMo 式）。
- 非溢出排除 3 条（`NON_OVERFLOW_PATTERNS` overflow.ts:82-86：Bedrock 限流前缀/rate limit/too many requests）先行排除；Cerebras 400/413 无响应体模式仅对 `provider=="cerebras"` 生效。
- `GetOverflowPatterns()` 返回数组副本（正则实例复用），对齐 pi `[...OVERFLOW_PATTERNS]`（overflow.ts:193-194）。按审计任务口径不导出非溢出排除列表（pi 也未导出）。

### 2.2 StringEnum（`Utils/StringEnum.cs`）

```csharp
public static JsonObject Create(
    IReadOnlyList<string> values,
    string? description = null,
    string? defaultValue = null);
```

- 产出 `{ type: "string", enum: [...] }` `JsonObject`（Google 等 provider 只认此形，不认 anyOf/const）。
- `description`/`default` 仅在**非空**时写入：空串省略、空白串保留——对齐 pi 真值展开（`string-enum.ts:20-21` `...(options?.description && {...})`：`""` falsy 省略、`" "` truthy 保留）。

### 2.3 Uuid（`Utils/Uuid.cs`）

```csharp
public const long MaxTimestamp = 0xFFFFFFFFFFFF;   // pi MAX_UUID_V7_TIMESTAMP
public static string V7(long? timestampMs = null);
```

- 48 位时间戳上限校验（越界抛异常，文案逐字 `UUIDv7 timestamp must be an integer between 0 and 281474976710655`）。
- 普通调用取 `DateTimeOffset.UtcNow` 毫秒并单调（`max(now, lastOrdinary)`，晚到的钟不回退）；显式时间戳原样保留（follower id）且**不推进**单调时钟（对齐 pi `uuid.ts:15-21`）。
- 41 位序列计数器：首次由随机字节播种（必然 < 上限），之后每调用 +1（与时间戳是否前进无关），用尽抛异常（文案逐字 `UUIDv7 generator sequence exhausted`）。
- 字节布局逐位对齐：`ver=7`（bytes[6] 高 nibble）、`variant=10`（bytes[8] 高两位）、bytes[12..15] 保持随机、bytes[11] 保留随机低位；8-4-4-4-12 小写十六进制。
- 并发：`RandomNumberGenerator.Create()` + 全程 `lock`（时间戳/序列/随机字节一体，保证全序）；pi 是模块级可变状态、单线程假设（偏离见 §3 行3）。

### 2.4 SessionResources（`SessionResources.cs`，Ai 命名空间根）

```csharp
public delegate void SessionResourceCleanup(string? sessionId);
public static IDisposable RegisterSessionResourceCleanup(SessionResourceCleanup cleanup);
public static void CleanupSessionResources(string? sessionId = null);
```

- 注册进锁保护的集合，返回 `IDisposable` 注销句柄（pi 返回注销闭包；Dispose 即注销，可重复 Dispose 幂等）。
- 清理：逐个回调 try/catch 收集异常，**单个失败不中断其余回调**，有错则抛 `AggregateException("Failed to cleanup session resources", errors)`（pi `AggregateError` 同文案）。sessionId 可空（全局清理）。
- 遍历为**快照遍历**（锁内拷贝后执行）：pi 对 `Set` 活迭代（偏离见 §3 行4）。

---

## 3. 三方偏离表（pi / pi.net / 我们）

| # | 项 | pi 原版 | pi.net | 我们 | 类别 | 原因 / 代价 |
|---|---|---|---|---|---|---|
| 1 | StringEnum 返回类型 | `StringEnum(values, options?)` 返回 `Schema<T[number]>`（pi-schema 泛型对象，带 `Static<>` 静态推导） | 无移植 | `StringEnum.Create(values, description?, defaultValue?)` 返回 `System.Text.Json.Nodes.JsonObject` | B | C# 无可选对象参数惯用法；Ai 层 schema 统一走 System.Text.Json.Nodes（S6 既定方向）。代价=类型层面弱于 TS 泛型 Schema 的静态推导；产出形状（键/真值规则）逐字一致 |
| 2 | Uuid 异常类型 | 越界抛 `RangeError`；同一毫秒序列耗尽抛 `RangeError`（uuid.ts:15/34） | 无移植 | 越界抛 `ArgumentOutOfRangeException`；序列耗尽抛 `InvalidOperationException`；**文案逐字保留** | A/B | .NET 异常语义对应（参数越界/状态非法）；文案不变。代价=按异常类型分流的调用方需知差异 |
| 3 | SessionResources 遍历语义 | `Set` **活迭代**：回调内再注册/注销会影响本轮遍历（session-resources.ts:17-27） | 无移植 | **快照遍历**（锁内 `CopyTo` 后执行回调）：回调内注册/注销不影响本轮 | D | 收益=回调可在持锁语义外安全注册/注销，避免锁内执行用户回调与重入死锁；代价=与 pi 在"回调中改注册表"这一极端竞态下多/少一轮的差异（后续清理兜住）；单线程常规路径行为等价 |
| 4 | 真值判断 C# 化 | `contextWindow` 真值判断（`0`/undefined 同为"未传"，overflow.ts:158/170）；description/default 真值展开（`""` 省略） | 无移植 | `contextWindow is int w && w != 0`（`0` 视为未设）；`!string.IsNullOrEmpty(description/defaultValue)`（空串省略、空白串保留） | A/B | JS truthy → C# 显式判定的等价改写；语义（0=未传、空串=省略、空白串=保留）与 pi 完全一致，仅表达形式不同 |
| 5 | 并发模型 | 模块级可变状态（`lastOrdinaryTimestamp`/`sequence`），单线程假设 | 无移植 | `Uuid` 全程 `lock`；`SessionResources` 锁保护集合 | D | C# 宿主可能多线程；pi 无并发语义可对照。代价=锁粒度带来可忽略开销；行为等价且并发安全 |
| 6 | 注销句柄 | 返回注销闭包 `() => void` | 无移植 | 返回 `IDisposable`（Dispose 即注销，幂等） | B | C# 资源释放惯例；无行为差异 |
| 7 | 集合返回类型 | `getOverflowPatterns(): RegExp[]` | 无移植 | `IReadOnlyList<Regex>`（仍返回副本） | B | C# 集合接口惯例；无行为差异 |
| 8 | 清理回调调用顺序 | `Set` 为插入序，cleanup 按注册顺序调用 | 无移植 | `HashSet<SessionResourceCleanup>`，迭代序未定义 | D | 审计复核 R-1；收益=O(1) 去重；代价=多回调时调用顺序不保证（测试不契约化顺序）。若需序，可改 List+HashSet 双结构 |
| 9 | null 清理回调 | `Set.add(null)` 可入表，cleanup 时 null 调用抛 TypeError 被聚合成 AggregateError | 无移植 | 注册时即抛 `ArgumentNullException` | D | 审计复核 R-2；收益=早失败、错误定位在注册点；代价=与 pi 的“清理时才聚合失败”时序不同 |

---

## 4. 测试与验收

| 测试类 | 覆盖 |
|---|---|
| `tests/PiUnityAgent.Ai.Tests/Utils/OverflowTests.cs`（8 个 Fact/Theory） | 22 条代表性 provider 溢出文案命中；Bedrock 限流文案被非溢出模式排除；Cerebras 400/413 无响应体仅对 `provider=="cerebras"` 生效；错误信息缺失/空串不走文案判定；静默溢出严格大于窗口（边界 1099/1100/1101）+ 不传窗口不判；length 止步零输出 99% 阈值（990/1000 → true、1001 → false、output>0 → false）；`IsRecoverableLength` 五分支；`GetOverflowPatterns` 每次新数组 + 数量 > 20 |
| `tests/PiUnityAgent.Ai.Tests/Utils/StringEnumTests.cs` | type/enum 形状与值序；description/default 齐备时写入；空串省略、空白串保留（pi 真值语义） |
| `tests/PiUnityAgent.Ai.Tests/Utils/UuidTests.cs` | 200 次格式正则（版本 nibble 显式断言）；显式时间戳编码进高 48 位（`00000000-03e8` / 上限 `ffffffff-ffff`）；普通调用推高单调钟后显式早时间戳仍原样保留；200 次普通调用字典序严格递增；同显式时间戳重复调用靠序列区分且有序；`-1` 与 `MaxTimestamp+1` 抛 `ArgumentOutOfRangeException` 且文案逐字 |
| `tests/PiUnityAgent.Ai.Tests/SessionResourcesTests.cs` | 注册/清理传 sessionId；缺省 null；Dispose 注销（含重复 Dispose 幂等）；单回调抛异常不拦截其余并抛 `AggregateException`（文案逐字 + InnerExceptions 计数）；双失败聚合 2 个；零注册清理 no-op；断言按"全部执行"口径（Set 顺序不契约化） |

**门禁（2026-11-10 实跑，`955e7cc`）**：`dotnet build PiUnityAgent.slnx -c Release` 0 警告 0 错误；Ai 测试 1157/1157（含新增 24 用例）；Core 测试 96/96；`python scripts/check-deferred.py --current-stage S8 --gate exit` exit 0（四件不产生 Dn 行，registry 23 不变）。

---

## 5. 遗留 / 关注点

- uuidv7/session-resources 当前无内部消费方（pi 侧也仅 codex provider 暂缓消费）；overflow 由 2A 压缩消费（S11 承接）、StringEnum 供工具作者（S6 之后的工具开发用）。四件补齐的是**公共导出面**，不是新增调用面。
- `Overflow.GetOverflowPatterns()` 只导出主列表（pi 同口径，非溢出排除列表 pi 也未导出）。

---

## 6. 参考索引

| 内容 | pi 源 |
|---|---|
| 溢出判定 | `packages/ai/src/utils/overflow.ts`（模式表 :39-69、Cerebras :71、非溢出 :82-86、判定 :144-184） |
| 字符串枚举 schema | `packages/ai/src/utils/string-enum.ts`（24 行全量） |
| 时间序 UUID | `packages/ai/src/utils/uuid.ts`（45 行全量） |
| 会话资源清理 | `packages/ai/src/session-resources.ts`（24 行全量） |
| 导出面 | `packages/ai/src/index.ts:33/:39/:42/:45` |
| 消费点 | `packages/durable/src/harness/generation.ts:12`（overflow）；`packages/ai/src/api/openai-codex-responses.ts:9/:41`（session-resources/uuidv7） |
| 修复报告 | `.superpowers/sdd/m1-audit-code-fix-report.md`（§一 U-S1 / §四 偏离记录） |
