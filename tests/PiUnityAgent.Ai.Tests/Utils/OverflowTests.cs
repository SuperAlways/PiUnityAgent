using System;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Ai.Utils;

namespace PiUnityAgent.Ai.Tests.Utils;

public class OverflowTests
{
    private static AssistantMessage Msg(
        StopReason stopReason,
        string? errorMessage = null,
        string provider = "p1",
        Usage? usage = null)
        => new()
        {
            Content = Array.Empty<ContentBlock>(),
            Api = "faux",
            Provider = provider,
            Model = "m1",
            Usage = usage ?? new Usage(),
            StopReason = stopReason,
            Timestamp = 1,
            ErrorMessage = errorMessage,
        };

    [Fact]
    public void Representative_Provider_Error_Messages_Are_Detected()
    {
        // 各 provider 的代表性溢出文案（pi 注释里的原文样例）。
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "prompt is too long: 213462 tokens > 200000 maximum"))); // Anthropic
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Your input exceeds the context window of this model"))); // OpenAI
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Requested token count exceeds the model's maximum context length of 131072 tokens"))); // LiteLLM
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "litellm.ContextWindowExceededError: Context Window exceeded for given call."))); // LiteLLM 预检
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "The input token count (1196265) exceeds the maximum number of tokens allowed (1048575)"))); // Google
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "This model's maximum prompt length is 131072 but the request contains 537812 tokens"))); // xAI
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Please reduce the length of the messages or completion"))); // Groq
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "This endpoint's maximum context length is 200000 tokens. However, you requested about 210000 tokens"))); // OpenRouter
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "The input (200000 tokens) is longer than the model's context length (131072 tokens)"))); // Together AI
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "the request exceeds the available context size, try increasing it"))); // llama.cpp
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "tokens to keep from the initial prompt is greater than the context length"))); // LM Studio
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "invalid params, context window exceeds limit"))); // MiniMax
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Your request exceeded model token limit: 131072 (requested: 140000)"))); // Kimi
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Prompt has 150,000 tokens, but the configured context size is 131,072 tokens"))); // DS4
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Prompt contains 150000 tokens ... too large for model with 131072 maximum context length"))); // Mistral
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "{\"code\":\"1261\",\"message\":\"Prompt too long\"}"))); // z.ai CN 端点
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Range of input length should be [1, 32768]"))); // DashScope/Qwen
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "prompt too long; exceeded max context length by 1024 tokens"))); // Ollama
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "github-copilot API error (413): {\"message\":\"failed to parse request\"}"))); // Copilot 413
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "413 {\"error\":{\"type\":\"request_too_large\",\"message\":\"Request exceeds the maximum size\"}}"))); // Anthropic 413
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "model_context_window_exceeded"))); // z.ai 非标 finish_reason
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "input is too long for requested model"), 0)); // Bedrock；contextWindow=0 不影响文案判定
    }

    [Fact]
    public void Non_Overflow_Errors_Are_Excluded_Even_When_They_Match_Overflow_Patterns()
    {
        // pi：Bedrock 限流文案含 "too many tokens"，没有排除就会误判成溢出。
        Assert.False(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Throttling error: Too many tokens, please wait before trying again.")));
        Assert.False(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Service unavailable: too many tokens, please retry later.")));
        Assert.False(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Rate limit hit: too many tokens in prompt")));
        Assert.False(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "Too many requests: too many tokens in batch")));
    }

    [Fact]
    public void Cerebras_Bodyless_Status_Error_Is_Detected_Only_For_Cerebras()
    {
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "400 status code (no body)", provider: "cerebras")));
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "413 status code (no body)", provider: "cerebras")));
        // 同样的文案换了 provider 就不算（主体模式列表都不命中）。
        Assert.False(Overflow.IsContextOverflow(
            Msg(StopReason.Error, "400 status code (no body)", provider: "p1")));
    }

    [Fact]
    public void Error_Message_Needed_For_Pattern_Case()
    {
        Assert.False(Overflow.IsContextOverflow(Msg(StopReason.Error, errorMessage: null)));
        // pi 真值判断：空串错误信息视同缺失。
        Assert.False(Overflow.IsContextOverflow(Msg(StopReason.Error, errorMessage: "")));
    }

    [Fact]
    public void Silent_Overflow_Detected_When_Input_Exceeds_ContextWindow()
    {
        // z.ai 式：stopReason=stop 但 input+cacheRead 超窗口。
        var usage = new Usage { Input = 1000, CacheRead = 100 };
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Stop, usage: usage), contextWindow: 1099));
        // 等于窗口不算（pi 是严格大于）。
        Assert.False(Overflow.IsContextOverflow(
            Msg(StopReason.Stop, usage: usage), contextWindow: 1100));
        Assert.False(Overflow.IsContextOverflow(
            Msg(StopReason.Stop, usage: usage), contextWindow: 1101));
        // 不传窗口不判静默溢出。
        Assert.False(Overflow.IsContextOverflow(Msg(StopReason.Stop, usage: usage)));
    }

    [Fact]
    public void Length_Stop_With_Zero_Output_Filling_Window_Is_Overflow()
    {
        // Xiaomi MiMo 式：output=0 且 input+cacheRead >= 99% 窗口。
        var usage = new Usage { Input = 990, CacheRead = 0 };
        Assert.True(Overflow.IsContextOverflow(
            Msg(StopReason.Length, usage: usage), contextWindow: 1000));
        Assert.False(Overflow.IsContextOverflow(
            Msg(StopReason.Length, usage: usage), contextWindow: 1001));
        // output > 0 说明还有空间生成，不算溢出。
        var withOutput = new Usage { Input = 990, Output = 10 };
        Assert.False(Overflow.IsContextOverflow(
            Msg(StopReason.Length, usage: withOutput), contextWindow: 1000));
        // 不传窗口不判。
        Assert.False(Overflow.IsContextOverflow(Msg(StopReason.Length, usage: usage)));
    }

    [Fact]
    public void IsRecoverableLength_Requires_Length_Stop_And_Short_Output()
    {
        Assert.True(Overflow.IsRecoverableLength(Msg(StopReason.Length, usage: new Usage { Output = 500 }), 1000));
        Assert.False(Overflow.IsRecoverableLength(Msg(StopReason.Stop, usage: new Usage { Output = 500 }), 1000));
        Assert.False(Overflow.IsRecoverableLength(Msg(StopReason.Length, usage: new Usage { Output = 1000 }), 1000));
        Assert.False(Overflow.IsRecoverableLength(Msg(StopReason.Length, usage: new Usage { Output = 1001 }), 1000));
        // desiredMaxOutput <= 0 一律不可恢复。
        Assert.False(Overflow.IsRecoverableLength(Msg(StopReason.Length, usage: new Usage { Output = 0 }), 0));
        Assert.False(Overflow.IsRecoverableLength(Msg(StopReason.Length, usage: new Usage { Output = 0 }), -1));
    }

    [Fact]
    public void GetOverflowPatterns_Returns_Copy_With_All_Primary_Patterns()
    {
        var first = Overflow.GetOverflowPatterns();
        var second = Overflow.GetOverflowPatterns();
        // pi 返回 [...OVERFLOW_PATTERNS]：每次新数组，元素复用；主列表 27 条 > 20。
        Assert.NotSame(first, second);
        Assert.True(first.Count > 20);
        Assert.Equal(first.Count, second.Count);
    }
}
