namespace PiUnityAgent.Core.Tools;

/// <summary>工具执行模式（pi ToolExecutionMode 字符串联合）：串行 / 可并行。</summary>
public enum ToolExecutionMode
{
    Sequential,
    Parallel,
}

/// <summary>工具恢复策略（pi AgentTool.replay）：永不重放 / 安全可重放。</summary>
public enum AgentToolReplay
{
    Never,
    Safe,
}
