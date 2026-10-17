using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class MarkedUnionBinder(SchemaBindingContext context)
{
    private static readonly IReadOnlyList<PrefixMarker> NoPrefixMarkers = [];
    private readonly SchemaBindingContext _context = context;

    public UnionBindingResult Bind(string key, string name, UnionNode union)
    {
        var edges = new List<UnionChildEdge>();
        var plan = BindUnion(name, key, union, edges);
        return new UnionBindingResult(key, plan, edges.AsReadOnly());
    }

    private UnionPlan? BindUnion(string name, string key, UnionNode union, ICollection<UnionChildEdge> edges)
    {
        var resolved = ResolveBranches(key, union);
        if (resolved is null)
        {
            return null;
        }

        var discriminator = UnionDiscriminatorSelector.Select(resolved);
        if (discriminator is null)
        {
            _context.Errors.Add(BindingErrorCategory.Schema, key, "marked union branches share no discriminating marker property");
            return null;
        }

        var prefixBranches = discriminator.PrefixBranches;
        if (prefixBranches.Count > 1)
        {
            var arms = string.Join(", ", prefixBranches.Select(static branch => $"'{branch.TypeName}'").Order(StringComparer.Ordinal));
            _context.Errors.Add(BindingErrorCategory.Schema, key, $"marked union declares more than one prefix-tagged arm ({arms}); at most one is admitted");
            return null;
        }

        var binding = new UnionBindingContext
        {
            Key = key,
            Name = name,
            Marker = discriminator.Marker,
            ChildEdges = edges,
            Errors = _context.Errors,
        };
        var literals = BindLiteralVariants(binding, discriminator.LiteralBranches);
        UnionPrefixVariantPlan? prefixVariant = null;
        if (prefixBranches.Count is 1)
        {
            prefixVariant = BindPrefixVariant(binding, prefixBranches[0], literals);
            if (prefixVariant is null)
            {
                return null;
            }
        }

        var marker = discriminator.Marker;
        var conceptName = CSharpNamePolicy.ToUnionConceptName(name);
        return new UnionPlan
        {
            Name = name,
            ConceptName = conceptName,
            Namespace = GeneratedNamespace.Models,
            UnknownTypeName = $"Unknown{conceptName}",
            MarkerWireName = marker.PropertyName,
            MarkerName = CSharpNamePolicy.ToPascalCase(marker.PropertyName),
            MarkerKind = marker.Kind,
            Variants = literals.Variants,
            PrefixVariant = prefixVariant,
            KnownImpossibleTags = [.. literals.KnownImpossibleTags.Order(StringComparer.Ordinal)],
            Description = union.Description,
        };
    }

    /// <summary>
    /// Turns literal branches into dispatch entries and membership edges. An uninhabited branch
    /// contributes its tag to the known-impossible set instead of a variant.
    /// </summary>
    private static UnionLiteralVariants BindLiteralVariants(UnionBindingContext context, IReadOnlyList<ResolvedUnionBranch> branches)
    {
        var marker = context.Marker;
        var variants = new List<UnionVariantPlan>(branches.Count);
        var knownImpossibleTags = new List<string>();
        foreach (var branch in branches)
        {
            var tag = branch.Markers.First(candidate =>
                    string.Equals(candidate.PropertyName, marker.PropertyName, StringComparison.Ordinal))
                .Value;
            if (!branch.IsInhabited)
            {
                knownImpossibleTags.Add(tag);
                continue;
            }

            var fixedMarker = branch.IsNestedUnion ? new UnionFixedMarkerPlan
            {
                WireName = marker.PropertyName,
                Name = CSharpNamePolicy.ToPascalCase(marker.PropertyName),
                Kind = marker.Kind,
                Value = tag,
            } : null;
            context.ChildEdges.Add(new UnionChildEdge(branch.MemberKey, context.Name, fixedMarker));
            variants.Add(new UnionVariantPlan
            {
                TypeName = branch.TypeName,
                Tag = tag,
                MarkerWireName = marker.PropertyName,
                IsNestedUnion = branch.IsNestedUnion,
            });
        }

        return new UnionLiteralVariants(variants, knownImpossibleTags);
    }

    /// <summary>
    /// Admits one direct, inhabited prefix arm on the union's string marker. Its span cannot
    /// overlap a literal tag, because dispatch order must not decide who owns a value.
    /// </summary>
    private static UnionPrefixVariantPlan? BindPrefixVariant(UnionBindingContext context, ResolvedUnionBranch branch,
        UnionLiteralVariants literals)
    {
        var key = context.Key;
        var marker = context.Marker;
        var errors = context.Errors;
        // Only an object carries prefix markers; a promoted inline object's key distinguishes it
        // from a direct component arm.
        if (branch.MemberKey.Contains('#', StringComparison.Ordinal))
        {
            errors.Add(BindingErrorCategory.Schema, key,
                $"prefix-tagged arm '{branch.TypeName}' must be a direct component branch of the marked union, not a promoted inline branch");
            return null;
        }

        var prefixMarker = branch.PrefixMarkers.FirstOrDefault(candidate =>
            string.Equals(candidate.PropertyName, marker.PropertyName, StringComparison.Ordinal));
        if (prefixMarker is null)
        {
            errors.Add(BindingErrorCategory.Schema, key,
                $"prefix-tagged arm '{branch.TypeName}' tags '{branch.PrefixMarkers[0].PropertyName}' "
                + $"but the union discriminates on '{marker.PropertyName}'");
            return null;
        }

        if (marker.Kind is not LiteralKind.String)
        {
            errors.Add(BindingErrorCategory.Schema, key,
                $"prefix-tagged arm '{branch.TypeName}' requires a string marker; the union discriminates on a '{marker.Kind}' marker");
            return null;
        }

        if (!branch.IsInhabited)
        {
            errors.Add(BindingErrorCategory.Schema, key, $"prefix-tagged arm '{branch.TypeName}' admits no JSON value");
            return null;
        }

        var covered = literals.Variants.FirstOrDefault(variant => variant.Tag.StartsWith(prefixMarker.Prefix, StringComparison.Ordinal));
        var coveredTag = covered?.Tag
                         ?? literals.KnownImpossibleTags.FirstOrDefault(tag => tag.StartsWith(prefixMarker.Prefix, StringComparison.Ordinal));
        if (coveredTag is not null)
        {
            errors.Add(BindingErrorCategory.Schema, key,
                $"prefix-tagged arm '{branch.TypeName}' prefix '{prefixMarker.Prefix}' "
                + $"is a prefix of literal tag '{coveredTag}' ('{covered?.TypeName ?? "(known-impossible)"}')");
            return null;
        }

        context.ChildEdges.Add(new UnionChildEdge(branch.MemberKey, context.Name));
        return new UnionPrefixVariantPlan
        {
            TypeName = branch.TypeName,
            Prefix = prefixMarker.Prefix,
            MarkerWireName = marker.PropertyName,
        };
    }

    private List<ResolvedUnionBranch>? ResolveBranches(string key, UnionNode union)
    {
        var graph = _context.Document.Schemas;
        var names = _context.TypeNames;
        var inhabitation = _context.Inhabitation;
        var errors = _context.Errors;
        var resolved = new List<ResolvedUnionBranch>(union.Branches.Count);
        foreach (var branch in union.Branches)
        {
            if (branch is not RefNode reference
                || !graph.TryGetValue(reference.Target, out var target)
                || !names.TryGetValue(reference.Target, out var typeName))
            {
                errors.Add(BindingErrorCategory.Schema, key,
                    "marked union branch must reference a named object or nested marked union with a literal or prefix marker");
                continue;
            }

            // A nested prefix arm's converter never sees the outer marker.
            if (target is UnionNode nestedUnion && FindPrefixLeaf(nestedUnion) is { } prefixLeaf)
            {
                errors.Add(BindingErrorCategory.Schema, key,
                    $"prefix-tagged arm '{prefixLeaf}' must be a direct component branch of the marked union, "
                    + $"not a branch of nested union '{typeName}'");
                continue;
            }

            // Uniform markers make the nested union one tag; otherwise dispatch spans its leaves.
            if (target is UnionNode { Classification: UnionClassification.Marked } nested
                && ResolveUniformNestedMarkers(nested, graph) is not { Count: > 0 })
            {
                var spanned = ResolveSpannedBranches(nested, reference.Target);
                if (spanned is null)
                {
                    errors.Add(BindingErrorCategory.Schema, key,
                        "marked union branch must reference a named object or nested marked union with a literal marker");
                    continue;
                }

                resolved.AddRange(spanned);
                continue;
            }

            var markers = target switch
            {
                ObjectNode { LiteralMarkers.Count: > 0 } objectNode => objectNode.LiteralMarkers,
                UnionNode { Classification: UnionClassification.Marked } uniform => ResolveUniformNestedMarkers(uniform, graph),
                _ => null,
            };
            var prefixMarkers = target is ObjectNode prefixed ? prefixed.PrefixMarkers : NoPrefixMarkers;
            if (markers is not { Count: > 0 } && prefixMarkers.Count is 0)
            {
                errors.Add(BindingErrorCategory.Schema, key,
                    "marked union branch must reference a named object or nested marked union with a literal or prefix marker");
                continue;
            }

            resolved.Add(new ResolvedUnionBranch(typeName, markers ?? [], prefixMarkers,
                target is UnionNode, reference.Target, inhabitation.IsInhabited(target)));
        }

        // A spanning branch contributes several entries under one member key.
        var answered = resolved.Select(static branch => branch.MemberKey).Distinct(StringComparer.Ordinal).Count();
        return answered == union.Branches.Count ? resolved : null;
    }

    /// <summary>Expands a marker-spanning nested union into the leaves the outer dispatches to.</summary>
    private List<ResolvedUnionBranch>? ResolveSpannedBranches(UnionNode nested, string nestedKey)
    {
        var result = new List<ResolvedUnionBranch>(nested.Branches.Count);
        foreach (var branch in nested.Branches)
        {
            // Only one level is admitted: a spanning leaf must itself be a marked object.
            if (branch is not RefNode reference
                || !_context.Document.Schemas.TryGetValue(reference.Target, out var target)
                || target is not ObjectNode { LiteralMarkers.Count: > 0 } leaf
                || !_context.TypeNames.TryGetValue(reference.Target, out var typeName))
            {
                return null;
            }

            // Prefix leaves are refused before this expansion runs.
            result.Add(new ResolvedUnionBranch(typeName, leaf.LiteralMarkers, PrefixMarkers: [],
                IsNestedUnion: false, nestedKey, _context.Inhabitation.IsInhabited(leaf)));
        }

        return result;
    }

    /// <summary>
    /// Names a nested leaf tagging its own discriminating property with a prefix. Prefixes on
    /// other properties do not make an ordinary literal variant a buried prefix arm.
    /// </summary>
    private string? FindPrefixLeaf(UnionNode nested)
    {
        var leaves = new List<ResolvedUnionBranch>(nested.Branches.Count);
        foreach (var branch in nested.Branches)
        {
            if (branch is not RefNode reference
                || !_context.Document.Schemas.TryGetValue(reference.Target, out var target)
                || target is not ObjectNode leaf
                || !_context.TypeNames.TryGetValue(reference.Target, out var leafName))
            {
                return null;
            }

            leaves.Add(new ResolvedUnionBranch(leafName, leaf.LiteralMarkers, leaf.PrefixMarkers,
                IsNestedUnion: false, reference.Target, _context.Inhabitation.IsInhabited(leaf)));
        }

        var arms = leaves.Count is 0 ? null : UnionDiscriminatorSelector.Select(leaves)?.PrefixBranches;
        return arms is { Count: > 0 } ? arms[0].TypeName : null;
    }

    /// <summary>Finds markers every variant of a nested union fixes to one shared value.</summary>
    private static IReadOnlyList<LiteralMarker>? ResolveUniformNestedMarkers(UnionNode nested,
        IReadOnlyDictionary<string, SchemaNode> graph)
    {
        var variantMarkers = new List<IReadOnlyList<LiteralMarker>>(nested.Branches.Count);
        foreach (var branch in nested.Branches)
        {
            if (branch is not RefNode reference
                || !graph.TryGetValue(reference.Target, out var target)
                || target is not ObjectNode { LiteralMarkers.Count: > 0 } objectNode)
            {
                return null;
            }

            variantMarkers.Add(objectNode.LiteralMarkers);
        }

        return
        [
            .. variantMarkers[0]
                .Where(candidate => variantMarkers.Skip(1).All(markers => markers.Any(other =>
                    string.Equals(other.PropertyName, candidate.PropertyName, StringComparison.Ordinal)
                    && other.Kind == candidate.Kind
                    && string.Equals(other.Value, candidate.Value, StringComparison.Ordinal)))),
        ];
    }
}
