using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Events;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Providers;

public sealed class FauxProvider
{
    private readonly object _gate = new();
    private readonly Queue<FauxResponseStep> _responses = new();
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
            Cost = definition.Cost ?? new ModelCost(),
            Reasoning = definition.Reasoning,
            ContextWindow = definition.ContextWindow,
            MaxTokens = definition.MaxTokens,
        }).ToList();

        Provider = new Provider
        {
            Id = _options.Provider,
            Name = _options.Provider,
            Models = Models,
            Stream = StreamAsync,
        };
    }

    public Provider Provider { get; }
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
        => id is null ? Models[0] : Models.First(m => m.Id == id);

    private Task<IAssistantMessageEventStream> StreamAsync(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken)
    {
        FauxResponseStep? step;
        lock (_gate) step = _responses.Count > 0 ? _responses.Dequeue() : null;
        _state.IncrementCallCount();

        var stream = new AssistantMessageEventStream();
        _ = EmitAsync(stream, step, model, context, options, cancellationToken);
        return Task.FromResult<IAssistantMessageEventStream>(stream);
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

            AssistantMessage message;
            if (step is FauxResponseStep.Factory factory)
            {
                message = await factory.Create(context, options, _state, model).ConfigureAwait(false);
            }
            else
            {
                message = ((FauxResponseStep.Response)step).Message;
            }

            var final = message with
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                Timestamp = message.Timestamp == 0 ? _clock.UtcNowMs() : message.Timestamp,
                Usage = EstimateUsage(context, message),
            };

            await EmitMessageAsync(stream, final, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EmitAborted(stream, model, "Aborted");
        }
        catch (Exception error)
        {
            EmitError(stream, model, error.Message);
        }
    }

    private static Usage EstimateUsage(TranscriptContext context, AssistantMessage message)
    {
        var input = context.Messages.Sum(m => m switch
        {
            UserMessage user => user.Content.ToBlocks().Sum(b => b is TextContent t ? t.Text.Length : 0),
            _ => 0,
        }) / 4;
        var output = message.Content.OfType<TextContent>().Sum(t => t.Text.Length) / 4;
        return new Usage
        {
            Input = input,
            Output = output,
            TotalTokens = input + output,
        };
    }

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

    private static void EmitAborted(AssistantMessageEventStream stream, Model model, string text)
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
            Timestamp = 0,
        };
        stream.Push(new AssistantMessageEvent.Error(StopReason.Aborted, assistant));
        stream.End(assistant);
    }

    private async Task EmitMessageAsync(
        AssistantMessageEventStream stream,
        AssistantMessage final,
        CancellationToken cancellationToken)
    {
        var partial = final with
        {
            Content = Array.Empty<ContentBlock>(),
            StopReason = StopReason.Pending,
        };
        stream.Push(new AssistantMessageEvent.Start(partial));

        var built = new List<ContentBlock>();
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
            }
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
        var size = Math.Max(1, Math.Min(_options.MinTokenSize, _options.MaxTokenSize));
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    private Task DelayAsync(CancellationToken cancellationToken)
        => _options.TokensPerSecond <= 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(1000.0 / _options.TokensPerSecond), cancellationToken);
}
