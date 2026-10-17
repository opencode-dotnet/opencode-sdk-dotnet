using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Walls;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal sealed class SchemaProjector
{
    private readonly JsonStringSchemaProjector _jsonStringProjector;
    private readonly ObjectSchemaProjector _objectProjector;
    private readonly PrefixItemsAdapter _prefixItemsAdapter;
    private readonly UnionNormalizer _unionNormalizer;
    private readonly SchemaShapeClassifier _shapeClassifier;

    public SchemaProjector()
    {
        _shapeClassifier = new SchemaShapeClassifier();
        _jsonStringProjector = new JsonStringSchemaProjector(this);
        _objectProjector = new ObjectSchemaProjector(this);
        _unionNormalizer = new UnionNormalizer(this, _shapeClassifier);
        _prefixItemsAdapter = new PrefixItemsAdapter(this);
    }

    public SchemaNode? Project(IOpenApiSchema schema, string root, string pointer, ProjectionState state)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(state);

        var location = string.Concat(root, pointer);
        if (schema is OpenApiSchemaReference reference)
        {
            return state.Register(ProjectReference(reference, location, state), root, pointer, location);
        }

        if (schema is not OpenApiSchema concrete)
        {
            state.Errors.Add(location, $"schema implementation '{schema.GetType().Name}' is not supported");
            return null;
        }

        if (!state.TryVisit(concrete))
        {
            state.Errors.Add(location, "schema identity was visited more than once");
            return null;
        }

        var errorCount = state.Errors.Count;
        SchemaWallPolicy.Check(concrete, location, state.Errors);
        if (state.Errors.Count != errorCount)
        {
            return null;
        }

        var projected = ProjectConcrete(concrete, root, pointer, location, state);
        return state.Register(projected, root, pointer, location);
    }

    private SchemaNode? ProjectConcrete(OpenApiSchema schema, string root, string pointer, string location, ProjectionState state)
    {
        if (schema is { AnyOf.Count: > 0, OneOf.Count: > 0 })
        {
            return _unionNormalizer.Project(schema, root, pointer, location, state);
        }

        return (_shapeClassifier.Classify(schema, location, state.Errors), schema.Type) switch
        {
            (CoreSchemaShape.Primitive, JsonSchemaType.String) => CreatePrimitive(schema, PrimitiveKind.String),
            (CoreSchemaShape.Primitive, JsonSchemaType.Number) => CreatePrimitive(schema, PrimitiveKind.Number),
            (CoreSchemaShape.Primitive, JsonSchemaType.Integer) => CreatePrimitive(schema, PrimitiveKind.Integer),
            (CoreSchemaShape.Primitive, JsonSchemaType.Boolean) => CreatePrimitive(schema, PrimitiveKind.Boolean),
            (CoreSchemaShape.Array, _) => ProjectArray(schema, root, pointer, state),
            (CoreSchemaShape.Object, _) => _objectProjector.Project(schema, root, pointer, location, state),
            (CoreSchemaShape.Enum, _) => EnumSchemaProjector.Project(schema, location, state.Errors),
            (CoreSchemaShape.Literal, _) => LiteralClassifier.Project(schema, location, state.Errors),
            (CoreSchemaShape.Union, _) => _unionNormalizer.Project(schema, root, pointer, location, state),
            (CoreSchemaShape.Null, _) => RefuseStandaloneNull(location, state),
            (CoreSchemaShape.Tuple, _) => _prefixItemsAdapter.Project(schema, root, pointer, location, state),
            (CoreSchemaShape.JsonString, _) => _jsonStringProjector.Project(schema, root, pointer, location, state),
            (CoreSchemaShape.EncodedString, _) => CreateEncodedString(schema),
            (CoreSchemaShape.Unrestricted, _) => CreateUnrestricted(schema),
            (CoreSchemaShape.Never, _) => ProjectNegation(schema),
            _ => null,
        };
    }

    /// <summary>
    /// Projects the two negations the wall admits. <c>not: {}</c> admits nothing.
    /// <c>not: {"type":"null"}</c> is how Effect renders a struct with no declared fields; nothing
    /// says what the content is, so it is an object of unspecified content.
    /// </summary>
    private static SchemaNode ProjectNegation(OpenApiSchema schema) =>
        schema.Not is OpenApiSchema { Type: JsonSchemaType.Null }
            ? new FreeFormObjectNode
            {
                Description = schema.Description,
                Format = schema.Format,
            }
            : new NeverNode();

    private ArrayNode? ProjectArray(OpenApiSchema schema, string root, string pointer, ProjectionState state)
    {
        // A bare {"type":"array"} constrains its elements to nothing: JSON Schema semantics
        // admit any value, so the item is the explicit any-value node (the unrestricted-{}
        // rule); mapping it to anything narrower would silently narrow the wire.
        if (schema.Items is null)
        {
            return new ArrayNode
            {
                Description = schema.Description,
                Format = schema.Format,
                Item = new UnrestrictedNode(),
            };
        }

        return ProjectArrayItems(schema, schema.Items, root, pointer, state);
    }

    internal ArrayNode? ProjectArrayItems(OpenApiSchema schema, IOpenApiSchema items, string root, string pointer, ProjectionState state)
    {
        var item = Project(items, root, GraphKeyBuilder.Append(pointer, "items"), state);
        return item is null
            ? null
            : new ArrayNode
            {
                Description = schema.Description,
                Format = schema.Format,
                Item = item,
            };
    }

    private static EncodedStringNode CreateEncodedString(OpenApiSchema schema) =>
        new()
        {
            Description = schema.Description,
            Format = schema.Format,
            ContentEncoding = schema.ContentEncoding!,
        };

    private static PrimitiveNode CreatePrimitive(OpenApiSchema schema, PrimitiveKind kind) =>
        new()
        {
            Description = schema.Description,
            Format = schema.Format,
            Kind = kind,
        };

    private static UnrestrictedNode CreateUnrestricted(OpenApiSchema schema) =>
        new()
        {
            Description = schema.Description,
            Format = schema.Format,
        };

    private static RefNode? ProjectReference(OpenApiSchemaReference reference, string location, ProjectionState state)
    {
        var target = reference.Reference.Id;
        if (!string.IsNullOrWhiteSpace(target) && reference.Target is not null)
        {
            return new RefNode
            {
                Target = target,
            };
        }

        state.Errors.Add(location, $"reference target '{target ?? "<missing>"}' could not be resolved");
        return null;
    }

    private static SchemaNode? RefuseStandaloneNull(string location, ProjectionState state)
    {
        state.Errors.Add(location, "null schemas are supported only as union branches");
        return null;
    }
}
