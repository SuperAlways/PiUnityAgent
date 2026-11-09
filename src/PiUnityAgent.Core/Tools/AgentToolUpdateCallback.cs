namespace PiUnityAgent.Core.Tools;

/// <summary>流式部分结果回调（pi AgentToolUpdateCallback）；execute() 结束后到达的调用由 S7 执行器忽略。</summary>
public delegate Task AgentToolUpdateCallback(AgentToolResult partialResult);
