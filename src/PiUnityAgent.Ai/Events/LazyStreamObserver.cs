using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Events;

/// <summary>
/// pi LazyStreamObserver：LazyStream 的事件/终态后置观察面。
/// 顺序按 pi api/lazy.ts：OnEvent 在 outer.Push 之后，OnResult 在 outer.End 之后。
/// </summary>
public interface ILazyStreamObserver
{
    void OnEvent(AssistantMessageEvent @event);
    void OnResult(AssistantMessage? result);
}

// TODO(后续扩展, D8): 遥测（pi TelemetrySpan/observeModelRequest）以 ILazyStreamObserver 包装接入 LazyStream.Run 第三参。
