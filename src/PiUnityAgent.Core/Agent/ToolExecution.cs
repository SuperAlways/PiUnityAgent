using System.Diagnostics;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Ai.Utils;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// 工具批执行。对应 TS <c>executeToolCalls / executeToolCallsSequential /
/// executeToolCallsParallel / prepareToolCall / finalizeExecutedToolCall</c>。
/// <para>语义要点：预检（参数处理 + 校验 + beforeToolCall）始终顺序执行，期间被阻断/中止的
/// 调用立即发出 <c>tool_execution_end</c>；并行模式允许通过预检的工具并发执行——
/// <c>tool_execution_end</c> 按完成序发出，而 tool-result 消息事件按助手消息中的源顺序发出；
/// 每个调用处理完后检查取消（顺序批跳过后续调用，并行预检停止排队）；批提前终止仅在
/// 批内全部终结结果的 <c>Terminate</c> 均为 true 时生效。</para>
/// </summary>
public static partial class ToolExecution
{
    internal sealed record ExecutedToolCallBatch(List<ToolResultMessage> Messages, bool Terminate);

    /// <summary>
    /// 按配置的模式分发执行。
    /// </summary>
    internal static async Task<ExecutedToolCallBatch> ExecuteToolCalls(
        AgentContext context,
        AssistantMessage assistantMessage,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit)
    {
        var toolCalls = assistantMessage.Content.OfType<ToolCall>().ToList();
        var hasSequentialToolCall = toolCalls.Any(tc =>
            (context.Tools ?? Array.Empty<AgentTool>())
                .FirstOrDefault(t => t.Name == tc.Name) is { ExecutionMode: ToolExecutionMode.Sequential });

        if (config.ToolExecution == ToolExecutionMode.Sequential || hasSequentialToolCall)
            return await ExecuteSequential(context, assistantMessage, toolCalls, config, signal, emit).ConfigureAwait(false);
        return await ExecuteParallel(context, assistantMessage, toolCalls, config, signal, emit).ConfigureAwait(false);
    }

