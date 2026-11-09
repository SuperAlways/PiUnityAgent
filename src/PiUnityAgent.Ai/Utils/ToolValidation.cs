using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Ai.Utils.Schema;

namespace PiUnityAgent.Ai.Utils;

/// <summary>
/// 工具调用参数校验（pi <c>utils/validation.ts</c>）：每个工具 schema 编译一次，
/// 先 coercion 再校验；失败按 <c>路径: 文案</c> 列出并附收到的参数。
/// </summary>
public static class ToolValidation
{
    private static readonly ConditionalWeakTable<JsonObject, ToolSchemaValidator> Validators = new();

    /// <summary>按名找工具并校验参数（first match，pi <c>validateToolCall</c>）。找不到/不合法抛错。</summary>
    public static JsonNode? ValidateToolCall(IReadOnlyList<Tool> tools, ToolCall toolCall)
    {
        var tool = tools.FirstOrDefault(candidate => candidate.Name == toolCall.Name)
            ?? throw new InvalidOperationException($"Tool \"{toolCall.Name}\" not found");
        return ValidateToolArguments(tool, toolCall);
    }

    /// <summary>校验并（必要时）强制转换参数（pi <c>validateToolArguments</c>）。</summary>
    public static JsonNode? ValidateToolArguments(Tool tool, ToolCall toolCall)
    {
        if (toolCall.ArgumentsError is { Length: > 0 } argumentsError)
        {
            throw new InvalidOperationException(
                $"Invalid arguments for tool \"{toolCall.Name}\": {argumentsError}");
        }

        // 深拷贝：工具改自己的参数不污染 transcript 里的调用（pi structuredClone）。
        var arguments = toolCall.Arguments.DeepClone().AsObject();
        var result = ValidatorFor(tool.Parameters).Parse(arguments);
        if (result.Ok) return result.Value;

        var errors = string.Join("\n",
            result.Errors.Select(error => $"  - {FormatPath(error.Path)}: {error.Message}"));
        var errorText = errors.Length > 0 ? errors : "  - root: invalid";
        throw new InvalidOperationException(
            $"Validation failed for tool \"{toolCall.Name}\":\n{errorText}\n\nReceived arguments:\n" +
            SchemaJson.StringifyIndented(toolCall.Arguments));
    }

    internal static ToolSchemaValidator ValidatorFor(JsonObject schema)
        => Validators.GetValue(schema, static key => ToolSchemaValidator.Compile(key));

    private static string FormatPath(string pointer)
    {
        if (pointer.Length == 0) return "root";
        return string.Join(".", pointer[1..].Split('/')
            .Select(segment => segment.Replace("~1", "/").Replace("~0", "~")));
    }
}
