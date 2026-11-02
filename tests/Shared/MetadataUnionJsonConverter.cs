using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OpenCode.Sdk.Internal;

// Compiled into each isolated image: Default and every recursive metadata call stay in that image.
internal abstract class MetadataUnionJsonConverter<TUnion> : JsonConverter<TUnion>
{
    private readonly UnionDiscriminatorReader _scanner = new();
    protected abstract string Concept { get; }
    protected abstract string Marker { get; }
    protected abstract UnionTagTable<Type?> Types { get; }
    protected virtual bool BooleanMarker => false;
    protected virtual string? Prefix => null;
    protected virtual Type? PrefixType => null;
    protected virtual void RequireFixed(ref Utf8JsonReader reader) { }
    protected abstract TUnion Unknown(string marker, JsonElement payload);

    public override TUnion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        RequireFixed(ref reader);
        Type? target;
        string? marker;
        bool known;
        if (BooleanMarker)
        {
            marker = _scanner.ReadBoolean(ref reader, Marker, Concept) ? "true" : "false";
            known = Types.TryGetValue(marker, out target);
        }
        else known = _scanner.TryFindKnown(ref reader, Marker, Concept, Types, out target, out marker);
        if (known && target is null)
            throw new JsonException($"The {Concept} payload uses a declared marker whose schema admits no JSON value.");
        if (!known && Prefix is { } prefix && marker!.StartsWith(prefix, StringComparison.Ordinal)) target = PrefixType;
        if (target is not null)
            return (TUnion)(JsonSerializer.Deserialize(ref reader, Info(target))
                ?? throw new JsonException($"The {Concept} payload deserialized to null."));
        using var document = JsonDocument.ParseValue(ref reader);
        return Unknown(marker!, document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, TUnion value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        JsonSerializer.Serialize(writer, value, Info(value.GetType()));
    }

    private static JsonTypeInfo Info(Type type) => OpenCodeJsonContext.Default.GetTypeInfo(type)
        ?? throw new JsonException($"The generated context has no metadata for {type.Name}.");
}
