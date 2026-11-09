using System.Text.Json;
using System.Text.Json.Nodes;
using PiUnityAgent.Ai.Types;
using PiUnityAgent.Core.Tools;

namespace PiUnityAgent.Core.Agent;

/// <summary>
/// 工具装载变更公告（pi declareToolChanges）。context.Tools 是运行时可执行集；
/// transcript 的 system 消息声明模型可调用集；每次请求前把差异写入系统消息。
/// pending 中已有 system 消息时，其 tool 字段视为意图并替换为相对 committed
/// transcript 的 delta（重放恒得 context.Tools）；否则在首个非 system pending 前插入。
/// S9/D11 的公开 transcript 重放辅助不在本阶段。
/// </summary>
internal static class ToolLoadout
{
    public static IReadOnlyList<Message> DeclareToolChanges(
        AgentContext context,
        IReadOnlyList<Message> pendingMessages,
        long timestamp,
        IReadOnlyList<Message>? committed = null)
    {
        var pending = pendingMessages.ToList();
        var systemIndex = -1;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            if (pending[i] is SystemMessage) { systemIndex = i; break; }
        }

        // baseline = pending 去掉 pending system 消息的工具意图（仅用于推演 currentTools）。
        var baseline = pending.Select((message, index) =>
            index == systemIndex && message is SystemMessage system
                ? WithToolChanges(system, Array.Empty<Tool>(), Array.Empty<ToolReference>())
                : message).ToList();

        var currentTools = GetCurrentTools(committed ?? context.Messages, baseline);
        var desired = (context.Tools ?? Array.Empty<AgentTool>()).Select(ToToolDeclaration).ToList();
        var changes = GetToolStateChanges(currentTools, desired);
        var unchanged = changes.ToolsAdded.Count == 0 && changes.ToolsRemoved.Count == 0;

        if (systemIndex >= 0 && pending[systemIndex] is SystemMessage pendingSystem)
        {
            // 已声明无工具变化的 system 消息保持原对象（TS 快路径，调用方可按引用判断 no-op）。
            if (unchanged && DeclaresNoToolChanges(pendingSystem))
                return pendingMessages;
            pending[systemIndex] = WithToolChanges(pendingSystem, changes.ToolsAdded, changes.ToolsRemoved);
            return pending;
        }

        if (unchanged) return pendingMessages;

        var update = new SystemMessage
        {
            Content = MessageContent.From(""),
            Timestamp = timestamp,
            ToolsAdded = changes.ToolsAdded.Count > 0 ? changes.ToolsAdded : null,
            ToolsRemoved = changes.ToolsRemoved.Count > 0 ? changes.ToolsRemoved : null,
        };
        var insertIndex = pending.FindIndex(message => message is not SystemMessage);
        pending.Insert(insertIndex < 0 ? pending.Count : insertIndex, update);
        return pending;
    }

    /// <summary>TS 同款：toolsAdded/toolsRemoved 为空（null 或空表）视为未声明任何工具变化。</summary>
    private static bool DeclaresNoToolChanges(SystemMessage message)
        => (message.ToolsAdded is null || message.ToolsAdded.Count == 0)
           && (message.ToolsRemoved is null || message.ToolsRemoved.Count == 0);

    internal static SystemMessage WithToolChanges(
        SystemMessage message, IReadOnlyList<Tool> added, IReadOnlyList<ToolReference> removed)
        => message with
        {
            ToolsAdded = added.Count > 0 ? added : null,
            ToolsRemoved = removed.Count > 0 ? removed : null,
        };

    internal static IReadOnlyList<Tool> GetCurrentTools(params IEnumerable<Message>[] messageSets)
    {
        var tools = new Dictionary<string, Tool>(StringComparer.Ordinal);
        foreach (var set in messageSets)
        {
            foreach (var message in set)
            {
                if (message is not SystemMessage system) continue;
                foreach (var reference in system.ToolsRemoved ?? Array.Empty<ToolReference>())
                    tools.Remove(reference.Name);
                foreach (var tool in system.ToolsAdded ?? Array.Empty<Tool>())
                    tools[tool.Name] = tool;
            }
        }
        return tools.Values.ToList();
    }

    internal static (IReadOnlyList<Tool> ToolsAdded, IReadOnlyList<ToolReference> ToolsRemoved)
        GetToolStateChanges(IReadOnlyList<Tool> previous, IReadOnlyList<Tool> current)
    {
        // 同名工具按 TS Map 语义去重（后写者胜），不因重名抛错。
        var previousTools = new Dictionary<string, Tool>(StringComparer.Ordinal);
        foreach (var tool in previous) previousTools[tool.Name] = tool;
        var currentTools = new Dictionary<string, Tool>(StringComparer.Ordinal);
        foreach (var tool in current) currentTools[tool.Name] = tool;
        // 定义变化 = 移除 + 新增（TS getToolStateChanges）。
        var added = current.Where(tool =>
                !previousTools.TryGetValue(tool.Name, out var before) || !DeclarationsEqual(before, tool))
            .Select(ToToolDeclaration).ToList();
        var removed = previous.Where(tool =>
                !currentTools.TryGetValue(tool.Name, out var after) || !DeclarationsEqual(tool, after))
            .Select(tool => new ToolReference { Name = tool.Name }).ToList();
        return (added, removed);
    }

    /// <summary>运行时工具 → 模型声明视图（AgentTool.Definition 单点投影）。</summary>
    internal static Tool ToToolDeclaration(AgentTool tool) => tool.Definition;

    /// <summary>声明视图归一：Parameters 深拷贝（TS toToolDeclaration 的 JSON 往返）。</summary>
    internal static Tool ToToolDeclaration(Tool tool) => tool with
    {
        Parameters = (JsonObject)tool.Parameters.DeepClone(),
    };

    internal static bool DeclarationsEqual(Tool left, Tool right)
        => Canonical(left).ToJsonString() == Canonical(right).ToJsonString();

    /// <summary>
    /// 声明四字段的规范化 JSON。键序由插入序固定；ConstrainedSampling 按运行时变体展开
    /// （System.Text.Json 对声明为抽象基类的属性只序列化基类契约，匿名对象直排会把
    /// Disabled/Config 混同为 {}，改手写展开保证定义差异可见）。
    /// </summary>
    private static JsonNode Canonical(Tool tool)
        => new JsonObject
        {
            ["Name"] = tool.Name,
            ["Description"] = tool.Description,
            ["Parameters"] = tool.Parameters.DeepClone(),
            ["ConstrainedSampling"] = CanonicalConstrainedSampling(tool.ConstrainedSampling),
        };

    private static JsonNode? CanonicalConstrainedSampling(ConstrainedSampling? value) => value switch
    {
        null => null,
        ConstrainedSampling.Disabled => new JsonObject { ["Disabled"] = true },
        ConstrainedSampling.Config config => new JsonObject { ["Config"] = CanonicalConstrainedSamplingConfig(config.Value) },
        _ => new JsonObject(),
    };

    private static JsonNode CanonicalConstrainedSamplingConfig(ConstrainedSamplingConfig value) => value switch
    {
        ConstrainedSamplingConfig.JsonSchema schema => new JsonObject
        {
            ["JsonSchema"] = new JsonObject { ["Strict"] = (int)schema.Strict },
        },
        ConstrainedSamplingConfig.Grammar grammar => new JsonObject
        {
            ["Grammar"] = new JsonObject(
                grammar.Variants.OrderBy(pair => pair.Key)
                    .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key.ToString(), pair.Value))),
        },
        _ => new JsonObject(),
    };
}
