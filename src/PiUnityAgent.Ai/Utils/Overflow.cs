using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Utils;

/// <summary>
/// pi overflow.ts：上下文溢出判定。三种情形：
/// 1) 错误文案命中各 provider 的溢出模式；2) 静默溢出（z.ai 式：请求成功但 usage.input 超上下文窗口）；
/// 3) length 止步且零输出、输入占满窗口（Xiaomi MiMo 式：截断输入后无处生成输出）。
/// </summary>
public static class Overflow
{
    // pi overflow.ts:39-69：各 provider 的上下文溢出错误文案（与 pi 逐条对齐，全部忽略大小写）。
    private static readonly Regex[] OverflowPatterns =
    {
        new(@"prompt (?:is )?too long", RegexOptions.IgnoreCase), // Anthropic 与 z.ai token 溢出
        new(@"prompt exceeds max length", RegexOptions.IgnoreCase), // z.ai CN 端点 token 溢出
        new(@"request_too_large", RegexOptions.IgnoreCase), // Anthropic 请求字节量溢出（HTTP 413）
        new(@"input is too long for requested model", RegexOptions.IgnoreCase), // Amazon Bedrock
        new(@"exceeds the context window", RegexOptions.IgnoreCase), // OpenAI（Completions 与 Responses API）
        new(@"exceeds (?:the )?(?:model'?s )?maximum context length(?: of [\d,]+ tokens?|\s*\([\d,]+\))", RegexOptions.IgnoreCase), // OpenAI 兼容代理（LiteLLM）
        new(@"context window exceeded", RegexOptions.IgnoreCase), // LiteLLM 请求前检查（ContextWindowExceededError）
        new(@"input token count.*exceeds the maximum", RegexOptions.IgnoreCase), // Google（Gemini）
        new(@"maximum prompt length is \d+", RegexOptions.IgnoreCase), // xAI（Grok）
        new(@"reduce the length of the messages", RegexOptions.IgnoreCase), // Groq
        new(@"maximum context length is \d+ tokens", RegexOptions.IgnoreCase), // OpenRouter（多数后端）
        new(@"exceeds (?:the )?maximum allowed input length of [\d,]+ tokens?", RegexOptions.IgnoreCase), // OpenRouter/Poolside
        new(@"input \(\d+ tokens\) is longer than the model'?s context length \(\d+ tokens\)", RegexOptions.IgnoreCase), // Together AI
        new(@"exceeds the limit of \d+", RegexOptions.IgnoreCase), // GitHub Copilot
        new(@"exceeds the available context size", RegexOptions.IgnoreCase), // llama.cpp server
        new(@"greater than the context length", RegexOptions.IgnoreCase), // LM Studio
        new(@"context window exceeds limit", RegexOptions.IgnoreCase), // MiniMax
        new(@"exceeded model token limit", RegexOptions.IgnoreCase), // Kimi For Coding
        new(@"too large for model with \d+ maximum context length", RegexOptions.IgnoreCase), // Mistral
        new(@"prompt has [\d,]+ tokens?, but the configured context size is [\d,]+ tokens?", RegexOptions.IgnoreCase), // DS4 server
        new(@"model_context_window_exceeded", RegexOptions.IgnoreCase), // z.ai 以错误文本露出的非标 finish_reason
        new(@"prompt too long; exceeded (?:max )?context length", RegexOptions.IgnoreCase), // Ollama 显式溢出错误
        new(@"range of input length should be", RegexOptions.IgnoreCase), // DashScope / Qwen Token Plan
        new(@"context[_ ]length[_ ]exceeded", RegexOptions.IgnoreCase), // 通用兜底
        new(@"too many tokens", RegexOptions.IgnoreCase), // 通用兜底
        new(@"token limit exceeded", RegexOptions.IgnoreCase), // 通用兜底
        // HTTP 413 无论 provider 与响应体内容如何都意味着请求体超限。
        // 只匹配裸 SDK 错误与 formatProviderError 的前缀，不匹配响应体里提到的状态码。
        new(@"^(?:[\w -]+ API error \(413\):|413(?:\s|:|$))", RegexOptions.IgnoreCase),
    };

