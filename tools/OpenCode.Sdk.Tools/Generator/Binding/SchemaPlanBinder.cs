using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class SchemaPlanBinder(
    StructuralUnionPlanBinder structuralUnions,
    UnionMembershipValidator unionMemberships,
    UnionDiscriminatorSelector discriminators,
    UnionMemberHoistBinder memberHoists)
{
    /// <summary>The prefix-marker list every branch that is not a marked object resolves to.</summary>
    private static readonly IReadOnlyList<PrefixMarker> NoPrefixMarkers = [];

    private readonly StringComparer _comparer = StringComparer.Ordinal;

    private readonly StructuralUnionPlanBinder _structuralUnions = structuralUnions
                                                                   ?? throw new ArgumentNullException(nameof(structuralUnions));

    private readonly UnionMembershipValidator _unionMemberships = unionMemberships
                                                                  ?? throw new ArgumentNullException(nameof(unionMemberships));

    private readonly UnionDiscriminatorSelector _discriminators = discriminators
                                                                  ?? throw new ArgumentNullException(nameof(discriminators));

    private readonly UnionMemberHoistBinder _memberHoists = memberHoists ?? throw new ArgumentNullException(nameof(memberHoists));

    public SchemaBindingResult Bind(SpecDocument document, ReachableSchemaSet reachable, GenerationCuration curation,
        IReadOnlyDictionary<string, string> typeNames, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reachable);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(typeNames);
        ArgumentNullException.ThrowIfNull(errors);

        var responseRoots = reachable.ResponseRootKeys.ToHashSet(_comparer);
        var requestKeys = reachable.RequestReachableKeys.ToHashSet(_comparer);
        var streamCauseKeys = reachable.StreamCauseKeys.ToHashSet(_comparer);
        var inhabitation = new SchemaInhabitationPolicy(document.Schemas);
        var inheritance = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unions = BindExplicitUnions(document, reachable, responseRoots, streamCauseKeys, typeNames, inheritance, inhabitation, errors);
        var errorUnion = BindErrorUnion(document, reachable, responseRoots, streamCauseKeys, typeNames, inheritance, errors);
        if (errorUnion is not null)
        {
            unions.Add(errorUnion);
        }

        var typeBinder = new TypePlanBinder(document.Schemas, typeNames, errors);
        var models = new List<ModelPlan>();
        foreach (var key in reachable.GraphKeys)
        {
            if (responseRoots.Contains(key) || !document.Schemas.TryGetValue(key, out var schema) || !typeNames.TryGetValue(key, out var name))
            {
                continue;
            }

            if (!inhabitation.IsInhabited(schema))
            {
                continue;
            }

            switch (schema)
            {
                case ObjectNode objectNode:
                    var objectPlan = BindObject(key, name, objectNode, inheritance, requestKeys.Contains(key), typeBinder, errors);
                    if (objectPlan is not null)
                    {
                        models.Add(objectPlan);
                    }

                    break;
                case EnumNode enumNode:
                    models.Add(BindEnum(key, name, enumNode, curation.EnumMemberNames, errors));
                    break;
                case UnionNode { Classification: UnionClassification.Structural } structural
                    when UnstructuredUnionPolicy.Collapse(structural, document.Schemas) is null:
                    var structuralPlan = _structuralUnions.Bind(key, name, structural, document.Schemas, typeBinder, errors);
                    if (structuralPlan is not null)
                    {
                        models.Add(structuralPlan);
                    }

                    break;
            }
        }

        var orderedModels = models.OrderBy(static model => model.Name, _comparer).ToArray();
        var orderedUnions = unions.OrderBy(static union => union.Name, _comparer).ToArray();
        _unionMemberships.Validate([.. orderedModels.OfType<ObjectModelPlan>()], orderedUnions, errors);
        // Hoisting reads the bound plans and only adds members to them, so it runs after the
        // membership wall and before the registry, which never lists a hoisted carrier: the
        // carrier is an interface no converter and no source-generated metadata materializes.
        var hoisted = _memberHoists.Bind(orderedUnions, orderedModels, curation, errors);
        orderedModels = [.. hoisted.Models];
        orderedUnions = [.. hoisted.Unions];
        return new SchemaBindingResult
        {
            Models = orderedModels,
            Unions = orderedUnions,
            HoistedInterfaces = hoisted.Interfaces,
            Registry = new RegistryPlan
            {
                TypeNames = ComposeRegistryNames(orderedModels, orderedUnions),
            },
        };
    }

    /// <summary>
    /// The types the serializer registry must carry. A hoisted carrier is never among them: it
    /// is an interface no converter names and no payload deserializes into.
    /// </summary>
    private string[] ComposeRegistryNames(IReadOnlyList<ModelPlan> models, IReadOnlyList<UnionPlan> unions) =>
    [
        .. models
            .Select(static model => model.Name)
            .Concat(models
                .OfType<StructuralUnionModelPlan>()
                .SelectMany(static model => model.Arms)
                .Where(static arm => arm.Type.IsCollection)
                .Select(static arm => TypeReferenceNamePolicy.Format(arm.Type)))
            .Concat(unions.SelectMany(static union => new[] { union.Name, union.UnknownTypeName, }))
            .Distinct(_comparer)
            .Order(_comparer),
    ];

    private List<UnionPlan> BindExplicitUnions(SpecDocument document, ReachableSchemaSet reachable, HashSet<string> responseRoots,
        HashSet<string> streamCauseKeys, IReadOnlyDictionary<string, string> names, Dictionary<string, List<string>> inheritance,
        SchemaInhabitationPolicy inhabitation, BindingErrorCollector errors)
    {
        var plans = new Dictionary<string, UnionPlan>(StringComparer.Ordinal);
        var fixedMarkers = new Dictionary<string, UnionFixedMarkerPlan>(StringComparer.Ordinal);
        foreach (var key in reachable.GraphKeys)
        {
            if (responseRoots.Contains(key)
                || !document.Schemas.TryGetValue(key, out var schema)
                || schema is not UnionNode { Classification: UnionClassification.Marked } union
                || !names.TryGetValue(key, out var name))
            {
                continue;
            }

            var plan = BindUnion(name, key, union, document.Schemas, names, inheritance, fixedMarkers, inhabitation, errors);
            if (plan is not null)
            {
                plans.Add(key, plan);
            }
        }

        // A nested union learns its base type and fixed outer marker while the outer union
        // binds, which can happen after the nested plan was built — the wiring is a post-pass.
        var result = new List<UnionPlan>(plans.Count);
        foreach (var (key, plan) in plans)
        {
            var wired = plan;
            if (inheritance.TryGetValue(key, out var baseTypes) && baseTypes.Count is 1)
            {
                wired = wired with
                {
                    BaseTypeName = baseTypes[0]
                };
            }

            if (fixedMarkers.TryGetValue(key, out var fixedMarker))
            {
                wired = wired with
                {
                    FixedMarker = fixedMarker
                };
            }

            if (streamCauseKeys.Contains(key))
            {
                wired = wired with
                {
                    BaseTypeName = EffectStreamTypeNamePolicy.CauseMarkerInterface
                };
            }

            result.Add(wired);
        }

        return result;
    }

    private UnionPlan? BindUnion(string name, string key, UnionNode union, IReadOnlyDictionary<string, SchemaNode> graph,
        IReadOnlyDictionary<string, string> names, Dictionary<string, List<string>> inheritance,
        Dictionary<string, UnionFixedMarkerPlan> fixedMarkers, SchemaInhabitationPolicy inhabitation, BindingErrorCollector errors)
    {
        var resolved = ResolveBranches(key, union, graph, names, inhabitation, errors);
        if (resolved is null)
        {
            return null;
        }

        var discriminator = _discriminators.Select(resolved);
        if (discriminator is null)
        {
            errors.Add(BindingErrorCategory.Schema, key, "marked union branches share no discriminating marker property");
            return null;
        }

        var prefixBranches = discriminator.PrefixBranches;
        if (prefixBranches.Count > 1)
        {
            var arms = string.Join(", ", prefixBranches.Select(static branch => $"'{branch.TypeName}'").Order(StringComparer.Ordinal));
            errors.Add(BindingErrorCategory.Schema, key, $"marked union declares more than one prefix-tagged arm ({arms}); at most one is admitted");
            return null;
        }

        var context = new UnionBindingContext
        {
            Key = key,
            Name = name,
            Marker = discriminator.Marker,
            Inheritance = inheritance,
            FixedMarkers = fixedMarkers,
            Errors = errors,
        };
        var literals = BindLiteralVariants(context, discriminator.LiteralBranches);
        UnionPrefixVariantPlan? prefixVariant = null;
        if (prefixBranches.Count is 1)
        {
            prefixVariant = BindPrefixVariant(context, prefixBranches[0], literals);
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
    /// Turns the literal-tagged branches into dispatch entries, recording each one's membership
    /// and, for a nested union, the outer marker it fixes. A branch whose schema admits no JSON
    /// value contributes its tag to the known-impossible set instead of a variant.
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

            AddInheritance(branch.MemberKey, context.Name, context.Inheritance, context.Errors);
            if (branch.IsNestedUnion)
            {
                context.FixedMarkers[branch.MemberKey] = new UnionFixedMarkerPlan
                {
                    WireName = marker.PropertyName,
                    Name = CSharpNamePolicy.ToPascalCase(marker.PropertyName),
                    Kind = marker.Kind,
                    Value = tag,
                };
            }

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
    /// Admits the one prefix-tagged arm of a marked union, or refuses naming the wall it hit.
    /// The arm dispatches on the union's own marker after every literal tag, so it must be a
    /// direct component branch, tag that same string marker, admit a JSON value, and claim no
    /// value a declared literal tag already owns.
    /// </summary>
    private static UnionPrefixVariantPlan? BindPrefixVariant(UnionBindingContext context, ResolvedUnionBranch branch,
        UnionLiteralVariants literals)
    {
        var key = context.Key;
        var marker = context.Marker;
        var errors = context.Errors;

        // Only an object carries prefix markers, so a nested-union branch can never be the arm;
        // a promoted inline object can, and its member key is the only thing that says so.
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

        // A literal tag inside the arm's span would be claimed by whichever dispatch ran first,
        // so the overlap refuses rather than silently ordering one over the other.
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

        AddInheritance(branch.MemberKey, context.Name, context.Inheritance, errors);
        return new UnionPrefixVariantPlan
        {
            TypeName = branch.TypeName,
            Prefix = prefixMarker.Prefix,
            MarkerWireName = marker.PropertyName,
        };
    }

    private List<ResolvedUnionBranch>? ResolveBranches(string key, UnionNode union, IReadOnlyDictionary<string, SchemaNode> graph,
        IReadOnlyDictionary<string, string> names, SchemaInhabitationPolicy inhabitation, BindingErrorCollector errors)
    {
        var resolved = new List<ResolvedUnionBranch>(union.Branches.Count);
        foreach (var branch in union.Branches)
        {
            if (branch is not RefNode reference
                || !graph.TryGetValue(reference.Target, out var target)
                || !names.TryGetValue(reference.Target, out var typeName))
            {
                errors.Add(
                    BindingErrorCategory.Schema,
                    key,
                    "marked union branch must reference a named object or nested marked union with a literal or prefix marker");
                continue;
            }

            // A prefix tag is matched against the outer union's own marker, so an arm one level
            // down would have to be reached through a converter that never sees that marker.
            if (target is UnionNode nestedUnion && FindPrefixLeaf(nestedUnion, graph, names, inhabitation) is { } prefixLeaf)
            {
                errors.Add(
                    BindingErrorCategory.Schema,
                    key,
                    $"prefix-tagged arm '{prefixLeaf}' must be a direct component branch of the marked union, "
                    + $"not a branch of nested union '{typeName}'");
                continue;
            }

            // A nested union that fixes the outer marker behaves like one tag. One that
            // discriminates on the outer marker instead spans it, so the outer dispatches
            // straight to that union's own leaves rather than through a second converter.
            if (target is UnionNode { Classification: UnionClassification.Marked } nested
                && ResolveUniformNestedMarkers(nested, graph) is not { Count: > 0 })
            {
                var spanned = ResolveSpannedBranches(nested, reference.Target, graph, names, inhabitation);
                if (spanned is null)
                {
                    errors.Add(
                        BindingErrorCategory.Schema,
                        key,
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
                errors.Add(
                    BindingErrorCategory.Schema,
                    key,
                    "marked union branch must reference a named object or nested marked union with a literal or prefix marker");
                continue;
            }

            resolved.Add(new ResolvedUnionBranch(
                typeName,
                markers ?? [],
                prefixMarkers,
                target is UnionNode,
                reference.Target,
                inhabitation.IsInhabited(target)));
        }

        // A spanning branch contributes several entries under one member key, so the arity
        // check counts branches answered rather than entries produced.
        var answered = resolved.Select(static branch => branch.MemberKey).Distinct(StringComparer.Ordinal).Count();
        return answered == union.Branches.Count ? resolved : null;
    }

    /// <summary>Expands a marker-spanning nested union into the leaves the outer dispatches to.</summary>
    private static List<ResolvedUnionBranch>? ResolveSpannedBranches(UnionNode nested, string nestedKey,
        IReadOnlyDictionary<string, SchemaNode> graph, IReadOnlyDictionary<string, string> names,
        SchemaInhabitationPolicy inhabitation)
    {
        var result = new List<ResolvedUnionBranch>(nested.Branches.Count);
        foreach (var branch in nested.Branches)
        {
            // Only one level is admitted: a leaf of a spanning union must itself be a marked
            // object, never another union.
            if (branch is not RefNode reference
                || !graph.TryGetValue(reference.Target, out var target)
                || target is not ObjectNode { LiteralMarkers.Count: > 0 } leaf
                || !names.TryGetValue(reference.Target, out var typeName))
            {
                return null;
            }

            // A prefix leaf under a nested union is refused before the spanning expansion runs,
            // so every leaf reached here carries literal markers only.
            result.Add(new ResolvedUnionBranch(
                typeName,
                leaf.LiteralMarkers,
                PrefixMarkers: [],
                IsNestedUnion: false,
                nestedKey,
                inhabitation.IsInhabited(leaf)));
        }

        return result;
    }

    /// <summary>
    /// Names the leaf that tags the nested union's own discriminating property with a prefix, or
    /// null when it has none. The same selection runs here as for the outer union, so a leaf
    /// whose prefix markers sit on other properties — a templated identifier beside a literal
    /// tag — is an ordinary variant of the nested union rather than an arm buried one level down.
    /// </summary>
    private string? FindPrefixLeaf(UnionNode nested, IReadOnlyDictionary<string, SchemaNode> graph,
        IReadOnlyDictionary<string, string> names, SchemaInhabitationPolicy inhabitation)
    {
        var leaves = new List<ResolvedUnionBranch>(nested.Branches.Count);
        foreach (var branch in nested.Branches)
        {
            if (branch is not RefNode reference
                || !graph.TryGetValue(reference.Target, out var target)
                || target is not ObjectNode leaf
                || !names.TryGetValue(reference.Target, out var leafName))
            {
                return null;
            }

            leaves.Add(new ResolvedUnionBranch(
                leafName,
                leaf.LiteralMarkers,
                leaf.PrefixMarkers,
                IsNestedUnion: false,
                reference.Target,
                inhabitation.IsInhabited(leaf)));
        }

        var arms = leaves.Count is 0 ? null : _discriminators.Select(leaves)?.PrefixBranches;
        return arms is { Count: > 0 } ? arms[0].TypeName : null;
    }

    /// <summary>
    /// Resolves the markers every variant of a nested union fixes to one shared value — for
    /// those properties the whole nested union behaves like a single tag.
    /// </summary>
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
                .Where(candidate => variantMarkers
                    .Skip(1)
                    .All(markers => markers.Any(other =>
                        string.Equals(other.PropertyName, candidate.PropertyName, StringComparison.Ordinal)
                        && other.Kind == candidate.Kind
                        && string.Equals(other.Value, candidate.Value, StringComparison.Ordinal)))),
        ];
    }

    private static UnionPlan? BindErrorUnion(SpecDocument document, ReachableSchemaSet reachable, HashSet<string> responseRoots,
        HashSet<string> streamCauseKeys, IReadOnlyDictionary<string, string> names,
        IDictionary<string, List<string>> inheritance, BindingErrorCollector errors)
    {
        var errorsInClosure = new List<KeyValuePair<string, ObjectNode>>();
        foreach (var key in reachable.GraphKeys)
        {
            if (!responseRoots.Contains(key)
                && !streamCauseKeys.Contains(key)
                && document.Schemas.TryGetValue(key, out var schema)
                && schema is ObjectNode { ErrorStyle: not ErrorStyle.None } objectNode)
            {
                errorsInClosure.Add(new KeyValuePair<string, ObjectNode>(key, objectNode));
            }
        }

        if (errorsInClosure.Count is 0)
        {
            return null;
        }

        var variants = new List<UnionVariantPlan>(errorsInClosure.Count);
        foreach (var (key, objectNode) in errorsInClosure.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!names.TryGetValue(key, out var typeName))
            {
                errors.Add(BindingErrorCategory.Naming, key, "error schema has no unique C# type name");
                continue;
            }

            if (ErrorMarkerPolicy.Resolve(objectNode, out var problem) is not { } marker)
            {
                errors.Add(BindingErrorCategory.Schema, key, problem);
                continue;
            }

            AddInheritance(key, CSharpNamePolicy.ToUnionInterfaceName("OpenCodeError"), inheritance, errors);
            variants.Add(new UnionVariantPlan
            {
                TypeName = typeName,
                Tag = marker.Value,
                MarkerWireName = marker.PropertyName,
            });
        }

        if (variants.Count is 0 || !HasUniqueErrorTags(variants, errors))
        {
            return null;
        }

        return new UnionPlan
        {
            Name = CSharpNamePolicy.ToUnionInterfaceName("OpenCodeError"),
            ConceptName = "OpenCodeError",
            Namespace = GeneratedNamespace.Models,
            UnknownTypeName = "UnknownOpenCodeError",
            MarkerWireName = ErrorMarkerPolicy.WireName,
            MarkerName = "Tag",
            MarkerKind = LiteralKind.String,
            Variants = [.. variants.OrderBy(static variant => variant.TypeName, StringComparer.Ordinal)],
            Description = "Represents a typed error returned by the opencode API.",
        };
    }

    /// <summary>
    /// One tag owned by two closure types would poison the converter's dispatch map at its
    /// first use, and the reader's per-status filter reads the same one member, so a collision
    /// refuses naming its owners; structurally identical duplicates
    /// have the schema-alias escape. Returns whether the tags are unique - the caller reads it
    /// as the go-ahead, so the name says what true means.
    /// </summary>
    private static bool HasUniqueErrorTags(IReadOnlyList<UnionVariantPlan> variants, BindingErrorCollector errors)
    {
        var duplicates = variants
            .GroupBy(static variant => variant.Tag, StringComparer.Ordinal)
            .Where(static group => group.Skip(1).Any())
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in duplicates)
        {
            var owners = string.Join(", ", group.Select(static variant => variant.TypeName).Order(StringComparer.Ordinal));
            errors.Add(BindingErrorCategory.Schema, "OpenCodeError", $"multiple error schemas declare tag '{group.Key}' ({owners})");
        }

        return duplicates.Length is 0;
    }

    private static ObjectModelPlan? BindObject(string key, string name, ObjectNode node, Dictionary<string, List<string>> inheritance,
        bool isRequestReachable, TypePlanBinder typeBinder, BindingErrorCollector errors)
    {
        if (!TryAdmitAdditionalProperties(key, node, errors, out var emitsExtensionData))
        {
            return null;
        }

        var properties = new List<ModelPropertyPlan>(node.Properties.Count);
        foreach (var property in node.Properties)
        {
            var type = typeBinder.Bind(key, property.Name, property.Schema);
            if (type is null)
            {
                continue;
            }

            // Read before the widening below: at this instant IsNullable still means exactly
            // "the schema admits JSON null", which is half of what the tri-state wrapper is
            // keyed on. After the widening it means "optional or nullable" and the two are
            // no longer separable (ADR-0004).
            var emitsOptionalWrapper = isRequestReachable && !property.IsRequired && type.IsNullable;
            if (!property.IsRequired)
            {
                type = type with
                {
                    IsNullable = true
                };
            }

            var literal = property.Schema as LiteralNode;
            properties.Add(new ModelPropertyPlan
            {
                WireName = property.Name,
                Name = CSharpNamePolicy.ToPascalCase(property.Name),
                Type = type,
                IsRequired = property.IsRequired,
                IsLiteral = literal is not null,
                EmitsOptionalWrapper = emitsOptionalWrapper,
                LiteralKind = literal?.Kind,
                LiteralValue = literal?.Value,
                Description = property.Schema.Description,
            });
        }

        if (properties.Count != node.Properties.Count)
        {
            return null;
        }

        var duplicate = properties
            .GroupBy(static property => property.Name, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Skip(1).Any());

        if (duplicate is not null)
        {
            errors.Add(BindingErrorCategory.Naming, key, $"multiple properties map to C# name '{duplicate.Key}'");
            return null;
        }

        if (emitsExtensionData && TakesExtensionDataMemberName(key, properties, errors))
        {
            return null;
        }

        return new ObjectModelPlan
        {
            Name = name,
            Namespace = GeneratedNamespace.Models,
            Description = node.Description,
            Properties = properties,
            ImplementedUnionNames = inheritance.TryGetValue(key, out var implemented) ? implemented : [],
            EmitsExtensionData = emitsExtensionData,
        };
    }

    /// <summary>
    /// Only a bag that admits any value rides beside named properties without loss: JsonElement
    /// carries every token exactly as sent, so the record gains the extension-data member. A
    /// typed bag would need a member no record has, so it keeps refusing. Returns whether the
    /// object binds at all; the out value says whether it carries the bag.
    /// </summary>
    private static bool TryAdmitAdditionalProperties(string key, ObjectNode node, BindingErrorCollector errors, out bool emitsExtensionData)
    {
        emitsExtensionData = false;
        if (node.AdditionalProperties is not AdditionalPropertiesKind.Schema)
        {
            return true;
        }

        if (node.AdditionalPropertiesSchema is not (UnrestrictedNode or NullableNode { Inner: UnrestrictedNode }))
        {
            errors.Add(
                BindingErrorCategory.Schema,
                string.Concat(key, "/additionalProperties"),
                "named properties and typed additional properties cannot be represented without data loss; only an unrestricted bag rides as extension data");
            return false;
        }

        emitsExtensionData = true;
        return true;
    }

    /// <summary>A named property whose C# name is one of the two extension-data members' refuses the object rather than shadowing them.</summary>
    private static bool TakesExtensionDataMemberName(string key, IEnumerable<ModelPropertyPlan> properties, BindingErrorCollector errors)
    {
        var taken = properties.FirstOrDefault(static property =>
            string.Equals(property.Name, ObjectModelPlan.ExtensionDataMemberName, StringComparison.Ordinal)
            || string.Equals(property.Name, ObjectModelPlan.ExtensionDataBagName, StringComparison.Ordinal));
        if (taken is null)
        {
            return false;
        }

        errors.Add(
            BindingErrorCategory.Naming,
            key,
            $"a property maps to C# name '{taken.Name}', which the extension-data member takes");
        return true;
    }

    /// <summary>Each value's member name comes from <see cref="EnumMemberNamePolicy"/>.</summary>
    private static EnumModelPlan BindEnum(string key, string name, EnumNode node,
        IReadOnlyList<EnumMemberNameCuration> memberNames, BindingErrorCollector errors)
    {
        var values = node
            .Values.Select(value => new EnumValuePlan
            {
                Name = EnumMemberNamePolicy.Name(key, value, memberNames),
                WireValue = value,
            })
            .ToArray();
        var duplicate = values.GroupBy(static value => value.Name, StringComparer.Ordinal).FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            errors.Add(BindingErrorCategory.Naming, name, $"multiple enum values map to C# name '{duplicate.Key}'");
        }

        return new EnumModelPlan
        {
            Name = name,
            Namespace = GeneratedNamespace.Models,
            Description = node.Description,
            Values = values,
        };
    }

    /// <summary>
    /// Records one union a schema is a branch of. A schema can be a branch of several, so
    /// membership accumulates rather than refusing the second (ADR-0011).
    /// </summary>
    private static void AddInheritance(string schemaKey, string unionName, IDictionary<string, List<string>> inheritance,
        BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (!inheritance.TryGetValue(schemaKey, out var unions))
        {
            unions = [];
            inheritance[schemaKey] = unions;
        }

        if (!unions.Contains(unionName, StringComparer.Ordinal))
        {
            unions.Add(unionName);
        }
    }
}
