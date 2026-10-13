using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Walls;

/// <summary>Admits only the exact typed-applicator semantics exercised by the pinned dialect.</summary>
internal static class TypedApplicatorPolicy
{
    public static void Check(OpenApiSchema schema, string location, IngestionErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentNullException.ThrowIfNull(errors);

        if (schema.Not is null)
        {
            return;
        }

        var serialized = Serialize(schema);
        if (serialized is JsonObject { Count: 1 } host
            && host["not"] is JsonObject negated
            && IsAdmitted(negated))
        {
            return;
        }

        errors.Add(string.Concat(location, "/not"), "schema keyword 'not' is supported only as the standalone forms 'not: {}' and 'not: {\"type\":\"null\"}'");
    }

    /// <summary>
    /// Admits <c>not: {}</c>, the dialect's never schema, and <c>not: {"type":"null"}</c>, how
    /// Effect renders a struct with no declared fields.
    /// </summary>
    private static bool IsAdmitted(JsonObject negated) =>
        negated.Count is 0
        || (negated.Count is 1
            && negated["type"] is JsonValue type
            && type.TryGetValue<string>(out var name)
            && string.Equals(name, "null", StringComparison.Ordinal));

    private static JsonNode? Serialize(OpenApiSchema schema)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new OpenApiJsonWriter(text);
        schema.SerializeAsV31(writer);
        return JsonNode.Parse(text.ToString());
    }
}
