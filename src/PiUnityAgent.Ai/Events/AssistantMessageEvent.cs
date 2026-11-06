using System.Text.Json.Serialization;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Events;

/// <summary>助手响应流式事件的判别基类。对应 pi AssistantMessageEvent 联合。</summary>
public abstract record AssistantMessageEvent
{
    public abstract string Type { get; }

    [JsonIgnore]
    public bool IsTerminal => this is Done or Error;

    public sealed record Start(AssistantMessage Partial) : AssistantMessageEvent
    {
        public override string Type => "start";
    }

    public sealed record TextStart(int ContentIndex, AssistantMessage Partial) : AssistantMessageEvent
    {
        public override string Type => "text_start";
    }

    public sealed record TextDelta(int ContentIndex, string Delta, AssistantMessage Partial) : AssistantMessageEvent
    {
        public override string Type => "text_delta";
    }

    public sealed record TextEnd(int ContentIndex, string Content, AssistantMessage Partial) : AssistantMessageEvent
    {
        public override string Type => "text_end";
    }

    public sealed record ThinkingStart(int ContentIndex, AssistantMessage Partial) : AssistantMessageEvent
    {
        public override string Type => "thinking_start";
    }

    public sealed record ThinkingDelta(int ContentIndex, string Delta, AssistantMessage Partial) : AssistantMessageEvent
    {
        public override string Type => "thinking_delta";
    }

    public sealed record ThinkingEnd(int ContentIndex, string Content, AssistantMessage Partial) : AssistantMessageEvent
    {
        public override string Type => "thinking_end";
    }

    public sealed record ToolCallStart(int ContentIndex, AssistantMessage Partial) : AssistantMessageEvent
    {
        public override string Type => "toolcall_start";
    }

    public sealed record ToolCallDelta(int ContentIndex, string Delta, AssistantMessage Partial) : AssistantMessageEvent
    {
        public override string Type => "toolcall_delta";
    }

    public sealed record ToolCallEnd(int ContentIndex, ToolCall ToolCall, AssistantMessage Partial) : AssistantMessageEvent
    {
        public override string Type => "toolcall_end";
    }

    public sealed record Done(StopReason Reason, AssistantMessage Message) : AssistantMessageEvent
    {
        public override string Type => "done";
    }

    public sealed record Error(StopReason Reason, AssistantMessage Message) : AssistantMessageEvent
    {
        public override string Type => "error";
    }
}
