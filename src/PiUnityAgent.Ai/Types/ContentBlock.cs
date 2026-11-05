using System.Text.Json.Serialization;

namespace PiUnityAgent.Ai.Types;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ThinkingContent), "thinking")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(ToolCall), "toolCall")]
public abstract record ContentBlock
{
    /// <summary>Wire discriminator for convenience; JSON uses [JsonDerivedType].</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}
