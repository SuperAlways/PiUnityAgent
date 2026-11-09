using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Core.Tools;

/// <summary>
/// Agent 运行时工具定义（pi <c>AgentTool</c>）。工具声明四字段与 Ai <see cref="Tool"/> 同义，
/// 由 <see cref="Definition"/> 单点投影；Execute 失败可抛异常或返回 <c>IsError=true</c> 的结果。
/// </summary>
public sealed record AgentTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>JSON Schema 对象（模型声明 + 运行时校验共用）。对应 pi Tool.parameters。</summary>
    public required JsonObject Parameters { get; init; }

    /// <summary>UI 显示名（与只给模型的 Description 分受众）。对应 pi AgentTool.label。</summary>
    public required string Label { get; init; }

    /// <summary>provider 侧约束采样；对应 pi Tool.constrainedSampling（以 Disabled 变体表达 false；null 表未指定，比 pi 的 false｜Config 多一档区分）。</summary>
    public ConstrainedSampling? ConstrainedSampling { get; init; }

    /// <summary>校验前的参数兼容垫片；入参为原始 JsonObject，返回待校验值。对应 pi prepareArguments。</summary>
    public Func<JsonObject, JsonNode?>? PrepareArguments { get; init; }

    /// <summary>structuredContent 的 JSON Schema（声明性）。对应 pi AgentTool.outputSchema。</summary>
    public JsonObject? OutputSchema { get; init; }

    /// <summary>执行入口。对应 pi AgentTool.execute（signal→CancellationToken）。</summary>
    public required Func<string, JsonNode?, CancellationToken, AgentToolUpdateCallback?, Task<AgentToolResult>> Execute { get; init; }

    /// <summary>持久意图存在但结果未知时的恢复策略。对应 pi AgentTool.replay。</summary>
    public AgentToolReplay? Replay { get; init; }

    /// <summary>单工具执行模式覆盖；缺省走批次默认。对应 pi AgentTool.executionMode。</summary>
    public ToolExecutionMode? ExecutionMode { get; init; }

    /// <summary>模型可调用的声明视图（供 S9/transcript 使用）。</summary>
    public Tool Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = Parameters,
        ConstrainedSampling = ConstrainedSampling,
    };
}
