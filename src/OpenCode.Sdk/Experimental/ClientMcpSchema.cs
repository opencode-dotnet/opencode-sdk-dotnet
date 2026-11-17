using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace OpenCode.Sdk;

/// <summary>Exports tool input schemas from caller-supplied JSON metadata without reflection.</summary>
[Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
public static class ClientMcpSchema
{
    /// <summary>Exports an independently owned object-root schema, retaining nested nullability.</summary>
    /// <typeparam name="T">The object or dictionary contract.</typeparam>
    /// <param name="typeInfo">The source-generated metadata; no reflection fallback is used.</param>
    /// <returns>The schema, not a runtime argument validator.</returns>
    public static JsonElement FromType<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (typeInfo.Kind is not (JsonTypeInfoKind.Object or JsonTypeInfoKind.Dictionary))
        {
            throw new NotSupportedException("Tool input schemas require object or dictionary metadata.");
        }

        var schema = (JsonObject)typeInfo.GetJsonSchemaAsNode();
        schema["type"] = "object";
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }
}
