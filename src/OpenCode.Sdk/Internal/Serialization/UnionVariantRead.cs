using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>Reads one concrete union variant using its context's options.</summary>
internal delegate TArm UnionVariantRead<out TArm>(ref Utf8JsonReader reader, JsonSerializerOptions options);
