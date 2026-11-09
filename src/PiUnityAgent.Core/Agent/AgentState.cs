using System;
using System.Collections.Generic;
using System.Linq;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>构造期初始状态；对应 pi AgentInitialState（AgentState 的可写子集）。</summary>
public sealed record AgentInitialState
{
    public string? SystemPrompt { get; init; }
    public Model? Model { get; init; }
    public ModelThinkingLevel? ThinkingLevel { get; init; }
    public IReadOnlyList<AgentTool>? Tools { get; init; }
    public IReadOnlyList<Message>? Messages { get; init; }
}

/// <summary>
/// 公开运行状态；对应 pi AgentState。读写面按 pi：tools/messages 赋值复制顶层数组。
/// 内部状态变更由 Agent 在 _gate 保护下调用；公开 getter 返回快照。
/// </summary>
public sealed class AgentState
{
    private readonly object _gate = new();
    private readonly List<AgentTool> _tools;
    private readonly List<Message> _messages;
    private readonly HashSet<string> _pendingToolCalls = new(StringComparer.Ordinal);
    private Model _model;
    private ModelThinkingLevel _thinkingLevel;
    private bool _isStreaming;
    private Message? _streamingMessage;
    private string? _errorMessage;

    internal AgentState(AgentInitialState? initialState)
    {
        _model = initialState?.Model ?? DefaultModel();
        _thinkingLevel = initialState?.ThinkingLevel ?? ModelThinkingLevel.Off;
        _tools = initialState?.Tools?.ToList() ?? new List<AgentTool>();
        _messages = initialState?.Messages?.ToList() ?? new List<Message>();

        var initial = AgentTranscript.CreateInitialSystemMessage(initialState?.SystemPrompt, _tools);
        if (initial is not null && (_messages.Count == 0 || _messages[0] is not SystemMessage))
        {
            _messages.Insert(0, initial);
        }
    }

    /// <summary>从 transcript 的 system 消息回读的当前 prompt（只读）。</summary>
    public string SystemPrompt => AgentTranscript.GetCurrentSystemPrompt(Messages);

    public Model Model
    {
        get
        {
            lock (_gate) return _model;
        }
        set
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            lock (_gate) _model = value;
        }
    }

    public ModelThinkingLevel ThinkingLevel
    {
        get
        {
            lock (_gate) return _thinkingLevel;
        }
        set
        {
            lock (_gate) _thinkingLevel = value;
        }
    }

    public IReadOnlyList<AgentTool> Tools
    {
        get
        {
            lock (_gate) return _tools.ToArray();
        }
        set
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            lock (_gate)
            {
                _tools.Clear();
                _tools.AddRange(value);
            }
        }
    }

    public IReadOnlyList<Message> Messages
    {
        get
        {
            lock (_gate) return _messages.ToArray();
        }
        set
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            lock (_gate)
            {
                _messages.Clear();
                _messages.AddRange(value);
            }
        }
    }

    public bool IsStreaming
    {
        get
        {
            lock (_gate) return _isStreaming;
        }
    }

    public Message? StreamingMessage
    {
        get
        {
            lock (_gate) return _streamingMessage;
        }
    }

    public IReadOnlyCollection<string> PendingToolCalls
    {
        get
        {
            lock (_gate) return _pendingToolCalls.ToArray();
        }
    }

    public string? ErrorMessage
    {
        get
        {
            lock (_gate) return _errorMessage;
        }
    }

    internal void SetIsStreaming(bool value)
    {
        lock (_gate) _isStreaming = value;
    }

    internal void SetStreamingMessage(Message? message)
    {
        lock (_gate) _streamingMessage = message;
    }

    internal void AddMessage(Message message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        lock (_gate) _messages.Add(message);
    }

    internal void AddPendingToolCall(string toolCallId)
    {
        lock (_gate) _pendingToolCalls.Add(toolCallId);
    }

    internal void RemovePendingToolCall(string toolCallId)
    {
        lock (_gate) _pendingToolCalls.Remove(toolCallId);
    }

    internal void SetErrorMessage(string? message)
    {
        lock (_gate) _errorMessage = message;
    }

    internal void ClearPendingToolCalls()
    {
        lock (_gate) _pendingToolCalls.Clear();
    }

    internal void ClearRuntime()
    {
        lock (_gate)
        {
            _isStreaming = false;
            _streamingMessage = null;
            _pendingToolCalls.Clear();
            _errorMessage = null;
        }
    }

    internal void ReplaceMessages(IReadOnlyList<Message> messages)
    {
        lock (_gate)
        {
            _messages.Clear();
            _messages.AddRange(messages);
        }
    }

    private static Model DefaultModel() => new()
    {
        Id = "unknown",
        Name = "unknown",
        Api = "unknown",
        Provider = "unknown",
        BaseUrl = string.Empty,
        Input = Array.Empty<ModelInputModality>(),
        Cost = new ModelCost(),
        ContextWindow = 0,
        MaxTokens = 0,
    };
}
