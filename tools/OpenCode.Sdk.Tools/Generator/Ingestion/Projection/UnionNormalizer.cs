using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Walls;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal sealed class UnionNormalizer
{
    private static readonly HashSet<string> SpecialNumberValues = new(StringComparer.Ordinal)
    {
        "NaN",
        "Infinity",
        "-Infinity",
    };

    private readonly SchemaProjector _schemaProjector;
    private readonly SchemaShapeClassifier _shapeClassifier;

    public UnionNormalizer(SchemaProjector schemaProjector, SchemaShapeClassifier shapeClassifier)
    {
        _schemaProjector = schemaProjector ?? throw new ArgumentNullException(nameof(schemaProjector));
        _shapeClassifier = shapeClassifier ?? throw new ArgumentNullException(nameof(shapeClassifier));
    }

    public SchemaNode? Project(OpenApiSchema schema, string root, string pointer, string location, ProjectionState state)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentNullException.ThrowIfNull(state);

        if (schema is { AnyOf.Count: > 0, OneOf.Count: > 0 })
        {
            state.Errors.Add(location, "anyOf and oneOf cannot be combined on one schema");
            return null;
        }

        var keyword = schema.OneOf is { Count: > 0 } ? UnionKeyword.OneOf : UnionKeyword.AnyOf;
        var keywordName = keyword is UnionKeyword.OneOf ? "oneOf" : "anyOf";
        var sourceBranches = keyword is UnionKeyword.OneOf ? schema.OneOf : schema.AnyOf;
        if (sourceBranches is null)
        {
            state.Errors.Add(location, $"{keywordName} must contain at least one branch");
            return null;
        }

        if (IsSpecialNumber(sourceBranches))
        {
            if (!ValidateSpecialNumberBranches(sourceBranches, root, pointer, keywordName, state))
            {
                return null;
            }

            return new SpecialNumberNode
            {
                Description = schema.Description,
                Format = schema.Format,
            };
        }

        var branches = ProjectBranches(sourceBranches, root, pointer, keywordName, state);
        if (branches is null)
        {
            return null;
        }

        var deduplicated = DeduplicateReferences(branches);
        var hasNull = deduplicated.Any(static branch => branch.IsNull);
        var nonNullBranches = deduplicated.Where(static branch => !branch.IsNull).ToArray();
        if (nonNullBranches.Length is 0)
        {
            state.Errors.Add(location, "union normalization produced zero non-null branches");
            return null;
        }

        var inner = CreateInner(nonNullBranches, keyword);
        if (hasNull)
        {
            return new NullableNode
            {
                Description = schema.Description,
                Format = schema.Format,
                Inner = inner,
            };
        }

        if (nonNullBranches.Length is 1)
        {
            return inner with
            {
                Description = schema.Description ?? inner.Description,
                Format = schema.Format ?? inner.Format,
            };
        }

        return inner with
        {
            Description = schema.Description,
            Format = schema.Format,
        };
    }

    private static bool ValidateSpecialNumberBranches(IList<IOpenApiSchema> branches, string root, string pointer, string keyword, ProjectionState state)
    {
        var errorCount = state.Errors.Count;
        for (var index = 0; index < branches.Count; index++)
        {
            if (branches[index] is not OpenApiSchema branch)
            {
                continue;
            }

            var branchPointer = GraphKeyBuilder.UnionBranch(pointer, keyword, index, marker: null);
            SchemaWallPolicy.Check(branch, string.Concat(root, branchPointer), state.Errors);
            if (branch.AnyOf is not { Count: > 0 } nested)
            {
                continue;
            }

            for (var innerIndex = 0; innerIndex < nested.Count; innerIndex++)
            {
                if (nested[innerIndex] is not OpenApiSchema inner)
                {
                    continue;
                }

                var innerPointer = GraphKeyBuilder.UnionBranch(branchPointer, "anyOf", innerIndex, marker: null);
                SchemaWallPolicy.Check(inner, string.Concat(root, innerPointer), state.Errors);
            }
        }

        return state.Errors.Count == errorCount;
    }

    private ProjectedBranch[]? ProjectBranches(IList<IOpenApiSchema> sourceBranches, string root, string pointer, string keyword,
        ProjectionState state)
    {
        var branches = new List<ProjectedBranch>(sourceBranches.Count);
        var failed = false;
        for (var index = 0; index < sourceBranches.Count; index++)
        {
            var source = sourceBranches[index];
            var marker = LiteralClassifier.FindFirstMarker(source);
            var prefixMarker = marker is null ? LiteralClassifier.FindFirstPrefixMarker(source) : null;
            var isMarkedUnionReference = marker is null && ReferencesMarkedUnion(source);
            var branchPointer = GraphKeyBuilder.UnionBranch(pointer, keyword, index, marker);
            var branchLocation = string.Concat(root, branchPointer);
            if (source is OpenApiSchema { Type: JsonSchemaType.Null } concrete)
            {
                var errorCount = state.Errors.Count;
                SchemaWallPolicy.Check(concrete, branchLocation, state.Errors);
                var shape = _shapeClassifier.Classify(concrete, branchLocation, state.Errors);
                if (state.Errors.Count == errorCount && shape is CoreSchemaShape.Null)
                {
                    branches.Add(new ProjectedBranch(Node: null, marker, IsNull: true, IsMarkedUnionReference: false));
                }
                else
                {
                    failed = true;
                }

                continue;
            }

            var projected = _schemaProjector.Project(source, root, branchPointer, state);
            if (projected is null)
            {
                failed = true;
                continue;
            }

            branches.Add(new ProjectedBranch(projected, marker, IsNull: false, isMarkedUnionReference, prefixMarker));
        }

        return failed ? null : [.. branches];
    }

    /// <summary>
    /// Accepts a branch referencing a nested marked union (exactly one level): every variant
    /// of the referenced union is itself a marked object. The Binder validates that the
    /// nested variants contribute one uniform outer tag; this records only markedness.
    /// </summary>
    private static bool ReferencesMarkedUnion(IOpenApiSchema source) =>
        source is OpenApiSchemaReference { Target: OpenApiSchema target }
        && (IsPureAnyOfBranch(target) || IsPureOneOfBranch(target))
        && (target.AnyOf is { Count: > 1 } ? target.AnyOf : target.OneOf) is { Count: > 1 } nested
        && nested.All(static branch => LiteralClassifier.FindFirstMarker(branch) is not null);

    /// <summary>The oneOf twin of <see cref="IsPureAnyOfBranch"/>; a nested marked union may use either keyword exclusively.</summary>
    private static bool IsPureOneOfBranch(OpenApiSchema schema) =>
        schema is { OneOf.Count: > 0, Type: null, Enum: null, Const: null, Items: null, Properties: null, AdditionalProperties: null }
        && schema.AnyOf is not { Count: > 0 }
        && schema.AllOf is not { Count: > 0 }
        && schema.Required is not { Count: > 0 }
        && schema.PatternProperties is not { Count: > 0 }
        && schema.AdditionalPropertiesAllowed
        && schema is { ContentEncoding: null, ContentMediaType: null, ContentSchema: null };

    private static ProjectedBranch[] DeduplicateReferences(IReadOnlyList<ProjectedBranch> branches)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        return
        [
            .. branches.Where(branch => branch.Node is not RefNode reference || targets.Add(reference.Target)),
        ];
    }

    private static SchemaNode CreateInner(IReadOnlyList<ProjectedBranch> branches, UnionKeyword keyword)
    {
        if (branches.Count is 1)
        {
            return branches[0].Node ?? throw new InvalidOperationException("A non-null union branch had no projected node.");
        }

        var classification = branches.All(static branch => branch.Marker is not null || branch.PrefixMarker is not null || branch.IsMarkedUnionReference)
            ? UnionClassification.Marked
            : UnionClassification.Structural;
        return new UnionNode
        {
            Branches =
            [
                .. branches.Select(static branch =>
                    branch.Node ?? throw new InvalidOperationException("A non-null union branch had no projected node.")),
            ],
            Keyword = keyword,
            Classification = classification,
        };
    }

    private static bool IsSpecialNumber(IEnumerable<IOpenApiSchema> branches)
    {
        var numberBranches = 0;
        var specialBranches = 0;
        return CountSpecialNumberBranches(branches, allowNested: true, ref numberBranches, ref specialBranches)
               && numberBranches is 1
               && specialBranches > 0;
    }

    private static bool CountSpecialNumberBranches(IEnumerable<IOpenApiSchema> branches, bool allowNested, ref int numberBranches,
        ref int specialBranches)
    {
        foreach (var branch in branches)
        {
            if (branch is not OpenApiSchema schema)
            {
                return false;
            }

            if (schema is { Type: JsonSchemaType.Number, Enum: null, Const: null } && !HasCompetingNodeConstraints(schema))
            {
                numberBranches++;
                continue;
            }

            // The v2 dialect nests the special-number union one level deeper
            // (anyOf: [anyOf of number-and-specials, a redundant specials enum]); exactly one
            // nesting level flattens, anything deeper or impure stays refused.
            if (allowNested && IsPureAnyOfBranch(schema))
            {
                if (!CountSpecialNumberBranches(schema.AnyOf!, allowNested: false, ref numberBranches, ref specialBranches))
                {
                    return false;
                }

                continue;
            }

            if (!IsSpecialStringBranch(schema))
            {
                return false;
            }

            specialBranches++;
        }

        return true;
    }

    private static bool IsPureAnyOfBranch(OpenApiSchema schema) =>
        schema is { AnyOf.Count: > 0, Type: null, Enum: null, Const: null, Items: null, Properties: null, AdditionalProperties: null }
        && schema.OneOf is not { Count: > 0 }
        && schema.AllOf is not { Count: > 0 }
        && schema.Required is not { Count: > 0 }
        && schema.PatternProperties is not { Count: > 0 }
        && schema.AdditionalPropertiesAllowed
        && schema is { ContentEncoding: null, ContentMediaType: null, ContentSchema: null };

    private static bool IsSpecialStringBranch(OpenApiSchema schema)
    {
        if (schema.Type is not JsonSchemaType.String || HasCompetingNodeConstraints(schema))
        {
            return false;
        }

        if (schema is { Const: not null, Enum: null })
        {
            return SpecialNumberValues.Contains(schema.Const);
        }

        if (schema is not { Const: null, Enum.Count: > 0 } || schema.Enum is not { } values)
        {
            return false;
        }

        return values.All(static value => value is JsonValue jsonValue
                                          && jsonValue.TryGetValue<string>(out var text)
                                          && SpecialNumberValues.Contains(text));
    }

    private static bool HasCompetingNodeConstraints(OpenApiSchema schema) =>
        schema.AnyOf is { Count: > 0 }
        || schema.OneOf is { Count: > 0 }
        || schema.AllOf is { Count: > 0 }
        || schema.Required is { Count: > 0 }
        || schema.Items is not null
        || schema.Properties is not null
        || schema.PatternProperties is { Count: > 0 }
        || !schema.AdditionalPropertiesAllowed
        || schema.AdditionalProperties is not null
        || schema.ContentEncoding is not null
        || schema.ContentMediaType is not null
        || schema.ContentSchema is not null;

    private sealed record ProjectedBranch(SchemaNode? Node, LiteralMarker? Marker, bool IsNull, bool IsMarkedUnionReference, PrefixMarker? PrefixMarker = null);
}
