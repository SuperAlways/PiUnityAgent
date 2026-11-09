using System.Threading.Tasks;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Providers.Auth;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed class FauxProvider
{
    private readonly object _gate = new();
    private readonly Queue<FauxResponseStep> _responses = new();
    private readonly object _deferredGate = new();
    private readonly Dictionary<string, DeferredEntry> _deferredResponses = new(StringComparer.Ordinal);
    private readonly FauxProviderOptions _options;
    private readonly IClock _clock;
    private readonly FauxProviderState _state = new();

    public FauxProvider(FauxProviderOptions? options = null)
    {
        _options = options ?? new FauxProviderOptions();
        _clock = _options.Clock ?? new SystemClock();
        var definitions = _options.Models is { Count: > 0 }
            ? _options.Models
            : new[]
            {
                new FauxModelDefinition
                {
                    Id = Faux.DefaultModelId,
                    Name = Faux.DefaultModelName,
                    Input = new[] { ModelInputModality.Text, ModelInputModality.Image },
                },
            };

        Models = definitions.Select(definition => new Model
        {
            Id = definition.Id,
            Name = definition.Name ?? definition.Id,
            Api = _options.Api,
            Provider = _options.Provider,
            BaseUrl = Faux.DefaultBaseUrl,
            Input = definition.Input ?? new[] { ModelInputModality.Text, ModelInputModality.Image },
            InputLimits = definition.InputLimits,
            Cost = definition.Cost ?? new ModelCost(),
            Reasoning = definition.Reasoning,
            ContextWindow = definition.ContextWindow,
            MaxTokens = definition.MaxTokens,
        }).ToList();

        Provider = new StaticProvider(
            _options.Provider,
            _options.Provider,
            Models,
            Stream,
            baseUrl: Faux.DefaultBaseUrl,
            auth: new ProviderAuth
            {
                ApiKey = new ApiKeyAuth
                {
                    Name = "Faux",
                    Resolve = (_, _) => Task.FromResult<AuthResult?>(new AuthResult(new ModelAuth(), null, "Faux")),
                },
            },
            fetchDeferred: FetchDeferredStream,
            cancelDeferred: CancelDeferredAsync);
    }

    public IProvider Provider { get; }
    public IReadOnlyList<Model> Models { get; }
    public FauxProviderState State => _state;

    public int PendingResponseCount
    {
        get { lock (_gate) return _responses.Count; }
    }

    public void SetResponses(IEnumerable<FauxResponseStep> steps)
    {
        lock (_gate)
        {
            _responses.Clear();
            foreach (var step in steps) _responses.Enqueue(step);
        }
    }

    public void AppendResponses(IEnumerable<FauxResponseStep> steps)
    {
        lock (_gate)
        {
            foreach (var step in steps) _responses.Enqueue(step);
        }
    }

    public Model GetModel(string? id = null)
        => id is null
            ? Models[0]
            : Models.FirstOrDefault(m => m.Id == id)
              ?? throw new KeyNotFoundException($"Faux model '{id}' not found.");

    private IAssistantMessageEventStream Stream(
        Model model,
        TranscriptContext context,
        StreamOptions? options)
    {
        FauxResponseStep? step;
        lock (_gate) step = _responses.Count > 0 ? _responses.Dequeue() : null;
        _state.IncrementCallCount();

        var stream = new AssistantMessageEventStream();
        var simpleOptions = options as SimpleStreamOptions;
        var cancellationToken = options?.CancellationToken ?? default;

        // fire-and-forget 安全：EmitAsync 所有抛错路径都在内部捕获并以终态事件收场。
        _ = EmitAsync(stream, step, model, context, simpleOptions, cancellationToken);
        return stream;
    }

    private async Task EmitAsync(
        AssistantMessageEventStream stream,
        FauxResponseStep? step,
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (step is null)
            {
                EmitError(stream, model, "No more faux responses queued");
                return;
            }

            // Deferred 请求：不解析最终消息，把响应步骤存档后发射 Deferred 终态（handle +
            // 空 content）；最终结果延迟到 FetchDeferredAsync 时再解析/缓存。
            if (options?.Deferred is DeferredRequest.Enabled)
            {
                var handle = CreateDeferredHandle(model);
                StoreDeferred(handle, step, context, options, model);
                await EmitMessageAsync(stream, CreateDeferredMessage(model, handle), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            AssistantMessage message;
            if (step is FauxResponseStep.Factory factory)
            {
                message = await factory.Create(context, options, _state, model).ConfigureAwait(false);
            }
            else
            {
                message = ((FauxResponseStep.Response)step).Message;
            }

            var final = NormalizeFinal(model, context, message);

            await EmitMessageAsync(stream, final, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 工厂等待期（或发射前）取消：尚无任何消息存在，产出空内容的 Aborted 终态；
            // 时间戳取 _clock 而非 0。
            EmitAborted(stream, model, "Request was aborted");
        }
        catch (Exception error)
        {
            EmitError(stream, model, error.Message);
        }
    }

    private static Usage EstimateUsage(TranscriptContext context, AssistantMessage message)
    {
        // spec §6.8：输入 = 全部上下文消息（system/user/assistant/toolResult）的文本块
        // 长度之和 / 4，向上取整（对齐 pi 的 Math.ceil）；输出 = assistant 文本长度 / 4。
        var inputLength = context.Messages.Sum(m => m switch
        {
            SystemMessage system => TextLength(system.Content.ToBlocks()),
            UserMessage user => TextLength(user.Content.ToBlocks()),
            AssistantMessage assistant => TextLength(assistant.Content),
            ToolResultMessage toolResult => TextLength(toolResult.Content),
            _ => 0,
        });
        var outputLength = message.Content.OfType<TextContent>().Sum(t => t.Text.Length);
        var input = (int)Math.Ceiling(inputLength / 4.0);
        var output = (int)Math.Ceiling(outputLength / 4.0);
        return new Usage
        {
            Input = input,
            Output = output,
            TotalTokens = input + output,
        };
    }

    private static int TextLength(IReadOnlyList<ContentBlock> blocks)
        => blocks.OfType<TextContent>().Sum(t => t.Text.Length);

    private static void EmitError(AssistantMessageEventStream stream, Model model, string message)
        => EmitError(stream, new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = StopReason.Error,
            ErrorMessage = message,
            Timestamp = 0,
        }, message);

    private static void EmitError(AssistantMessageEventStream stream, AssistantMessage message, string text)
    {
        var assistant = message with
        {
            Content = Array.Empty<ContentBlock>(),
            StopReason = StopReason.Error,
            ErrorMessage = text,
        };
        stream.Push(new AssistantMessageEvent.Error(StopReason.Error, assistant));
        stream.End(assistant);
    }

    private void EmitAborted(AssistantMessageEventStream stream, Model model, string text)
    {
        var assistant = new AssistantMessage
        {
            Content = Array.Empty<ContentBlock>(),
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Usage = new Usage(),
            StopReason = StopReason.Aborted,
            ErrorMessage = text,
            Timestamp = _clock.UtcNowMs(),
        };
        stream.Push(new AssistantMessageEvent.Error(StopReason.Aborted, assistant));
        stream.End(assistant);
    }

    private async Task EmitMessageAsync(
        AssistantMessageEventStream stream,
        AssistantMessage final,
        CancellationToken cancellationToken)
    {
        // 预取消在 Start 之前检查：预取消的 token 产出仅 [Error]（无 Start）。
        cancellationToken.ThrowIfCancellationRequested();

        // 发射前规范化 ToolCall id：空/缺省 id 用 provider 单调计数器补 `tool:{n}`；
        // 此后 partials、ToolCallEnd、终态消息一律使用规范化后的消息。
        final = NormalizeToolCallIds(final);

        var partial = final with
        {
            Content = Array.Empty<ContentBlock>(),
            StopReason = StopReason.Pending,
        };
        stream.Push(new AssistantMessageEvent.Start(partial));

        var built = new List<ContentBlock>();
        try
        {
            foreach (var block in final.Content)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (block)
                {
                    case TextContent text:
                        await EmitTextAsync(stream, final, built, text, cancellationToken).ConfigureAwait(false);
                        break;
                    case ThinkingContent thinking:
                        await EmitThinkingAsync(stream, final, built, thinking, cancellationToken).ConfigureAwait(false);
                        break;
                    case ToolCall call:
                        await EmitToolCallAsync(stream, final, built, call, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"faux streamWithDeltas does not support {block.GetType().Name}.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 中途取消：从流上最新 Partial 快照构造 Aborted 终态（快照缺省回落到 final），
            // 保留已累积内容与原时间戳；本异常不会逃逸到调用方。
            var aborted = BuildAborted(stream, final);
            stream.Push(new AssistantMessageEvent.Error(StopReason.Aborted, aborted));
            stream.End(aborted);
            return;
        }

        if (final.StopReason is StopReason.Error or StopReason.Aborted)
        {
            stream.Push(new AssistantMessageEvent.Error(final.StopReason, final));
            stream.End(final);
            return;
        }
        if (final.StopReason == StopReason.Pending)
        {
            EmitError(stream, final, "Faux response ended without a stop reason");
            return;
        }

        stream.Push(new AssistantMessageEvent.Done(final.StopReason, final));
        stream.End(final);
    }

    private AssistantMessage NormalizeToolCallIds(AssistantMessage final)
    {
        if (!final.Content.Any(b => b is ToolCall call && string.IsNullOrEmpty(call.Id)))
        {
            return final;
        }

        var content = final.Content
            .Select(b => b is ToolCall call && string.IsNullOrEmpty(call.Id)
                ? call with { Id = $"tool:{_state.NextToolCallIndex()}" }
                : b)
            .ToArray();
        return final with { Content = content };
    }

    private static AssistantMessage BuildAborted(
        AssistantMessageEventStream stream,
        AssistantMessage final)
    {
        // stream.Partial 是最近一个事件携带的快照（含已累积内容、Usage 与原时间戳）；
        // 仅在快照尚未建立时回落到 final。
        var source = stream.Partial ?? final;
        return source with
        {
            StopReason = StopReason.Aborted,
            ErrorMessage = "Request was aborted",
        };
    }

    private async Task EmitTextAsync(
        AssistantMessageEventStream stream,
        AssistantMessage final,
        List<ContentBlock> built,
        TextContent block,
        CancellationToken cancellationToken)
    {
        var index = built.Count;
        var current = new TextContent { Text = string.Empty };
        stream.Push(new AssistantMessageEvent.TextStart(index, Partial(final, built, current)));
        foreach (var chunk in Chunk(block.Text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DelayAsync(cancellationToken).ConfigureAwait(false);
            current = current with { Text = current.Text + chunk };
            stream.Push(new AssistantMessageEvent.TextDelta(index, chunk, Partial(final, built, current)));
        }
        stream.Push(new AssistantMessageEvent.TextEnd(index, current.Text, Partial(final, built, current)));
        built.Add(current);
    }

    private async Task EmitThinkingAsync(
        AssistantMessageEventStream stream,
        AssistantMessage final,
        List<ContentBlock> built,
        ThinkingContent block,
        CancellationToken cancellationToken)
    {
        var index = built.Count;
        var current = new ThinkingContent { Thinking = string.Empty, ThinkingSignature = block.ThinkingSignature, Redacted = block.Redacted };
        stream.Push(new AssistantMessageEvent.ThinkingStart(index, Partial(final, built, current)));
        foreach (var chunk in Chunk(block.Thinking))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DelayAsync(cancellationToken).ConfigureAwait(false);
            current = current with { Thinking = current.Thinking + chunk };
            stream.Push(new AssistantMessageEvent.ThinkingDelta(index, chunk, Partial(final, built, current)));
        }
        stream.Push(new AssistantMessageEvent.ThinkingEnd(index, current.Thinking, Partial(final, built, current)));
        built.Add(current);
    }

    private async Task EmitToolCallAsync(
        AssistantMessageEventStream stream,
        AssistantMessage final,
        List<ContentBlock> built,
        ToolCall block,
        CancellationToken cancellationToken)
    {
        var index = built.Count;
        var current = block with { Arguments = new JsonObject() };
        stream.Push(new AssistantMessageEvent.ToolCallStart(index, Partial(final, built, current)));
        foreach (var chunk in Chunk(block.Arguments.ToJsonString()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DelayAsync(cancellationToken).ConfigureAwait(false);
            stream.Push(new AssistantMessageEvent.ToolCallDelta(index, chunk, Partial(final, built, current)));
        }
        stream.Push(new AssistantMessageEvent.ToolCallEnd(index, block, Partial(final, built, block)));
        built.Add(block);
    }

    private AssistantMessage Partial(
        AssistantMessage final,
        List<ContentBlock> built,
        ContentBlock current)
        => final with
        {
            Content = built.Concat(new[] { current }).ToArray(),
            StopReason = StopReason.Pending,
        };

    private IEnumerable<string> Chunk(string text)
    {
        // pi/pi.net 对齐：空文本也产出一个空分块（TextStart→空 delta→TextEnd）。
        if (text.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        // 有效分块大小固定为 min(MinTokenSize, MaxTokenSize)；MaxTokenSize 只起钳位作用。
        var size = Math.Max(1, Math.Min(_options.MinTokenSize, _options.MaxTokenSize));
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    private Task DelayAsync(CancellationToken cancellationToken)
        => _options.TokensPerSecond <= 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(1000.0 / _options.TokensPerSecond), cancellationToken);

    private AssistantMessage NormalizeFinal(
        Model model,
        TranscriptContext context,
        AssistantMessage message)
        => message with
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Timestamp = message.Timestamp == 0 ? _clock.UtcNowMs() : message.Timestamp,
            Usage = EstimateUsage(context, message),
        };

    private DeferredHandle CreateDeferredHandle(Model model) => new()
    {
        Provider = model.Provider,
        ModelId = model.Id,
        Api = model.Api,
        Id = $"deferred:{_state.NextDeferredIndex()}",
        PollAfterMs = _options.Deferred?.PollAfterMs,
    };

    private AssistantMessage CreateDeferredMessage(Model model, DeferredHandle handle) => new()
    {
        Content = Array.Empty<ContentBlock>(),
        Api = model.Api,
        Provider = model.Provider,
        Model = model.Id,
        Usage = new Usage(),
        StopReason = StopReason.Deferred,
        Deferred = handle,
        Timestamp = _clock.UtcNowMs(),
    };

    private void StoreDeferred(
        DeferredHandle handle,
        FauxResponseStep step,
        TranscriptContext context,
        SimpleStreamOptions options,
        Model model)
    {
        var entry = new DeferredEntry
        {
            Handle = handle,
            Step = step,
            Context = context,
            Options = options,
            Model = model,
            PendingFetches = Math.Max(0, _options.Deferred?.PendingFetches ?? 0),
        };
        lock (_deferredGate) _deferredResponses[handle.Id] = entry;
    }

    private IAssistantMessageEventStream FetchDeferredStream(
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options)
    {
        _state.IncrementDeferredFetchCount();
        var stream = new AssistantMessageEventStream();
        var cancellationToken = options?.CancellationToken ?? default;
        _ = FetchDeferredCoreAsync(stream, model, handle, options, cancellationToken);
        return stream;
    }

    private async Task FetchDeferredCoreAsync(
        AssistantMessageEventStream stream,
        Model model,
        DeferredHandle handle,
        DeferredFetchOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (options?.OnResponse is not null)
            {
                await options.OnResponse(
                    new ProviderResponse(200, new Dictionary<string, string>()),
                    model).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            DeferredEntry entry;
            lock (_deferredGate)
            {
                if (!_deferredResponses.TryGetValue(handle.Id, out var found)
                    || found.Handle.Provider != handle.Provider
                    || found.Handle.ModelId != handle.ModelId
                    || found.Handle.Api != handle.Api)
                {
                    throw new InvalidOperationException(
                        $"Unknown faux deferred response: {handle.Id}");
                }
                if (found.Cancelled)
                {
                    throw new InvalidOperationException(
                        $"Faux deferred response was cancelled: {handle.Id}");
                }
                entry = found;
            }

            if (entry.PendingFetches > 0)
            {
                lock (_deferredGate) entry.PendingFetches--;
                await EmitMessageAsync(
                    stream,
                    CreateDeferredMessage(model, handle),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            AssistantMessage? final;
            lock (_deferredGate) final = entry.Final;
            if (final is null)
            {
                final = await ResolveDeferredFinalAsync(entry, model).ConfigureAwait(false);
                lock (_deferredGate)
                {
                    // 单次解析：并发/重复取回共用首个解析结果。
                    if (entry.Final is null)
                    {
                        entry.Final = final;
                    }
                    final = entry.Final;
                }
            }

            // 走到此处 final 必非空（或已缓存/或刚解析）；`!` 仅消除流分析对
            // entry.Final（AssistantMessage?）的保守告警。
            await EmitMessageAsync(stream, final!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EmitAborted(stream, model, "Request was aborted");
        }
        catch (Exception error)
        {
            EmitError(stream, model, error.Message);
        }
    }

    private async Task<AssistantMessage> ResolveDeferredFinalAsync(
        DeferredEntry entry,
        Model requestModel)
    {
        try
        {
            var submissionOptions = entry.Options with
            {
                Deferred = null,
                CancellationToken = default,
                OnResponse = null,
            };
            var message = entry.Step is FauxResponseStep.Factory factory
                ? await factory.Create(entry.Context, submissionOptions, _state, entry.Model)
                    .ConfigureAwait(false)
                : ((FauxResponseStep.Response)entry.Step).Message;
            return NormalizeFinal(entry.Model, entry.Context, message);
        }
        catch (Exception error)
        {
            return new AssistantMessage
            {
                Content = Array.Empty<ContentBlock>(),
                Api = requestModel.Api,
                Provider = requestModel.Provider,
                Model = requestModel.Id,
                Usage = new Usage(),
                StopReason = StopReason.Error,
                ErrorMessage = error.Message,
                Timestamp = _clock.UtcNowMs(),
            };
        }
    }

    private async Task CancelDeferredAsync(
        Model model,
        DeferredHandle handle,
        DeferredCancelOptions? options,
        CancellationToken cancellationToken)
    {
        var clone = handle with
        {
            Data = handle.Data is null ? null : JsonNode.Parse(handle.Data.ToJsonString()),
        };
        lock (_deferredGate)
        {
            _state.CancelledDeferred.Add(clone);
            if (_deferredResponses.TryGetValue(handle.Id, out var entry))
            {
                entry.Cancelled = true;
            }
        }

        if (options?.OnResponse is not null)
        {
            await options.OnResponse(
                new ProviderResponse(200, new Dictionary<string, string>()),
                model).ConfigureAwait(false);
        }

        // 对齐 pi：faux 的 cancelDeferred 只做克隆/记录/标记 + onResponse，不读 signal、不抛——
        // 预取消的 token 也要让 best-effort cancel 正常完成。参数仅为 delegate 签名保留。
    }

    private sealed class DeferredEntry
    {
        public required DeferredHandle Handle { get; init; }
        public required FauxResponseStep Step { get; init; }
        public required TranscriptContext Context { get; init; }
        public required SimpleStreamOptions Options { get; init; }
        public required Model Model { get; init; }
        public int PendingFetches { get; set; }
        public bool Cancelled { get; set; }
        public AssistantMessage? Final { get; set; }
    }
}
