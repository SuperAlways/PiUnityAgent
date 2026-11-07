using System;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Events;

public static class LazyStream
{
    /// <summary>
    /// 同步返回 outer 流；返回时立即启动 factory。factory 只启动一次。
    /// setup 失败（含 OCE）统一为 Error(Error)；Aborted 由 provider 内层流负责。
    /// 对应 pi api/lazy.ts 与 pi.net Api/Lazy.cs。
    /// </summary>
    public static IAssistantMessageEventStream Run(
        Model model,
        Func<Task<IAssistantMessageEventStream>> factory,
        TelemetrySpan? telemetry = null)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (factory is null) throw new ArgumentNullException(nameof(factory));

        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var outer = new AssistantMessageEventStream();

        // fire-and-forget：ForwardAsync 内部捕获所有异常并以终态事件收场。
        _ = ForwardAsync(outer, model, factory, startedAt);
        return outer;
    }

    private static async Task ForwardAsync(
        AssistantMessageEventStream outer,
        Model model,
        Func<Task<IAssistantMessageEventStream>> factory,
        long startedAt)
    {
        try
        {
            var inner = await factory().ConfigureAwait(false)
                ?? throw new InvalidOperationException("LazyStream factory returned null.");

            await foreach (var @event in inner.ConfigureAwait(false))
            {
                outer.Push(@event);
            }

            var final = await inner.WaitForDoneAsync().ConfigureAwait(false);
            outer.End(final);
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
            outer.Push(new AssistantMessageEvent.Error(StopReason.Error, message));
            outer.End(message);
        }
    }
}
