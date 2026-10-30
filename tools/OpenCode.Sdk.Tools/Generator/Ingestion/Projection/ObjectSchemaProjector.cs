using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal sealed class ObjectSchemaProjector
{
    private readonly SchemaProjector _schemaProjector;

    public ObjectSchemaProjector(SchemaProjector schemaProjector)
    {
        _schemaProjector = schemaProjector ?? throw new ArgumentNullException(nameof(schemaProjector));
    }

    public SchemaNode? Project(OpenApiSchema schema, string root, string pointer, string location, ProjectionState state)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentNullException.ThrowIfNull(state);

        if (schema.PatternProperties is { Count: > 0 })
        {
            return ProjectPatternDictionary(schema, root, pointer, location, state);
        }

        if (schema.Properties is not null || !schema.AdditionalPropertiesAllowed)
        {
            return ProjectObject(schema, root, pointer, state);
        }

        if (schema.AdditionalProperties is not null)
        {
            return ProjectDictionary(schema, schema.AdditionalProperties, root, GraphKeyBuilder.Append(pointer, "additionalProperties"), state);
        }

        return new FreeFormObjectNode
        {
            Description = schema.Description,
            Format = schema.Format,
        };
    }

    private DictionaryNode? ProjectPatternDictionary(OpenApiSchema schema, string root, string pointer, string location, ProjectionState state)
    {
        if (schema.PatternProperties is not { Count: 1 } patterns)
        {
            state.Errors.Add(location, "patternProperties must contain exactly one entry");
            return null;
        }

        if (schema.Properties is not null)
        {
            state.Errors.Add(location, "patternProperties cannot be combined with properties");
            return null;
        }

        // additionalProperties: false only restates that no key outside the pattern is admitted,
        // which the dictionary does not check; a schema for the other keys refuses.
        if (schema.AdditionalProperties is not null)
        {
            state.Errors.Add(location, "patternProperties cannot be combined with additionalProperties");
            return null;
        }

        var pattern = patterns.Single();
        var valuePointer = GraphKeyBuilder.Append(GraphKeyBuilder.Append(pointer, "patternProperties"), pattern.Key);
        return ProjectDictionary(schema, pattern.Value, root, valuePointer, state);
    }

    private DictionaryNode? ProjectDictionary(OpenApiSchema schema, IOpenApiSchema valueSchema, string root, string valuePointer,
        ProjectionState state)
    {
        var value = _schemaProjector.Project(valueSchema, root, valuePointer, state);
        return value is null
            ? null
            : new DictionaryNode
            {
                Description = schema.Description,
                Format = schema.Format,
                Value = value,
            };
    }

    private ObjectNode? ProjectObject(OpenApiSchema schema, string root, string pointer, ProjectionState state)
    {
        var properties = schema.Properties ?? new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        var missingRequired = schema.Required?.Where(name => !properties.ContainsKey(name)).ToArray() ?? [];
        if (missingRequired.Length > 0)
        {
            var requiredLocation = string.Concat(root, GraphKeyBuilder.Append(pointer, "required"));
            foreach (var missing in missingRequired)
            {
                state.Errors.Add(requiredLocation, $"required property '{missing}' is not declared in properties");
            }

            return null;
        }

        var projectedProperties = new List<SpecProperty>(properties.Count);
        var failed = false;
        foreach (var property in properties)
        {
            var projected = _schemaProjector.Project(property.Value, root, GraphKeyBuilder.Append(GraphKeyBuilder.Append(pointer, "properties"), property.Key), state);
            var isRequired = schema.Required?.Contains(property.Key) is true;
            failed |= projected is null;

            // An optional never member says the key must be absent; it carries no value, so it is no property.
            if (projected is null || (projected is NeverNode && !isRequired))
            {
                continue;
            }

            projectedProperties.Add(new SpecProperty
            {
                Name = property.Key,
                Schema = projected,
                IsRequired = isRequired,
            });
        }

        if (failed)
        {
            return null;
        }

        SchemaNode? additionalPropertiesSchema = null;
        var (additionalPropertiesSource, additionalPropertiesPointer) = ResolveAdditionalProperties(schema, pointer);
        if (additionalPropertiesSource is not null && additionalPropertiesPointer is not null)
        {
            additionalPropertiesSchema = _schemaProjector.Project(additionalPropertiesSource, root, additionalPropertiesPointer, state);
            if (additionalPropertiesSchema is null)
            {
                return null;
            }
        }

        var markers = CollectLiteralMarkers(projectedProperties);
        var prefixMarkers = CollectPrefixMarkers(projectedProperties, properties);

        var additionalProperties = AdditionalPropertiesKind.Open;
        if (additionalPropertiesSchema is not null)
        {
            additionalProperties = AdditionalPropertiesKind.Schema;
        }
        else if (!schema.AdditionalPropertiesAllowed)
        {
            additionalProperties = AdditionalPropertiesKind.Forbidden;
        }

        return new ObjectNode
        {
            Description = schema.Description,
            Format = schema.Format,
            Properties = projectedProperties,
            AdditionalProperties = additionalProperties,
            AdditionalPropertiesSchema = additionalPropertiesSchema,
            LiteralMarkers = markers,
            PrefixMarkers = prefixMarkers,
            ErrorStyle = ErrorStyleClassifier.Classify(projectedProperties),
        };
    }

    /// <summary>
    /// The host's own additionalProperties, or the rest wrapper's when the host carries the
    /// StructWithRest encoding (<see cref="RestWrapperPolicy"/>); the pointer names where the
    /// document really says it, so a promoted value keeps an honest graph key.
    /// </summary>
    private static (IOpenApiSchema? Schema, string? Pointer) ResolveAdditionalProperties(OpenApiSchema schema, string pointer)
    {
        if (schema.AdditionalProperties is { } own)
        {
            return (own, GraphKeyBuilder.Append(pointer, "additionalProperties"));
        }

        if (RestWrapperPolicy.TryGetRest(schema) is { } rest)
        {
            return (rest, GraphKeyBuilder.Append(GraphKeyBuilder.Append(GraphKeyBuilder.Append(pointer, "allOf"), "0"), "additionalProperties"));
        }

        return (null, null);
    }

    private static LiteralMarker[] CollectLiteralMarkers(IReadOnlyList<SpecProperty> projectedProperties) =>
    [
        .. projectedProperties
            .Where(static property => property is { IsRequired: true, Schema: LiteralNode })
            .Select(static property =>
            {
                var literal = (LiteralNode)property.Schema;
                return new LiteralMarker
                {
                    PropertyName = property.Name,
                    Kind = literal.Kind,
                    Value = literal.Value,
                };
            }),
    ];

    private static List<PrefixMarker> CollectPrefixMarkers(IReadOnlyList<SpecProperty> projectedProperties,
        IDictionary<string, IOpenApiSchema> properties)
    {
        var prefixMarkers = new List<PrefixMarker>();
        foreach (var projected in projectedProperties)
        {
            if (!projected.IsRequired
                || projected.Schema is not PrimitiveNode { Kind: PrimitiveKind.String, Format: null }
                || !properties.TryGetValue(projected.Name, out var rawProperty)
                || rawProperty is not OpenApiSchema { Pattern: { } pattern }
                || TemplateLiteralPrefixPolicy.TryDecodePrefix(pattern) is not { } prefix)
            {
                continue;
            }

            prefixMarkers.Add(new PrefixMarker { PropertyName = projected.Name, Prefix = prefix });
        }

        return prefixMarkers;
    }
}
