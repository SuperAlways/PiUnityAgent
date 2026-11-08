using PiUnityAgent.Ai.Events;

namespace PiUnityAgent.Ai.Types;

/// <summary>pi StreamFunction 的同步 C# 等价物；失败必须编码为流内终态，不抛。</summary>
public delegate IAssistantMessageEventStream StreamFn(
    Model model,
    TranscriptContext context,
    StreamOptions? options);

/// <summary>pi StreamSimpleFunction 的同步 C# 等价物；简单流入口的 provider 侧委托。</summary>
public delegate IAssistantMessageEventStream SimpleStreamFn(
    Model model,
    TranscriptContext context,
    SimpleStreamOptions? options);
