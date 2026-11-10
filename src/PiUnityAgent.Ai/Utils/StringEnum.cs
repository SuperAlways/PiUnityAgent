using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace PiUnityAgent.Ai.Utils;

/// <summary>
/// pi string-enum.ts：构造与 Google 等 provider 兼容的字符串枚举 schema——
/// 它们不支持 anyOf/const 写法，只认 <c>{ type: "string", enum: [...] }</c>。
/// </summary>
public static class StringEnum
{
    /// <summary>
    /// pi StringEnum(values, options) 的 C# 写法（pi 是可选对象参数，这里拆成具名可空参数）：
    /// description / default 仅在非空时输出（pi 是真值展开，空串同样省略）。
    /// </summary>
    public static JsonObject Create(
        IReadOnlyList<string> values,
        string? description = null,
        string? defaultValue = null)
    {
        var enumValues = new JsonArray();
        foreach (var value in values)
        {
            enumValues.Add(JsonValue.Create(value));
        }

        var schema = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = enumValues,
        };
        if (!string.IsNullOrEmpty(description))
        {
            schema["description"] = description;
        }
        if (!string.IsNullOrEmpty(defaultValue))
        {
            schema["default"] = defaultValue;
        }
        return schema;
    }
}
