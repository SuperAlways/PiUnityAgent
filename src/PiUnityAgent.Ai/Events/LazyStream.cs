using System;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Events;

public static class LazyStream
{
    /// <summary>
    /// 同步返回 outer 流；返回即调度 factory（Task.Run，与 pi.net Api/Lazy.cs 一致：
    /// setup 在同步返回之后异步运行，调用方拿到的瞬间 provider 尚未被调用）。
    /// factory 只启动一次。setup 失败（含 OCE）统一为 Error(Error)；Aborted 由 provider 内层流负责。
    /// 观察顺序按 pi api/lazy.ts：外层 Push 之后调 observer.OnEvent；外层 End 之后调 observer.OnResult；
    /// setup 失败同样先 Push→OnEvent(error 事件) 再 End→OnResult(错误终态消息，不是 null)，各只调用一次。
    /// 对应 pi api/lazy.ts 与 pi.net Api/Lazy.cs。
    /// </summary>
    public static IAssistantMessageEventStream Run(
        Model model,
        Func<Task<IAssistantMessageEventStream>> factory,
        ILazyStreamObserver? observer = null)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (factory is null) throw new ArgumentNullException(nameof(factory));

        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var outer = new AssistantMessageEventStream();

        // fire-and-forget：ForwardAsync 内部捕获所有异常并以终态事件收场。
        _ = Task.Run(() => ForwardAsync(outer, model, factory, startedAt, observer));
        return outer;
    }

    private static async Task ForwardAsync(
        AssistantMessageEventStream outer,
        Model model,
        Func<Task<IAssistantMessageEventStream>> factory,
        long startedAt,
        ILazyStreamObserver? observer)
    {
        try
        {
            var inner = await factory().ConfigureAwait(false)
                ?? throw new InvalidOperationException("LazyStream factory returned null.");

            await foreach (var @event in inner.ConfigureAwait(false))
            {
                outer.Push(@event);
                observer?.OnEvent(@event);
            }

            var final = await inner.WaitForDoneAsync().ConfigureAwait(false);
            outer.End(final);
            observer?.OnResult(final);
        }
        catch (Exception error)
        {
            var message = new AssistantMessage
            {
                Content = Array.Empty<ContentBlock>(),
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                Usage = new Usage(),
                StopReason = StopReason.Error,
                ErrorMessage = error.Message,
                Timestamp = startedAt,
            };
            // pi api/lazy.ts catch 顺序：push → observer.onEvent → end → observeResult。
            var errorEvent = new AssistantMessageEvent.Error(StopReason.Error, message);
            outer.Push(errorEvent);
            observer?.OnEvent(errorEvent);
            outer.End(message);
            observer?.OnResult(message);
        }
    }
}
