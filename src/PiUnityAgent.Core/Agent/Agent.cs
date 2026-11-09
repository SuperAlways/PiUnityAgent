using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// 有状态 agent 门面（pi agent.ts）。持有 transcript、双队列、运行生命周期与事件订阅。
/// 公开控制 API 可被 Unity 主线程与循环续体跨线程调用；同步状态段由 _gate 保护。
/// </summary>
public sealed partial class Agent
{
    private readonly object _gate = new();
    private readonly AgentState _state;
    private readonly List<Func<AgentEvent, CancellationToken, Task>> _listeners = new();
    private readonly PendingMessageQueue _steeringQueue;
    private readonly PendingMessageQueue _followUpQueue;
    private ActiveRun? _activeRun;

    public Agent(AgentOptions options)
    {
        options ??= new AgentOptions();
        _state = new AgentState(options.InitialState);
        ConvertToLlm = options.ConvertToLlm ?? DefaultConvertToLlm;
        TransformContext = options.TransformContext;
        StreamFunction = options.StreamFn ?? DefaultStreamFn.Get();
        GetApiKey = options.GetApiKey;
        BeforeToolCall = options.BeforeToolCall;
        AfterToolCall = options.AfterToolCall;
        FinishTurn = options.FinishTurn;
        PrepareRequest = options.PrepareRequest;
        PrepareNextTurn = options.PrepareNextTurn;
        PrepareNextTurnWithContext = options.PrepareNextTurnWithContext;
        SessionId = options.SessionId;
        ThinkingBudgets = options.ThinkingBudgets;
        Transport = options.Transport ?? TransportKind.Auto;
        MaxRetryDelayMs = options.MaxRetryDelayMs;
        ToolExecution = options.ToolExecution ?? ToolExecutionMode.Parallel;
        Identity = options.Identity;
        OnPayload = options.OnPayload;
        OnResponse = options.OnResponse;
        OnProviderStreamEvent = options.OnProviderStreamEvent;
        _steeringQueue = new PendingMessageQueue(options.SteeringMode);
        _followUpQueue = new PendingMessageQueue(options.FollowUpMode);
    }

    /// <summary>当前状态；对应 pi agent.state。</summary>
    public AgentState State => _state;

