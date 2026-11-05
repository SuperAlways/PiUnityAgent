using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiUnityAgent.Ai.Types;

[JsonConverter(typeof(MessageContentJsonConverter))]
public abstract record MessageContent
{
    public sealed record Text(string Value) : MessageContent;

    public sealed record Blocks(IReadOnlyList<ContentBlock> Value) : MessageContent;

    public static MessageContent From(string text) => new Text(text);

    public static MessageContent From(IReadOnlyList<ContentBlock> blocks) => new Blocks(blocks);

    public IReadOnlyList<ContentBlock> ToBlocks() => this switch
    {
        Text text => new ContentBlock[] { new TextContent { Text = text.Value } },
        Blocks blocks => blocks.Value,
        _ => throw new InvalidOperationException($"Unsupported MessageContent variant: {GetType().Name}")
    };
}

public sealed class MessageContentJsonConverter : JsonConverter<MessageContent>
{
    public override bool HandleNull => true;

    public override MessageContent Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new MessageContent.Text(reader.GetString()!),
            JsonTokenType.StartArray => new MessageContent.Blocks(
                JsonSerializer.Deserialize<List<ContentBlock>>(ref reader, options) ?? new()),
            JsonTokenType.Null => new MessageContent.Blocks(Array.Empty<ContentBlock>()),
            _ => throw new JsonException(
                $"MessageContent expects a string or an array, got {reader.TokenType}.")
        };

    public override void Write(
        Utf8JsonWriter writer, MessageContent value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteStringValue("");
                break;
            case MessageContent.Text text:
                writer.WriteStringValue(text.Value);
                break;
            case MessageContent.Blocks blocks:
                JsonSerializer.Serialize(writer, blocks.Value, options);
                break;
            default:
                throw new JsonException($"Unsupported MessageContent variant: {value.GetType().Name}");
        }
    }
}
