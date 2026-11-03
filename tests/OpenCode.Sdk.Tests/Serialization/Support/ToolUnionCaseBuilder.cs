using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Serialization.Support;

internal static class ToolUnionCaseBuilder
{
    internal static IReadOnlyList<JsonElement> Seeds(bool state)
    {
        using var document = JsonDocument.Parse(new FixtureLoader().LoadJson(
            state ? "Serialization.reader-tool-states.json" : "Serialization.reader-tool-content.json"));
        return [.. document.RootElement.EnumerateArray().Select(static value => value.Clone())];
    }

    internal static IReadOnlyList<string> Required(JsonElement seed) => Tag(seed) switch
    {
        "streaming" => ["input"],
        "running" => ["input", "metadata"],
        "completed" => ["input", "content"],
        "error" => ["input", "error"],
        "file" => ["uri", "mime"],
        "text" => ["text"],
        "session.text.delta" or "session.reasoning.delta" or "session.tool.input.delta" => ["id", "created", "data"],
        "session.text.ended" => ["id", "created", "durable", "data"],
        _ => throw new InvalidOperationException("Unknown fixture arm."),
    };

    internal static string Tag(JsonElement seed) => seed.GetProperty(seed.TryGetProperty("status", out _) ? "status" : "type").GetString()!;

    internal static async Task<byte[]> ChangeAsync(JsonElement seed, string key, string change)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        if (change == "unknown")
        {
            writer.WriteStartArray("unknown");
            writer.WriteStartObject();
            writer.WriteString("nested", "ignored λ");
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
        var properties = seed.EnumerateObject().ToArray();
        foreach (var property in change == "reversed" ? properties.Reverse() : properties)
        {
            if (change == "minimal" && !Required(seed).Contains(property.Name, StringComparer.Ordinal) && property.Name is not ("status" or "type"))
            {
                continue;
            }
            if (property.Name == key && change == "omit")
            {
                continue;
            }
            if (property.Name == key && change is "null-first" or "different-first")
            {
                writer.WritePropertyName(key);
                if (change == "null-first")
                {
                    writer.WriteNullValue();
                }
                else
                {
                    Different(writer, property.Value);
                }
            }
            var name = change == "case" && property.Name == key ? key.ToUpperInvariant() : property.Name;
            writer.WritePropertyName(change == "escaped" ? JsonEncodedText.Encode(name, JavaScriptEncoder.Create(UnicodeRanges.None)) : JsonEncodedText.Encode(name));
            if (property.Name == key && change is "null" or "wrong")
            {
                if (change == "null")
                {
                    writer.WriteNullValue();
                }
                else
                {
                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        writer.WriteStringValue("wrong-token");
                    }
                    else
                    {
                        writer.WriteNumberValue(731);
                    }
                }
            }
            else
            {
                property.Value.WriteTo(writer);
            }
            if (property.Name == key && change is "null-last" or "different-last")
            {
                writer.WritePropertyName(key);
                if (change == "null-last")
                {
                    writer.WriteNullValue();
                }
                else
                {
                    Different(writer, property.Value);
                }
            }
        }
        writer.WriteEndObject();
        await writer.FlushAsync();
        return buffer.WrittenSpan.ToArray();
    }

    private static void Different(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String: writer.WriteStringValue("different-first"); break;
            case JsonValueKind.Number: writer.WriteNumberValue(997); break;
            case JsonValueKind.Array: writer.WriteStartArray(); writer.WriteEndArray(); break;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var member in value.EnumerateObject())
                {
                    writer.WritePropertyName(member.Name);
                    if (member.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue("different-first");
                    }
                    else
                    {
                        member.Value.WriteTo(writer);
                    }
                }
                writer.WriteNumber("first", 37);
                writer.WriteEndObject();
                break;
            default: throw new InvalidOperationException("Unsupported fixture duplicate.");
        }
    }

    internal static byte[] Bytes(JsonElement seed) => Encoding.UTF8.GetBytes(seed.GetRawText());

    internal static async Task<byte[]> WithDepthAsync(JsonElement seed, int depth)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("unknown");
        for (var index = 0; index < depth; index++)
        {
            writer.WriteStartArray();
        }
        writer.WriteStringValue(new string('λ', 256));
        for (var index = 0; index < depth; index++)
        {
            writer.WriteEndArray();
        }
        foreach (var member in seed.EnumerateObject())
        {
            member.WriteTo(writer);
        }
        writer.WriteEndObject();
        await writer.FlushAsync();
        return buffer.WrittenSpan.ToArray();
    }
}
