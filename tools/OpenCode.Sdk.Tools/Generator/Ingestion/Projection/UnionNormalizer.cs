using System.Globalization;
using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Walls;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal sealed class UnionNormalizer
{
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

        if (SpecialNumberPolicy.IsWhole(sourceBranches))
        {
            return ProjectSpecialNumber(schema, sourceBranches, string.Concat(root, GraphKeyBuilder.Append(pointer, keywordName)), state.Errors);
        }

        if (OpenEnumPolicy.ProjectWhole(schema, sourceBranches) is { } wholeOpenEnum)
        {
            return CheckBranchWalls(sourceBranches, string.Concat(root, GraphKeyBuilder.Append(pointer, keywordName)), state.Errors) ? wholeOpenEnum : null;
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

    /// <summary>A whole special-number union is one special-number node once each member passes the wall, nested members included.</summary>
    private static SpecialNumberNode? ProjectSpecialNumber(OpenApiSchema schema, IList<IOpenApiSchema> branches, string keywordLocation,
        IngestionErrorCollector errors)
    {
        if (!CheckBranchWalls(branches, keywordLocation, errors))
        {
            return null;
        }

        return new SpecialNumberNode
        {
            Description = schema.Description,
            Format = schema.Format,
        };
    }

    /// <summary>A union whose branches fold into one node never projects them, so the wall checks each, and each nested anyOf member.</summary>
    private static bool CheckBranchWalls(IList<IOpenApiSchema> branches, string keywordLocation, IngestionErrorCollector errors)
    {
        var errorCount = errors.Count;
        for (var index = 0; index < branches.Count; index++)
        {
            if (branches[index] is not OpenApiSchema branch)
            {
                continue;
            }

            var branchLocation = GraphKeyBuilder.Append(keywordLocation, index.ToString(CultureInfo.InvariantCulture));
            SchemaWallPolicy.Check(branch, branchLocation, errors);
            if (branch.AnyOf is not { Count: > 0 } nested)
            {
                continue;
            }

            for (var innerIndex = 0; innerIndex < nested.Count; innerIndex++)
            {
                if (nested[innerIndex] is OpenApiSchema inner)
                {
                    SchemaWallPolicy.Check(inner, GraphKeyBuilder.Append(GraphKeyBuilder.Append(branchLocation, "anyOf"), innerIndex.ToString(CultureInfo.InvariantCulture)), errors);
                }
            }
        }

        return errors.Count == errorCount;
    }

    private ProjectedBranch[]? ProjectBranches(IList<IOpenApiSchema> sourceBranches, string root, string pointer, string keyword,
        ProjectionState state)
    {
        var specialNumber = SpecialNumberPolicy.FindFlat(sourceBranches);
        var openEnum = OpenEnumPolicy.Find(sourceBranches, specialNumber);
        var branches = new List<ProjectedBranch>(sourceBranches.Count);
        var failed = false;
        for (var index = 0; index < sourceBranches.Count; index++)
        {
            var source = sourceBranches[index];
            if (specialNumber?.Contains(index) == true || openEnum?.Contains(index) == true)
            {
                // A folded member is never projected on its own, so the wall checks it here.
                var foldPointer = GraphKeyBuilder.UnionBranch(pointer, keyword, index, marker: null);
                var errorCount = state.Errors.Count;
                SchemaWallPolicy.Check((OpenApiSchema)source, string.Concat(root, foldPointer), state.Errors);
                failed |= state.Errors.Count != errorCount;
                if (index == specialNumber?[0])
                {
                    branches.Add(new ProjectedBranch(new SpecialNumberNode(), Marker: null, IsNull: false, IsMarkedUnionReference: false));
                }
                else if (index == openEnum?[0]
                         && state.Register(new EnumNode { Values = OpenEnumPolicy.Values(sourceBranches, openEnum) }, root, foldPointer, string.Concat(root, foldPointer)) is { } registered)
                {
                    branches.Add(new ProjectedBranch(registered, Marker: null, IsNull: false, IsMarkedUnionReference: false));
                }

                continue;
            }

            var marker = LiteralClassifier.FindFirstMarker(source);
            var prefixMarker = marker is null ? LiteralClassifier.FindFirstPrefixMarker(source) : null;
            var isMarkedUnionReference = marker is null && ReferencesMarkedUnion(source);
            var ownKey = marker is null && prefixMarker is null ? OwnRequiredKeyPolicy.Find(sourceBranches, index) : null;
            var branchPointer = ownKey is null
                ? GraphKeyBuilder.UnionBranch(pointer, keyword, index, marker)
                : GraphKeyBuilder.Append(GraphKeyBuilder.Append(pointer, keyword), ownKey);
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
        && (UnionBranchShape.IsPureAnyOfBranch(target) || UnionBranchShape.IsPureOneOfBranch(target))
        && (target.AnyOf is { Count: > 1 } ? target.AnyOf : target.OneOf) is { Count: > 1 } nested
        && nested.All(static branch => LiteralClassifier.FindFirstMarker(branch) is not null);

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

    private sealed record ProjectedBranch(SchemaNode? Node, LiteralMarker? Marker, bool IsNull, bool IsMarkedUnionReference, PrefixMarker? PrefixMarker = null);
}
