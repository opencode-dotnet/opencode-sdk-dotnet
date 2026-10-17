using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal static class LiteralClassifier
{
    public static LiteralMarker? FindFirstMarker(IOpenApiSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        foreach (var property in RequiredProperties(schema))
        {
            if (property.Value is not OpenApiSchema propertySchema)
            {
                continue;
            }

            var marker = CreateMarker(property.Key, propertySchema);
            if (marker is not null)
            {
                return marker;
            }
        }

        return null;
    }

    public static PrefixMarker? FindFirstPrefixMarker(IOpenApiSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        foreach (var property in RequiredProperties(schema))
        {
            if (property.Value is not OpenApiSchema { Type: JsonSchemaType.String, Enum: null, Const: null, Format: null, Pattern: { } pattern }
                || TemplateLiteralPrefixPolicy.TryDecodePrefix(pattern) is not { } prefix)
            {
                continue;
            }

            return new PrefixMarker { PropertyName = property.Key, Prefix = prefix };
        }

        return null;
    }

    private static IEnumerable<KeyValuePair<string, IOpenApiSchema>> RequiredProperties(IOpenApiSchema schema)
    {
        var candidate = schema is OpenApiSchemaReference reference ? reference.Target : schema;
        return candidate is OpenApiSchema { Properties: not null, Required: not null } objectSchema
            ? objectSchema.Properties.Where(property => objectSchema.Required.Contains(property.Key))
            : [];
    }

    public static LiteralNode? Project(OpenApiSchema schema, string location, IngestionErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentNullException.ThrowIfNull(errors);

        if (schema.Const is not null)
        {
            if (schema.Type is JsonSchemaType.String)
            {
                return new LiteralNode
                {
                    Description = schema.Description,
                    Format = schema.Format,
                    Kind = LiteralKind.String,
                    Value = schema.Const,
                    Dialect = LiteralDialect.Const,
                };
            }

            errors.Add(location, "const literals are supported only on string schemas");
            return null;
        }

        if (schema.Enum is not { Count: 1 } values)
        {
            errors.Add(location, "boolean enums with multiple values are not supported");
            return null;
        }

        if (schema.Type is JsonSchemaType.String)
        {
            return ProjectStringEnum(schema, values[0], location, errors);
        }

        if (schema.Type is JsonSchemaType.Boolean)
        {
            return ProjectBooleanEnum(schema, values[0], location, errors);
        }

        return schema.Type is JsonSchemaType.Number or JsonSchemaType.Integer
            ? ProjectNumberEnum(schema, values[0], location, errors)
            : RefuseUnsupportedEnum(location, errors);
    }

    private static LiteralNode? ProjectStringEnum(OpenApiSchema schema, JsonNode? value, string location, IngestionErrorCollector errors)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return new LiteralNode
            {
                Description = schema.Description,
                Format = schema.Format,
                Kind = LiteralKind.String,
                Value = text,
                Dialect = LiteralDialect.SingleValueEnum,
            };
        }

        errors.Add(location, "single-value string enum must contain a string");
        return null;
    }

    private static LiteralNode? ProjectBooleanEnum(OpenApiSchema schema, JsonNode? value, string location, IngestionErrorCollector errors)
    {
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var boolean))
        {
            return new LiteralNode
            {
                Description = schema.Description,
                Format = schema.Format,
                Kind = LiteralKind.Boolean,
                Value = boolean ? "true" : "false",
                Dialect = LiteralDialect.SingleValueEnum,
            };
        }

        errors.Add(location, "single-value boolean enum must contain a boolean");
        return null;
    }

    private static LiteralNode? ProjectNumberEnum(OpenApiSchema schema, JsonNode? value, string location, IngestionErrorCollector errors)
    {
        if (value is JsonValue jsonValue && jsonValue.GetValueKind() is System.Text.Json.JsonValueKind.Number)
        {
            return new LiteralNode
            {
                Description = schema.Description,
                Format = schema.Format,
                Kind = LiteralKind.Number,
                Value = jsonValue.ToJsonString(),
                Dialect = LiteralDialect.SingleValueEnum,
            };
        }

        errors.Add(location, "single-value number enum must contain a number");
        return null;
    }

    private static LiteralNode? RefuseUnsupportedEnum(string location, IngestionErrorCollector errors)
    {
        errors.Add(location, "single-value enums are supported only on string, number, or boolean schemas");
        return null;
    }

    private static LiteralMarker? CreateMarker(string propertyName, OpenApiSchema schema)
    {
        if (schema is { Type: JsonSchemaType.String, Const: not null, Enum: null })
        {
            return new LiteralMarker
            {
                PropertyName = propertyName,
                Kind = LiteralKind.String,
                Value = schema.Const,
            };
        }

        if (schema is not { Enum: [JsonValue value], Const: null })
        {
            return null;
        }

        if (schema.Type is JsonSchemaType.String && value.TryGetValue<string>(out var text))
        {
            return new LiteralMarker
            {
                PropertyName = propertyName,
                Kind = LiteralKind.String,
                Value = text,
            };
        }

        if (schema.Type is JsonSchemaType.Boolean && value.TryGetValue<bool>(out var boolean))
        {
            return new LiteralMarker
            {
                PropertyName = propertyName,
                Kind = LiteralKind.Boolean,
                Value = boolean ? "true" : "false",
            };
        }

        return null;
    }
}
