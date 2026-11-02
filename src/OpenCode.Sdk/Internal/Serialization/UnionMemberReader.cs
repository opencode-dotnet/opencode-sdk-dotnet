using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
#if !NET11_0_OR_GREATER
using System.Text.Json.Serialization.Metadata;
#endif

namespace OpenCode.Sdk.Internal;

/// <summary>Retains member converters and options-specific named-number metadata without reflection.</summary>
internal static class UnionMemberReader
{
    // Downlevel ConditionalWeakTable lacks the collection-initializer surface required by [].
#if NET
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> SpecialOptions = [];
#else
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> SpecialOptions = new();
#endif

    internal static T? Read<T>(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
#if NET11_0_OR_GREATER
        var info = options.GetTypeInfo<T>();
#else
        var info = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
#endif
        return ((JsonConverter<T>)info.Converter).Read(ref reader, typeof(T), options);
    }

    internal static T? ReadSpecialNumbers<T>(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var special = SpecialOptions.GetValue(options, static original =>
        {
            var copy = new JsonSerializerOptions(original)
            {
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                TypeInfoResolver = null,
            };
            var context = new OpenCodeJsonContext(copy);
            return context.Options;
        });
#if NET11_0_OR_GREATER
        return JsonSerializer.Deserialize(ref reader, special.GetTypeInfo<T>());
#else
        return JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<T>)special.GetTypeInfo(typeof(T)));
#endif
    }
}
