using System.Globalization;
using System.Text.Json;
using Microsoft.OpenApi;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal sealed class SchemaShapeClassifier
{
    private readonly ConstraintRule[] _constraintRules =
    [
        new(Constraint.Type, "type", static schema => schema.Type is not null),
        new(Constraint.Const, "const", static schema => schema.Const is not null),
        new(Constraint.Enum, "enum", static schema => schema.Enum is { Count: > 0 }),
        new(Constraint.OneOf, "oneOf", static schema => schema.OneOf is { Count: > 0 }),
        new(Constraint.AnyOf, "anyOf", static schema => schema.AnyOf is { Count: > 0 }),
        new(Constraint.AllOf, "allOf", static schema => schema.AllOf is { Count: > 0 }),
        new(Constraint.Required, "required", static schema => schema.Required is { Count: > 0 }),
        new(Constraint.Items, "items", static schema => schema.Items is not null),
        new(Constraint.Properties, "properties", static schema => schema.Properties is not null),
        new(Constraint.PatternProperties, "patternProperties", static schema => schema.PatternProperties is { Count: > 0 }),
        new(Constraint.AdditionalPropertiesAllowed, "additionalProperties", static schema => !schema.AdditionalPropertiesAllowed),
        new(Constraint.AdditionalProperties, "additionalProperties", static schema => schema.AdditionalProperties is not null),
        new(Constraint.ContentEncoding, "contentEncoding", static schema => schema.ContentEncoding is not null),
        new(Constraint.ContentMediaType, "contentMediaType", static schema => schema.ContentMediaType is not null),
        new(Constraint.ContentSchema, "contentSchema", static schema => schema.ContentSchema is not null),
        new(Constraint.PrefixItems, "prefixItems", static schema => schema.UnrecognizedKeywords?.ContainsKey("prefixItems") is true),
        new(Constraint.Not, "not", static schema => schema.Not is not null),
    ];

    public CoreSchemaShape Classify(OpenApiSchema schema, string location, IngestionErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentNullException.ThrowIfNull(errors);

        var populatedRules = _constraintRules.Where(rule => rule.IsPopulated(schema)).ToArray();
        var constraints = populatedRules.Aggregate(Constraint.None, static (current, rule) => current | rule.Constraint);

        // The v2 dialect wraps validation keywords in a single-element allOf
        // ({"type":"integer","allOf":[{"exclusiveMinimum":0}]}). A wrapper carrying only the
        // validation/annotation vocabulary the projection deliberately ignores unwraps to the
        // host's own shape; a wrapper with anything structural, consumed (format), referencing,
        // applicator-typed, or unrecognized keeps the fail-closed refusal below.
        if (constraints is (Constraint.Type | Constraint.AllOf) && IsValidationOnlyAllOfWrapper(schema))
        {
            constraints = Constraint.Type;
        }

        // Effect's StructWithRest lands the struct's properties on the host beside a
        // single-element allOf carrying only the rest's additionalProperties. The wrapper says
        // nothing the host's own additionalProperties could not, so it reads as exactly that
        // and the object shape below admits it; every other allOf keeps the refusal.
        if (constraints.HasFlag(Constraint.AllOf) && RestWrapperPolicy.TryGetRest(schema) is not null)
        {
            constraints = (constraints & ~Constraint.AllOf) | Constraint.AdditionalProperties;
        }

        var shape = (constraints, schema.Type) switch
        {
            (Constraint.None, _) => CoreSchemaShape.Unrestricted,
            (Constraint.Type, JsonSchemaType.String or JsonSchemaType.Number or JsonSchemaType.Integer or JsonSchemaType.Boolean) => CoreSchemaShape
                .Primitive,
            (Constraint.Type | Constraint.Enum, JsonSchemaType.String) when schema.Enum?.Count is 1 => CoreSchemaShape.Literal,
            (Constraint.Type | Constraint.Enum, JsonSchemaType.String) => CoreSchemaShape.Enum,
            (Constraint.Type | Constraint.Enum, JsonSchemaType.Boolean) => CoreSchemaShape.Literal,
            (Constraint.Type | Constraint.Enum, JsonSchemaType.Number or JsonSchemaType.Integer) when schema.Enum?.Count is 1 => CoreSchemaShape
                .Literal,
            (Constraint.Type | Constraint.Const, _) => CoreSchemaShape.Literal,
            (Constraint.AnyOf, _) => CoreSchemaShape.Union,
            (Constraint.OneOf, _) => CoreSchemaShape.Union,
            (Constraint.Type, JsonSchemaType.Null) => CoreSchemaShape.Null,
            (Constraint.Type | Constraint.PrefixItems, JsonSchemaType.Array) => CoreSchemaShape.Tuple,
            (Constraint.Type | Constraint.Items | Constraint.PrefixItems, JsonSchemaType.Array) => CoreSchemaShape.Tuple,
            (Constraint.Type | Constraint.ContentSchema, JsonSchemaType.String) => CoreSchemaShape.JsonString,
            (Constraint.Type | Constraint.ContentMediaType | Constraint.ContentSchema, JsonSchemaType.String) => CoreSchemaShape.JsonString,
            (Constraint.Type | Constraint.ContentEncoding, JsonSchemaType.String) => CoreSchemaShape.EncodedString,
            (Constraint.Not, _) => CoreSchemaShape.Never,
            (Constraint.Type, JsonSchemaType.Array) => CoreSchemaShape.Array,
            (Constraint.Type | Constraint.Items, JsonSchemaType.Array) => CoreSchemaShape.Array,
            _ when IsObjectShape(constraints, schema.Type) => CoreSchemaShape.Object,
            _ => CoreSchemaShape.Unsupported,
        };

        if (shape is not CoreSchemaShape.Unsupported)
        {
            return shape;
        }

        var names = string.Join(", ", populatedRules.Select(rule => rule.WireName).Distinct(StringComparer.Ordinal));
        errors.Add(location, $"schema node-kind constraints '{names}' do not form a supported core shape");

        return shape;
    }

    /// <summary>Recognizes a reference intersected only with annotations, independently of typed wrappers.</summary>
    public static OpenApiSchemaReference? AnnotatedReference(OpenApiSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Type is not null || schema.AllOf is not { Count: > 1 })
        {
            return null;
        }

        var references = schema.AllOf.OfType<OpenApiSchemaReference>().ToArray();
        if (references.Length is not 1 || references[0].Reference.UnrecognizedKeywords is { Count: > 0 })
        {
            return null;
        }

        // Inspect the reader's complete keyword vocabulary, not the structural constraint list:
        // validation keywords that other projection paths ignore are not annotations here.
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        schema.SerializeAsV31(new OpenApiJsonWriter(text));
        using var document = JsonDocument.Parse(text.ToString());
        var host = document.RootElement;
        return host.EnumerateObject().All(static property => property.NameEquals("allOf") || IsAnnotationKeyword(property.Name))
            && host.GetProperty("allOf").EnumerateArray().All(static element => element.EnumerateObject()
                .All(static property => property.NameEquals("$ref") || IsAnnotationKeyword(property.Name)))
                ? references[0]
                : null;
    }

    private bool IsValidationOnlyAllOfWrapper(OpenApiSchema schema)
    {
        if (schema.AllOf is not { Count: 1 } || schema.AllOf[0] is not OpenApiSchema element)
        {
            return false;
        }

        return !_constraintRules.Any(rule => rule.IsPopulated(element)) && AllOfWrapperKeywords.AreAbsent(element);
    }

    private static bool IsObjectShape(Constraint constraints, JsonSchemaType? type)
    {
        const Constraint objectConstraints = Constraint.Type | Constraint.Required | Constraint.Properties | Constraint.PatternProperties
                                             | Constraint.AdditionalPropertiesAllowed | Constraint.AdditionalProperties;
        if ((constraints & ~objectConstraints) is not Constraint.None || type is not (null or JsonSchemaType.Object))
        {
            return false;
        }

        var objectMembers = constraints & ~Constraint.Type;
        if (type is JsonSchemaType.Object && objectMembers is Constraint.None)
        {
            return true;
        }

        if (constraints.HasFlag(Constraint.Required) && !constraints.HasFlag(Constraint.Properties))
        {
            return false;
        }

        return objectMembers is not Constraint.None;
    }

    private static bool IsAnnotationKeyword(string name) =>
        name is "description" or "title" or "examples" or "deprecated" or "readOnly" or "writeOnly";

    [Flags]
    private enum Constraint
    {
        None = 0,
        Type = 1 << 0,
        Const = 1 << 1,
        Enum = 1 << 2,
        OneOf = 1 << 3,
        AnyOf = 1 << 4,
        AllOf = 1 << 5,
        Required = 1 << 6,
        Items = 1 << 7,
        Properties = 1 << 8,
        PatternProperties = 1 << 9,
        AdditionalPropertiesAllowed = 1 << 10,
        AdditionalProperties = 1 << 11,
        ContentEncoding = 1 << 12,
        ContentMediaType = 1 << 13,
        ContentSchema = 1 << 14,
        PrefixItems = 1 << 15,
        Not = 1 << 16,
    }

    private sealed record ConstraintRule(Constraint Constraint, string WireName, Func<OpenApiSchema, bool> IsPopulated);
}
