using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// One known arm of a generated union: the type its tag selects, read through that type's
/// source-generated converter, so a known payload materializes without re-entering the serializer.
/// </summary>
internal abstract class UnionArm<TUnion>
{
    /// <summary>The arm's type, as the union's tag table maps it.</summary>
    public abstract Type Type { get; }

    public abstract TUnion? Read(ref Utf8JsonReader reader);

    public static UnionArm<TUnion> Of<TArm>()
        where TArm : TUnion => new Arm<TArm>();

    private sealed class Arm<TArm> : UnionArm<TUnion>
        where TArm : TUnion
    {
        private JsonTypeInfo<TArm>? _typeInfo;

        public override Type Type => typeof(TArm);

        public override TUnion? Read(ref Utf8JsonReader reader)
        {
            // Resolved on the first read rather than when the table is built: the context and the
            // union converters reach each other while they initialize.
            var typeInfo = _typeInfo ??= OpenCodeJsonContext.Default.GetTypeInfo(typeof(TArm)) as JsonTypeInfo<TArm>
                ?? throw new JsonException($"The generated context has no metadata for {typeof(TArm).Name}.");
            var start = reader;
            try
            {
                return ((JsonConverter<TArm>)typeInfo.Converter).Read(ref reader, typeof(TArm), typeInfo.Options);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or NotSupportedException)
            {
                // A direct converter call skips the serializer's error handling, which names the failing
                // member and turns a token-type failure into a JsonException. Reading the same payload
                // again through the serializer reports it as the arm's metadata always has.
                reader = start;
                return JsonSerializer.Deserialize(ref reader, typeInfo);
            }
        }
    }
}