    /// <summary>
    /// 单次工具调用走与模型发起调用完全相同的管线：参数准备、schema 准备、
    /// <c>beforeToolCall</c>、执行、<c>afterToolCall</c>。对应 TS <c>runToolCall</c>
    /// （agent-loop.ts）。不发事件、不加消息——供「工具再调工具」（ctx.executeTool）使用，
    /// 使钩子（如权限检查）对那些调用同样生效。
    /// <para><paramref name="tools"/> 是调用解析所针对的工具集，独立于
    /// <paramref name="context"/>.Tools（pi <c>RunToolCallOptions.tools</c> 必填、独立于
    /// <c>context.tools</c>）——调用方可针对与 context 不同的工具集解析调用。
    /// 工具失败永不抛错：未知工具、参数错误、被阻断、执行抛异常（含取消的
    /// <c>OperationCanceledException</c>）都以 <c>IsError = true</c> 返回。
    /// <paramref name="onUpdate"/> 的流式部分结果直接转发给调用方
    /// （TS 同款：runToolCall 不产生事件，onUpdate 由调用方决定去向）。</para>
    /// </summary>
    public static async Task<AgentToolCallOutcome> RunToolCallAsync(
        ToolCall toolCall,
        IReadOnlyList<AgentTool> tools,
        AssistantMessage assistantMessage,
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var prepared = await PrepareToolCall(tools, context, assistantMessage, toolCall, config, signal)
            .ConfigureAwait(false);
        if (prepared is PreparedCall.Blocked blocked)
        {
            return new AgentToolCallOutcome(toolCall, blocked.Result, blocked.Result.IsError);
        }

        if (prepared is not PreparedCall.Ready ready)
        {
            throw new InvalidOperationException("Unknown prepared call");
        }

        var executed = await ExecutePrepared(ready, signal, onUpdate).ConfigureAwait(false);
        return await Finalize(executed.Result, executed.DurationMs, ready, context, assistantMessage, config, signal)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 输出被 token 上限截断的助手消息中的全部工具调用一律报错：
    /// 流式参数经 JSON 抢救解析后可能"看似完整实则截断"，不可执行。
    /// 对应 TS <c>failToolCallsFromTruncatedMessage</c>。
    /// </summary>
    internal static async Task<ExecutedToolCallBatch> FailToolCallsFromTruncatedMessage(
        IReadOnlyList<ToolCall> toolCalls, AgentEventSink emit)
    {
        var messages = new List<ToolResultMessage>();
        foreach (var toolCall in toolCalls)
        {
            await emit(new AgentEvent.ToolExecutionStart(toolCall.Id, toolCall.Name, toolCall.Arguments)).ConfigureAwait(false);
            var outcome = new AgentToolCallOutcome(toolCall, AgentToolResult.FromError(
                $"Tool call \"{toolCall.Name}\" was not executed: the response hit the output token limit, "
                + "so its arguments may be truncated. Re-issue the tool call with complete arguments."), true);
            await emit(new AgentEvent.ToolExecutionEnd(toolCall.Id, toolCall.Name, outcome.Result, true, null)).ConfigureAwait(false);
            var message = CreateToolResultMessage(outcome);
            await emit(new AgentEvent.MessageStart(message)).ConfigureAwait(false);
            await emit(new AgentEvent.MessageEnd(message)).ConfigureAwait(false);
            messages.Add(message);
        }
        return new ExecutedToolCallBatch(messages, Terminate: false);
    }

    // ---------- 顺序执行 ----------

    private static async Task<ExecutedToolCallBatch> ExecuteSequential(
        AgentContext context,
        AssistantMessage assistantMessage,
        IReadOnlyList<ToolCall> toolCalls,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit)
    {
        var messages = new List<ToolResultMessage>();
        var finalized = new List<AgentToolCallOutcome>();
        foreach (var toolCall in toolCalls)
        {
            var outcome = await ExecuteOne(context, assistantMessage, toolCall, config, signal, emit).ConfigureAwait(false);
            finalized.Add(outcome);
            // 顺序模式：每个调用终结后立即发出 end 事件。
            await emit(new AgentEvent.ToolExecutionEnd(
                outcome.ToolCall.Id, outcome.ToolCall.Name, outcome.Result, outcome.IsError, outcome.DurationMs))
                .ConfigureAwait(false);
            var message = CreateToolResultMessage(outcome);
            await emit(new AgentEvent.MessageStart(message)).ConfigureAwait(false);
            await emit(new AgentEvent.MessageEnd(message)).ConfigureAwait(false);
            messages.Add(message);
            // 已取消则不再执行后续调用（TS 585-587）。
            if (signal.IsCancellationRequested) break;
        }
        return new ExecutedToolCallBatch(messages, ShouldTerminateBatch(finalized));
    }

    /// <summary>顺序执行单个调用：预检 → 执行 → 终结（start 事件在预检前发出）。</summary>
    private static async Task<AgentToolCallOutcome> ExecuteOne(
        AgentContext context,
        AssistantMessage assistantMessage,
        ToolCall toolCall,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit)
    {
        await emit(new AgentEvent.ToolExecutionStart(toolCall.Id, toolCall.Name, toolCall.Arguments)).ConfigureAwait(false);
        var prepared = await PrepareToolCall(
            context.Tools ?? Array.Empty<AgentTool>(), context, assistantMessage, toolCall, config, signal)
            .ConfigureAwait(false);
        return prepared switch
        {
            PreparedCall.Blocked blocked => new AgentToolCallOutcome(toolCall, blocked.Result, blocked.Result.IsError),
            PreparedCall.Ready ready => await ExecuteAndFinalize(ready, context, assistantMessage, config, signal, emit).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown prepared call"),
        };
    }

    // ---------- 并行执行 ----------

    /// <summary>并行槽位：预检即终结（immediate，end 已内联发出）或待并发执行的 ready。</summary>
    private readonly record struct ParallelSlot(AgentToolCallOutcome? Immediate, PreparedCall.Ready? Prepared)
    {
        public static ParallelSlot Ended(AgentToolCallOutcome outcome) => new(outcome, null);
        public static ParallelSlot Pending(PreparedCall.Ready prepared) => new(null, prepared);
    }

    private static async Task<ExecutedToolCallBatch> ExecuteParallel(
        AgentContext context,
        AssistantMessage assistantMessage,
        IReadOnlyList<ToolCall> toolCalls,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit)
    {
        // 阶段一：顺序预检。immediate（未知/校验失败/阻断/中止）立即发出 end 事件并入槽
        // （TS 615-624）；ready 记槽待阶段二并发启动（TS 在 Promise.all 才真正起跑，等价）。
        var slots = new List<ParallelSlot>(toolCalls.Count);
        foreach (var toolCall in toolCalls)
        {
            await emit(new AgentEvent.ToolExecutionStart(toolCall.Id, toolCall.Name, toolCall.Arguments)).ConfigureAwait(false);
            var prepared = await PrepareToolCall(
                context.Tools ?? Array.Empty<AgentTool>(), context, assistantMessage, toolCall, config, signal)
                .ConfigureAwait(false);
            if (prepared is PreparedCall.Blocked blocked)
            {
                var immediate = new AgentToolCallOutcome(blocked.ToolCall, blocked.Result, blocked.Result.IsError);
                await emit(new AgentEvent.ToolExecutionEnd(
                    immediate.ToolCall.Id, immediate.ToolCall.Name, immediate.Result, immediate.IsError, immediate.DurationMs))
                    .ConfigureAwait(false);
                slots.Add(ParallelSlot.Ended(immediate));
            }
            else
            {
                slots.Add(ParallelSlot.Pending((PreparedCall.Ready)prepared));
            }
            // 已取消则不再排队后续调用（TS 623-625）；被跳过的调用整条丢弃（无消息产出）。
            if (signal.IsCancellationRequested) break;
        }

        // 阶段二：并发执行 ready 槽位；thunk 入口先查取消——中止则不执行工具，
        // 产出 "Operation aborted" 错误结局（TS 630-637）。
        var tasks = new List<Task<(int Index, AgentToolCallOutcome Outcome)>>();
        for (var index = 0; index < slots.Count; index++)
        {
            if (slots[index].Prepared is not { } prepared) continue;
            var currentIndex = index;
            var current = prepared;
            tasks.Add(Task.Run(async () =>
            {
                AgentToolCallOutcome outcome;
                if (signal.IsCancellationRequested)
                {
                    outcome = new AgentToolCallOutcome(
                        current.ToolCall, AgentToolResult.FromError("Operation aborted"), true);
                }
                else
                {
                    outcome = await ExecuteAndFinalize(current, context, assistantMessage, config, signal, emit)
                        .ConfigureAwait(false);
                }
                return (currentIndex, outcome);
            }, CancellationToken.None));
        }

        // 阶段三：end 按完成序收割。thunk 的 end 统一在收割循环串行发出（TS 在 thunk 内发出，
        // 完成序一致；串行发射保证事件汇不被并发调用）。
        var completed = new Dictionary<int, AgentToolCallOutcome>();
        var pendingTasks = new List<Task<(int Index, AgentToolCallOutcome Outcome)>>(tasks);
        while (pendingTasks.Count > 0)
        {
            var finished = await Task.WhenAny(pendingTasks.ToArray()).ConfigureAwait(false);
            pendingTasks.Remove(finished);
            // await 解包原异常：工具执行/终结的抛错已 catch-all 为错误结果（含 OCE），
            // 此处仅剩事件汇/基础设施异常沿原样传播。
            var (index, outcome) = await finished.ConfigureAwait(false);
            completed[index] = outcome;
            await emit(new AgentEvent.ToolExecutionEnd(
                outcome.ToolCall.Id, outcome.ToolCall.Name, outcome.Result, outcome.IsError, outcome.DurationMs))
                .ConfigureAwait(false);
        }

        // 阶段四：tool-result 消息按助手消息源顺序合成并发出（仅已入槽调用）。
        var messages = new List<ToolResultMessage>();
        var finalized = new List<AgentToolCallOutcome>();
        for (var index = 0; index < slots.Count; index++)
        {
            var outcome = slots[index].Immediate ?? completed[index];
            finalized.Add(outcome);
            var message = CreateToolResultMessage(outcome);
            await emit(new AgentEvent.MessageStart(message)).ConfigureAwait(false);
            await emit(new AgentEvent.MessageEnd(message)).ConfigureAwait(false);
            messages.Add(message);
        }
        return new ExecutedToolCallBatch(messages, ShouldTerminateBatch(finalized));
    }

    // ---------- 预检结果 ----------

    /// <summary>预检结果的判别联合：可执行 / 被阻断。</summary>
    private abstract record PreparedCall
    {
        private PreparedCall() { }

        public sealed record Ready(ToolCall ToolCall, AgentTool Tool, JsonNode? Args) : PreparedCall;

        public sealed record Blocked(ToolCall ToolCall, AgentToolResult Result, bool Terminate) : PreparedCall;
    }

    /// <summary>中止产出：不执行工具，错误文案对齐 TS 的 "Operation aborted"。</summary>
    private static PreparedCall.Blocked AbortedCall(ToolCall toolCall)
        => new(toolCall, AgentToolResult.FromError("Operation aborted"), false);

    /// <summary>
    /// 预检：在 <paramref name="tools"/> 中查找工具 → prepareArguments 垫片 → Schema 校验 →
    /// beforeToolCall（可阻断）→ 取消探测。对应 TS prepareToolCall（agent-loop.ts:727-787）：
    /// 垫片/校验/钩子抛错一律立即以原始错误消息终结（不执行）；钩子返回后与返回 ready 前
    /// 各探测一次取消，中止产出 "Operation aborted"（不执行）。
    /// </summary>
    private static async Task<PreparedCall> PrepareToolCall(
        IReadOnlyList<AgentTool> tools,
        AgentContext context,
        AssistantMessage assistantMessage,
        ToolCall toolCall,
        AgentLoopConfig config,
        CancellationToken signal)
    {
        var tool = tools.FirstOrDefault(t => t.Name == toolCall.Name);
        if (tool is null)
            return new PreparedCall.Blocked(toolCall, AgentToolResult.FromError($"Tool {toolCall.Name} not found"), false);

        try
        {
            // 参数兼容垫片。ToolCall.Arguments 是 JsonObject（恒非空）；垫片可返回任意节点或 null，
            // 统一归一为 JsonObject（与 TS 的 validatedArgs 一致，校验前准备好）。
            var preparedArgs = tool.PrepareArguments is not null
                ? tool.PrepareArguments(toolCall.Arguments)
                : toolCall.Arguments;
            preparedArgs ??= new JsonObject();
            var preparedArguments = (JsonObject)preparedArgs;

            // Schema 校验（上游 validateToolArguments）。校验在 beforeToolCall 之前，
            // 失败即以 isError 结果回给模型；「模型给的参数文本不是合法 JSON」由
            // argumentsError 短路，绝不让工具带着残缺字段执行（上游 #10897）。
            // 工具拿到的是强制类型转换后的参数，与 TS 的 validatedArgs 一致。
            var args = ToolValidation.ValidateToolArguments(tool.Definition, toolCall with { Arguments = preparedArguments });

            if (config.BeforeToolCall is not null)
            {
                var decision = await config.BeforeToolCall(
                    new BeforeToolCallContext(assistantMessage, toolCall, args, context), signal).ConfigureAwait(false);
                if (signal.IsCancellationRequested)
                    return AbortedCall(toolCall);
                if (decision?.Block == true)
                {
                    // pi `beforeResult.reason || "Tool execution was blocked"`：空串同 null 回退默认文案。
                    var reason = string.IsNullOrEmpty(decision.Reason) ? "Tool execution was blocked" : decision.Reason;
                    // TS 把 terminate 写在错误结果本体上（result.terminate = true），否则
                    // blocked 分支构造 outcome 时会把它丢掉。AgentToolResult.Terminate 是
                    // init-only，用 with 落到结果上；Blocked 记录同时保留该旗标。
                    var terminate = decision.Terminate == true;
                    return new PreparedCall.Blocked(
                        toolCall, AgentToolResult.FromError(reason) with { Terminate = terminate }, terminate);
                }
            }

            if (signal.IsCancellationRequested)
                return AbortedCall(toolCall);

            return new PreparedCall.Ready(toolCall, tool, args);
        }
        catch (Exception error)
        {
            // TS prepareToolCall catch-all：垫片/校验/钩子抛错一律立即终结，错误消息原样给模型。
            // 取消（OperationCanceledException）同样落错误结果——不沿管线 rethrow（那会让取消
            // 变成 stream.Fail，丢掉 turn_end/agent_end 与批内已完成结果；pi 无此路径）。
            return new PreparedCall.Blocked(toolCall, AgentToolResult.FromError(error.Message), false);
        }
    }

    // ---------- 执行 / 终结 ----------

    private static async Task<AgentToolCallOutcome> ExecuteAndFinalize(
        PreparedCall.Ready prepared,
        AgentContext context,
        AssistantMessage assistantMessage,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit)
    {
        var executed = await ExecutePrepared(prepared, signal, EmitToolExecutionUpdate(prepared.ToolCall, emit))
            .ConfigureAwait(false);
        return await Finalize(executed.Result, executed.DurationMs, prepared, context, assistantMessage, config, signal)
            .ConfigureAwait(false);
    }

    /// <summary>执行结果 + 单调时钟测得的耗时（毫秒）。对应 TS <c>ExecutedToolCallOutcome</c>。</summary>
    private sealed record ExecutedToolResult(AgentToolResult Result, long DurationMs);

    /// <summary>
    /// 流式部分结果转发为 tool_execution_update 事件（对齐 TS 的 emitToolExecutionUpdate）。
    /// args 用原始调用参数（TS 同款：toolCall.arguments，非校验后值）。
    /// </summary>
    private static AgentToolUpdateCallback EmitToolExecutionUpdate(ToolCall toolCall, AgentEventSink emit)
        => partial => emit(new AgentEvent.ToolExecutionUpdate(
            toolCall.Id, toolCall.Name, toolCall.Arguments, partial));

    /// <summary>
    /// 执行已预检的工具调用。对应 TS <c>executePreparedToolCall</c>：
    /// onUpdate 收集为待办并在 execute 结算后统一等待；结算（含失败/取消）之后到达的
    /// 部分结果被 <c>acceptingUpdates</c> 丢弃；耗时在 execute 结算时即测得、先于等待
    /// 待办更新（TS 856 顺序）。抛错（含 <c>OperationCanceledException</c>）走 pi 的
    /// catch-all：一律落 <c>IsError</c> 结果，耗时保留、待办更新仍等待——取消不沿管线
    /// rethrow（TS 无此路径；C# 遵守 CT 的工具抛 OCE 也应是模型可见的工具失败）。
    /// netstandard2.1 无 <c>Stopwatch.GetElapsedTime</c>（.NET 7+），以 <c>Stopwatch</c>
    /// 实例等价计时。
    /// </summary>
    private static async Task<ExecutedToolResult> ExecutePrepared(
        PreparedCall.Ready prepared,
        CancellationToken signal,
        AgentToolUpdateCallback? onUpdate)
    {
        var stopwatch = Stopwatch.StartNew();
        long Elapsed() => (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds);
        var updateEvents = new List<Task>();
        // 工具可能从多线程并发回调 onUpdate，入队须互斥（守卫读为尽力而为，与 TS 一致）。
        var updateEventsLock = new object();
        var acceptingUpdates = true;
        try
        {
            var result = await prepared.Tool.Execute(
                prepared.ToolCall.Id, prepared.Args, signal,
                onUpdate is null
                    ? null
                    : partial =>
                    {
                        if (!acceptingUpdates) return Task.CompletedTask;
                        var pendingUpdate = onUpdate(partial);
                        lock (updateEventsLock) updateEvents.Add(pendingUpdate);
                        return pendingUpdate;
                    }).ConfigureAwait(false);
            var durationMs = Elapsed();
            acceptingUpdates = false;
            await AwaitUpdates(updateEvents, updateEventsLock).ConfigureAwait(false);
            return new ExecutedToolResult(result, durationMs);
        }
        catch (Exception error)
        {
            // 抛出的错误视为失败结果（模型可见文本，UI 可读异常）；耗时先于等待更新测得（TS 856）。
            // OperationCanceledException 同样走此路径（pi catch-all）：取消是工具失败而非管线异常，
            // 中止路径由显式探测产出 "Operation aborted"（见 PrepareToolCall / 并行 thunk）。
            var durationMs = Elapsed();
            acceptingUpdates = false;
            await AwaitUpdates(updateEvents, updateEventsLock).ConfigureAwait(false);
            return new ExecutedToolResult(AgentToolResult.FromError(error.Message), durationMs);
        }
        finally
        {
            acceptingUpdates = false;
        }
    }

    /// <summary>快照并等待全部待办更新（入队与快照共用一把锁）。</summary>
    private static async Task AwaitUpdates(List<Task> updateEvents, object updateEventsLock)
    {
        Task[] pending;
        lock (updateEventsLock) pending = updateEvents.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    /// <summary>应用 afterToolCall 覆盖并产出最终结果。对应 TS <c>finalizeExecutedToolCall</c>。</summary>
    private static async Task<AgentToolCallOutcome> Finalize(
        AgentToolResult executed,
        long durationMs,
        PreparedCall.Ready prepared,
        AgentContext context,
        AssistantMessage assistantMessage,
        AgentLoopConfig config,
        CancellationToken signal)
    {
        var result = executed;
        var isError = executed.IsError;

        if (config.AfterToolCall is not null)
        {
            try
            {
                var patch = await config.AfterToolCall(
                    new AfterToolCallContext(assistantMessage, prepared.ToolCall, prepared.Args, result, isError, context),
                    signal).ConfigureAwait(false);
                if (patch is not null)
                {
                    // 逐字段覆盖：提供即替换，省略即保留原值；无深合并。
                    // isError 只进 outcome，不写回结果本体（TS 同款：spread 不带 isError）。
                    result = result with
                    {
                        Content = patch.Content ?? result.Content,
                        Details = patch.Details ?? result.Details,
                        Usage = patch.Usage ?? result.Usage,
                        Terminate = patch.Terminate ?? result.Terminate,
                    };
                    if (patch.StructuredContent is not null)
                        result = result with { StructuredContent = patch.StructuredContent };
                    // content 被替换而未提供 structuredContent 时，结构化内容失效（可能不再匹配）。
                    else if (patch.Content is not null)
                        result = result with { StructuredContent = null };
                    isError = patch.IsError ?? isError;
                }
            }
            catch (Exception error)
            {
                // 钩子自身抛错也按工具失败回给模型（TS 同款），不让循环崩在钩子上。
                result = AgentToolResult.FromError(error.Message);
                isError = true;
            }
        }

        return new AgentToolCallOutcome(prepared.ToolCall, result, isError) { DurationMs = durationMs };
    }

    /// <summary>批提前终止：所有终结结果均要求终止才生效。</summary>
    private static bool ShouldTerminateBatch(IReadOnlyList<AgentToolCallOutcome> finalized)
        => finalized.Count > 0 && finalized.All(o => o.Result.Terminate);

    /// <summary>把工具结果合成为 toolResult 消息（源顺序；usage/details 随行，TS createToolResultMessage）。</summary>
    private static ToolResultMessage CreateToolResultMessage(AgentToolCallOutcome outcome)
        => new()
        {
            ToolCallId = outcome.ToolCall.Id,
            ToolName = outcome.ToolCall.Name,
            Content = outcome.Result.Content,
            Details = outcome.Result.Details,
            Usage = outcome.Result.Usage,
            IsError = outcome.IsError,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DurationMs = outcome.DurationMs is { } duration ? (int)Math.Min(int.MaxValue, duration) : null,
        };
}