    // pi overflow.ts:71：Cerebras 400/413 无响应体的裸错误文案。
    private static readonly Regex CerebrasBodylessOverflowPattern =
        new(@"^4(?:00|13)\s*(?:status code)?\s*\(no body\)", RegexOptions.IgnoreCase);

    // pi overflow.ts:82-86：明确非溢出的错误（限流/服务端错误）；命中即排除溢出判定，
    // 例：Bedrock 的限流文案含 "Too many tokens"，没有排除就会误判成溢出。
    private static readonly Regex[] NonOverflowPatterns =
    {
        new(@"^(Throttling error|Service unavailable):", RegexOptions.IgnoreCase), // AWS Bedrock 非溢出（formatBedrockError 的人类可读前缀）
        new(@"rate limit", RegexOptions.IgnoreCase), // 通用限流
        new(@"too many requests", RegexOptions.IgnoreCase), // 通用 HTTP 429 风格
    };

    /// <summary>
    /// pi isContextOverflow：判断一条 assistant 消息是否代表上下文溢出。
    /// case 1 错误文案模式；case 2 静默溢出（需传 contextWindow）；case 3 length 止步零输出溢出。
    /// </summary>
    public static bool IsContextOverflow(AssistantMessage message, int? contextWindow = null)
    {
        // 情形 1：错误文案模式匹配
        if (message.StopReason == StopReason.Error && !string.IsNullOrEmpty(message.ErrorMessage))
        {
            // 命中已知非溢出模式（限流/服务端错误）的先排除
            var isNonOverflow = MatchesAny(NonOverflowPatterns, message.ErrorMessage);
            if (!isNonOverflow)
            {
                if (MatchesAny(OverflowPatterns, message.ErrorMessage))
                {
                    return true;
                }
                if (message.Provider == "cerebras" && CerebrasBodylessOverflowPattern.IsMatch(message.ErrorMessage))
                {
                    return true;
                }
            }
        }

        // 情形 2：静默溢出（z.ai 式）——请求成功但 usage 超上下文。
        // pi 用真值判断（0 视为未传），这里对齐为 != 0。
        if (contextWindow is int stopWindow && stopWindow != 0 && message.StopReason == StopReason.Stop)
        {
            var inputTokens = message.Usage.Input + message.Usage.CacheRead;
            if (inputTokens > stopWindow)
            {
                return true;
            }
        }

        // 情形 3：length 止步溢出（Xiaomi MiMo 式）——服务端把超长输入截到窗口内，
        // 没给输出留空间：stopReason=length 且 output=0，input+cacheRead 占满窗口（>= 99%）。
        if (contextWindow is int lengthWindow && lengthWindow != 0
            && message.StopReason == StopReason.Length && message.Usage.Output == 0)
        {
            var inputTokens = message.Usage.Input + message.Usage.CacheRead;
            if (inputTokens >= lengthWindow * 0.99)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// pi isRecoverableLength：length 止步是否低于调用方/模型原本想要的输出上限。
    /// 这类响应可能由上下文压力或 provider 侧截断造成，调用方可以做一次有界的压缩重试。
    /// desiredMaxOutput 必须是做任何上下文钳位之前的原始上限。
    /// </summary>
    public static bool IsRecoverableLength(AssistantMessage message, int desiredMaxOutput)
        => message.StopReason == StopReason.Length && desiredMaxOutput > 0 && message.Usage.Output < desiredMaxOutput;

    /// <summary>pi getOverflowPatterns：取溢出模式列表（供测试）；每次返回数组副本，正则实例复用。</summary>
    public static IReadOnlyList<Regex> GetOverflowPatterns()
        => (Regex[])OverflowPatterns.Clone();

    private static bool MatchesAny(Regex[] patterns, string input)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(input))
            {
                return true;
            }
        }
        return false;
    }
}
