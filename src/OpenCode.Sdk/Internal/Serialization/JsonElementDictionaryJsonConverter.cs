using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Internal;

internal sealed class JsonElementDictionaryJsonConverter : JsonConverter<IReadOnlyDictionary<string, JsonElement>>
{
    private static readonly JsonElement True = ParseLiteral("true"u8);
    private static readonly JsonElement False = ParseLiteral("false"u8);
    private static readonly JsonElement Null = ParseLiteral("null"u8);

    public override IReadOnlyDictionary<string, JsonElement> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        ArgumentNullException.ThrowIfNull(options);
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A JsonElement dictionary must be an object.");
        }

        var dictionary = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var probe = reader;
        if (probe.Read() && probe.TokenType == JsonTokenType.EndObject)
        {
            reader = probe;
            return dictionary;
        }

        // Each non-literal value retains the shared document, bounded by the dictionary's own JSON,
        // to avoid a separate document and copy for every entry.
        var root = JsonElement.ParseValue(ref reader);
        foreach (var property in root.EnumerateObject())
        {
            if (options.AllowDuplicateProperties)
            {
                dictionary[property.Name] = Literal(property.Value);
            }
            else if (!dictionary.TryAdd(property.Name, Literal(property.Value)))
            {
                throw new JsonException("A JsonElement dictionary contains a duplicate key.");
            }
        }

        return dictionary;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, JsonElement> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        writer.WriteStartObject();
        foreach (var entry in value)
        {
            writer.WritePropertyName(entry.Key);
            entry.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// System.Text.Json reads every <c>true</c>, <c>false</c> and <c>null</c> into one cached element
    /// each, so repeated literals compare equal by default; a shared-document child would not.
    /// </summary>
    private static JsonElement Literal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => True,
        JsonValueKind.False => False,
        JsonValueKind.Null => Null,
        JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Number => value,
        _ => value,
    };

    private static JsonElement ParseLiteral(ReadOnlySpan<byte> literal)
    {
        var reader = new Utf8JsonReader(literal);
        _ = reader.Read();
        return JsonElement.ParseValue(ref reader);
    }
}