    public Func<IReadOnlyList<Message>, Task<IReadOnlyList<Message>>> ConvertToLlm { get; set; }
    public Func<IReadOnlyList<Message>, CancellationToken, Task<IReadOnlyList<Message>>>? TransformContext { get; set; }
    public StreamFn StreamFunction { get; set; }
    public Func<string, Task<string?>>? GetApiKey { get; set; }
    public BeforeToolCall? BeforeToolCall { get; set; }
    public AfterToolCall? AfterToolCall { get; set; }
    public FinishTurn? FinishTurn { get; set; }
    public PrepareRequest? PrepareRequest { get; set; }
    public Func<CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; set; }
    public PrepareNextTurnWithContext? PrepareNextTurnWithContext { get; set; }
    public string? SessionId { get; set; }
    public ThinkingBudgets? ThinkingBudgets { get; set; }
    public TransportKind Transport { get; set; }
    public int? MaxRetryDelayMs { get; set; }
    public ToolExecutionMode ToolExecution { get; set; }
    public ClientIdentity? Identity { get; set; }
    public Func<JsonNode?, ProviderModel, Task<JsonNode?>>? OnPayload { get; set; }
    public Func<ProviderResponse, ProviderModel, Task>? OnResponse { get; set; }
    public Func<JsonNode, ProviderModel, Task>? OnProviderStreamEvent { get; set; }

    /// <summary>按订阅顺序 await；返回取消订阅句柄。同一 listener 重复订阅只保留一个。</summary>
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener)
    {
        if (listener is null)
        {
            throw new ArgumentNullException(nameof(listener));
        }

        lock (_gate)
        {
            if (!_listeners.Contains(listener))
            {
                _listeners.Add(listener);
            }
        }
        return new Unsubscriber(this, listener);
    }

    /// <summary>当前运行的中止令牌；无运行时 null。</summary>
    public CancellationToken? Signal
    {
        get
        {
            lock (_gate)
            {
                return _activeRun?.Cancellation.Token;
            }
        }
    }

    /// <summary>中止当前运行（若有）。</summary>
    public void Abort()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _activeRun?.Cancellation;
        }
        if (cancellation is null)
        {
            return;
        }
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 完成竞态：快照与 Cancel 之间运行恰好收场，FinishRun 已销毁 CTS——已无可中止。
        }
    }

    /// <summary>等待当前运行与全部事件监听器结束（agent_end listener 结算后才完成）。</summary>
    public Task WaitForIdle()
    {
        lock (_gate)
        {
            return _activeRun?.Promise.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>清空对话状态与队列，仅保留折叠后的 system 基线。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_activeRun is not null)
            {
                throw new InvalidOperationException(
                    "Agent is already processing. Wait for completion before resetting.");
            }

            var baseline = AgentTranscript.GetCurrentSystemMessage(_state.Messages);
            _state.ReplaceMessages(baseline is null
                ? Array.Empty<Message>()
                : new Message[] { baseline });
            _state.ClearRuntime();
            _steeringQueue.Clear();
            _followUpQueue.Clear();
        }
    }

    private bool HasActiveRun()
    {
        lock (_gate) return _activeRun is not null;
    }

    private static Task<IReadOnlyList<Message>> DefaultConvertToLlm(IReadOnlyList<Message> messages)
        => Task.FromResult<IReadOnlyList<Message>>(messages
            .Where(message => message.Role is "system" or "user" or "assistant" or "toolResult")
            .ToList());

    private async Task RunWithLifecycle(Func<CancellationToken, Task> executor)
    {
        ActiveRun run;
        lock (_gate)
        {
            if (_activeRun is not null)
            {
                throw new InvalidOperationException("Agent is already processing.");
            }

            run = new ActiveRun
            {
                Cancellation = new CancellationTokenSource(),
                Promise = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _activeRun = run;
            _state.SetIsStreaming(true);
            _state.SetStreamingMessage(null);
            _state.SetErrorMessage(null);
        }

        try
        {
            await executor(run.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await HandleRunFailure(error, run.Cancellation.IsCancellationRequested)
                .ConfigureAwait(false);
        }
        finally
        {
            FinishRun(run);
        }
    }

    private async Task HandleRunFailure(Exception error, bool aborted)
    {
        var model = _state.Model;
        var failure = new AssistantMessage
        {
            Content = new ContentBlock[] { new TextContent { Text = string.Empty } },
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = aborted ? StopReason.Aborted : StopReason.Error,
            ErrorMessage = error.Message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await ProcessEvents(new AgentEvent.MessageStart(failure)).ConfigureAwait(false);
        await ProcessEvents(new AgentEvent.MessageEnd(failure)).ConfigureAwait(false);
        await ProcessEvents(new AgentEvent.TurnEnd(failure, Array.Empty<ToolResultMessage>()))
            .ConfigureAwait(false);
        await ProcessEvents(new AgentEvent.AgentEnd(new Message[] { failure }))
            .ConfigureAwait(false);
    }

    private void FinishRun(ActiveRun run)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeRun, run))
            {
                return;
            }
            _state.SetIsStreaming(false);
            _state.SetStreamingMessage(null);
            _state.ClearPendingToolCalls();
            _activeRun = null;
        }

        run.Cancellation.Dispose();
        run.Promise.TrySetResult(true);
    }

    private async Task ProcessEvents(AgentEvent @event)
    {
        lock (_gate)
        {
            switch (@event)
            {
                case AgentEvent.MessageStart start:
                    _state.SetStreamingMessage(start.Message);
                    break;
                case AgentEvent.MessageUpdate update:
                    _state.SetStreamingMessage(update.Message);
                    break;
                case AgentEvent.MessageEnd end:
                    _state.SetStreamingMessage(null);
                    _state.AddMessage(end.Message);
                    break;
                case AgentEvent.ToolExecutionStart start:
                    _state.AddPendingToolCall(start.ToolCallId);
                    break;
                case AgentEvent.ToolExecutionEnd end:
                    _state.RemovePendingToolCall(end.ToolCallId);
                    break;
                case AgentEvent.TurnEnd turn:
                    // 对齐 pi：errorMessage 真值才记录（空串不算错误，不覆盖既有错误信息）。
                    if (!string.IsNullOrEmpty(turn.Message.ErrorMessage))
                    {
                        _state.SetErrorMessage(turn.Message.ErrorMessage);
                    }
                    break;
                case AgentEvent.AgentEnd:
                    _state.SetStreamingMessage(null);
                    break;
            }
        }

        CancellationToken signal;
        Func<AgentEvent, CancellationToken, Task>[] listeners;
        lock (_gate)
        {
            if (_activeRun is null)
            {
                throw new InvalidOperationException("Agent listener invoked outside active run");
            }
            signal = _activeRun.Cancellation.Token;
            listeners = _listeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            await listener(@event, signal).ConfigureAwait(false);
        }
    }

    private sealed class ActiveRun
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public required TaskCompletionSource<bool> Promise { get; init; }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Agent? _owner;
        private readonly Func<AgentEvent, CancellationToken, Task> _listener;

        public Unsubscriber(Agent owner, Func<AgentEvent, CancellationToken, Task> listener)
        {
            _owner = owner;
            _listener = listener;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }
            lock (owner._gate) owner._listeners.Remove(_listener);
        }
    }
}
