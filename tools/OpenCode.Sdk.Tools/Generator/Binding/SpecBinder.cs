using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Binding.Abstractions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class SpecBinder : ISpecBinder
{
    public EmitPlan Bind(SpecDocument document, OperationSelection selection, GenerationCuration curation)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(curation);

        var errors = new BindingErrorCollector();
        var operationsById = document.Operations.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);
        var selected = SelectOperations(selection, operationsById, errors);
        var reachable = ReachableSchemaCollector.Collect(document, selected, errors);
        CurationRowContract.ValidateBeforeAliases(curation, errors);
        // Upstream stabilize's per-site duplicates collapse mechanically, ahead of curation:
        // curated rows are then judged against the graph as it will be bound, and a row that
        // repeats a mechanical fold is refused as redundant instead of shadowing it.
        var collapse = StabilizeDuplicatePolicy.Resolve(document, reachable, curation.SchemaNames, errors);
        var context = new BindingContext(document, reachable, curation, errors);
        CurationValidator.Validate(context, selected);
        SchemaAliasValidator.Validate(context, collapse);

        // Aliases are validated against the raw document above; the collapse happens before
        // names resolve so the rest of the pipeline never sees the duplicates. Reachability
        // recollects on the transformed document because an alias target's promoted inline
        // children only become reachable through the rewritten references.
        var aliased = SchemaAliasApplier.Apply(document, collapse, curation.SchemaAliases);
        if (!ReferenceEquals(aliased, document))
        {
            document = aliased;
            var aliasedById = document.Operations.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);
            selected = Array.AsReadOnly([.. selected.Select(operation => aliasedById[operation.OperationId])]);
            reachable = ReachableSchemaCollector.Collect(document, selected, errors);
            context = context with { Document = document, Reachable = reachable };
        }

        curation = CurationValidator.ValidateCollapsedSchemas(context, collapse);

        // Type names are resolved exactly once per bind; schema and operation binding
        // consume the same map so neither can reinterpret a schema under another name.
        var typeNames = SchemaNameResolver.Resolve(document, reachable, selected, curation, errors);
        var schemaContext = SchemaBindingContext.Create(document, reachable, typeNames, errors);
        var schemaResult = new SchemaPlanBinder(schemaContext).Bind(curation);
        var clients = OperationPlanBinder.Bind(document, selected, curation, schemaResult, errors);
        var candidates = new OwnershipCandidates
        {
            Models = schemaResult.Models,
            Unions = schemaResult.Unions,
            HoistedInterfaces = schemaResult.HoistedInterfaces,
        };
        var inventory = GeneratedTypeNameInventory.Create(candidates, clients, schemaResult.HoistedClaims, typeNames);
        var refusedHoists = OperationPlanBinder.CheckTypeNameCollisions(inventory, clients, errors);
        var pending = PendingOperations(document, selected, curation);

        var masked = BindModelFacets(schemaContext, schemaResult, clients, curation, collapse);
        CheckDtoNameCollisions(inventory, errors);
        candidates = candidates with { Models = masked.Models, Unions = masked.Unions, HoistedInterfaces = [.. candidates.HoistedInterfaces.Where(plan => !refusedHoists.Contains(plan.Name))] };
        InternalNameCollisionCheck.Check(inventory, errors);
        // An unplaced model never refuses a bind: it is the generate command's wall, so the
        // pending-operation probe still judges bindability under its synthetic curation.
        var placement = ModelOwnershipPolicy.Place(candidates, clients, curation, errors);
        var reach = ModelReachCollector.Collect(new ReachScope(document, selected, clients, typeNames), candidates, errors);
        var experimental = ExperimentalModelPolicy.Collect(new ReachScope(document, selected, clients, typeNames), candidates, inventory, errors);
        CheckStreamFallbacks(candidates, clients, errors);
        errors.ThrowIfAny();
        var plan = new EmitPlan
        {
            Namespaces = new(GeneratedNamespace.Public, GeneratedNamespace.Models, GeneratedNamespace.Internal),
            SelectedOperationIds = selection.OperationIds,
            Models = [.. masked.Models.Select(model => model with { ExperimentalDiagnostic = experimental.GetValueOrDefault(model.Name) })],
            Unions = [.. masked.Unions.Select(union => union with { ExperimentalDiagnostic = experimental.GetValueOrDefault(union.Name) })],
            HoistedInterfaces = [.. schemaResult.HoistedInterfaces.Select(plan => plan with { ExperimentalDiagnostic = experimental.GetValueOrDefault(plan.Name) })],
            Registry = ComposeRegistry(schemaResult.Registry, clients),
            Clients = clients,
            PendingOperations = pending,
            TransportOwnedOperationIds = [.. curation.TransportOwned.Select(static row => row.OperationId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            DeclinedOperations = DeclinedOperations(curation),
            ImplicitAliases = collapse,
            Placement = placement,
            ModelReach = reach,
        };
        plan = DescriptionCurationBinder.Apply(WireDocumentationPolicy.Apply(ResolveEmissionData(plan)), curation.Descriptions, errors);
        errors.ThrowIfAny();
        return plan;
    }

    public static EmitPlan ResolveEmissionData(EmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan = ResolveStreamFallbacks(plan);
        var enums = plan.Models.OfType<EnumModelPlan>().ToDictionary(static model => model.Name, StringComparer.Ordinal);
        var resolved = new Dictionary<string, IReadOnlyList<EnumModelPlan>>(StringComparer.Ordinal);
        foreach (var container in plan.Clients.SelectMany(static client => client.Operations)
                     .GroupBy(static operation => operation.RouteContainerName, StringComparer.Ordinal))
        {
            var models = new SortedDictionary<string, EnumModelPlan>(StringComparer.Ordinal);
            foreach (var property in container.SelectMany(static operation => operation.QueryRequest?.Properties ?? []))
            {
                if (property.Kind is not QueryValueKind.Enum)
                {
                    continue;
                }

                var name = property.EnumTypeName ?? throw new InvalidOperationException(
                    $"Query property '{property.PropertyName}' has no enum type name.");
                models[name] = enums.TryGetValue(name, out var model) ? model
                    : throw new InvalidOperationException($"Query enum '{name}' has no generated model.");
            }

            resolved.Add(container.Key, Array.AsReadOnly(models.Values.ToArray()));
        }

        return UnionChainBuilder.Resolve(plan) with { QueryEnumModels = resolved };
    }

    /// <summary>Resolves stream fallback metadata from bound payload-union identity.</summary>
    private static EmitPlan ResolveStreamFallbacks(EmitPlan plan)
    {
        var byName = plan.Unions.ToDictionary(static union => union.Name, StringComparer.Ordinal);
        var payloadNames = StreamPayloadNames(plan.Clients);
        return plan with
        {
            Clients = [.. plan.Clients.Select(client => client with
            {
                Operations = [.. client.Operations.Select(operation => operation.Stream is { } stream
                    ? operation with
                    {
                        Stream = stream with
                        {
                            UnknownPayloadTypeName = byName.GetValueOrDefault(stream.PayloadTypeName)?.UnknownTypeName,
                        },
                    }
                    : operation)],
            })],
            Unions = [.. plan.Unions.Select(union => union with
            {
                IsStreamFallbackCarrier = payloadNames.Contains(union.Name),
            })],
        };
    }

    private static HashSet<string> StreamPayloadNames(IEnumerable<ClientPlan> clients) =>
        clients.SelectMany(static client => client.Operations)
            .Select(static operation => operation.Stream?.PayloadTypeName)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);

    private static void CheckStreamFallbacks(OwnershipCandidates candidates, IReadOnlyList<ClientPlan> clients, BindingErrorCollector errors)
    {
        var payloadNames = StreamPayloadNames(clients);
        foreach (var impossible in candidates.Unions.Where(static union => union.KnownImpossibleTags.Count > 0)
                     .OrderBy(static union => union.Name, StringComparer.Ordinal))
        {
            var reaching = SecretMemberPolicy.ReachingTypes(candidates.Models, candidates.Unions, [impossible.Name]);
            foreach (var root in candidates.Unions.Where(union => payloadNames.Contains(union.Name) && reaching.Contains(union.Name))
                         .OrderBy(static union => union.Name, StringComparer.Ordinal))
            {
                errors.Add(BindingErrorCategory.Schema, impossible.Name,
                    $"Stream payload union '{root.Name}' reaches '{impossible.Name}', which declares impossible tags: {string.Join(", ", impossible.KnownImpossibleTags.Order(StringComparer.Ordinal))}; a stream's unknown-arm fallback would bypass impossible-tag dispatch, so decline the stream or curate the tag.");
            }
        }
    }

    /// <summary>Attaches operation-owned model properties and session-event roles before masking secret payloads.</summary>
    private static UnknownPayloadMaskResult BindModelFacets(SchemaBindingContext context, SchemaBindingResult schemas,
        IReadOnlyList<ClientPlan> clients, GenerationCuration curation, StabilizeDuplicateCollapse collapse)
    {
        var models = AttachRequestQueryProperties(schemas.Models, clients, context.Errors);
        if (curation.SessionScopedEvent is { } row)
        {
            // The row's identifier is read through the same alias map the schemas were bound with,
            // so a stabilized duplicate of the identifier schema is the identifier too.
            var aliases = SchemaAliasApplier.Compose(collapse, curation.SchemaAliases);
            var identifier = !string.IsNullOrWhiteSpace(row.Identifier) && aliases.TryGetValue(row.Identifier, out var target)
                ? target
                : row.Identifier;
            models = SessionScopedEventBinder.Bind(new SessionScopedEventBindingContext
            {
                Document = context.Document,
                TypeNames = context.TypeNames,
                Schemas = schemas with { Models = models },
                Clients = clients,
                Row = row with { Identifier = identifier },
            }, context.Errors);
        }

        return SecretMemberPolicy.MaskUnknownPayloads(SecretMemberPolicy.Apply(models, curation, context.Errors), schemas.Unions);
    }

    /// <summary>Declined operations retain their reviewed reasons in ordinal identity order.</summary>
    private static IReadOnlyList<DeclinedOperationPlan> DeclinedOperations(GenerationCuration curation) =>
    [
        .. curation.Declined
            .DistinctBy(static row => row.OperationId, StringComparer.Ordinal)
            .OrderBy(static row => row.OperationId, StringComparer.Ordinal)
            .Select(static row => new DeclinedOperationPlan { OperationId = row.OperationId, Reason = row.Reason }),
    ];

    /// <summary>Every operation the selection, a transport-owned row and a declined row all leave out, ordinal-sorted.</summary>
    private static PendingOperationPlan[] PendingOperations(SpecDocument document, IReadOnlyList<SpecOperation> selected,
        GenerationCuration curation)
    {
        var selectedIds = selected.Select(static operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);
        // A transport-owned operation is neither selected nor pending. Leaving it out of the
        // pending set hides no drift only because the curation validator proves every
        // row on every bind — existence, reason, and the subtree fingerprint are that wall.
        var transportOwnedIds = curation.TransportOwned.Select(static row => row.OperationId).ToHashSet(StringComparer.Ordinal);
        // A declined operation is likewise neither selected nor pending: a standing wall refuses
        // it and the maintainer decided the wall stands. The curation validator proves every row
        // (existence, reason, no overlap with selection or a transport-owned row); the
        // coordinator's probe proves the remaining claim — that the wall is real.
        var declinedIds = curation.Declined.Select(static row => row.OperationId).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. document
                .Operations
                .Where(operation => !selectedIds.Contains(operation.OperationId)
                                    && !transportOwnedIds.Contains(operation.OperationId)
                                    && !declinedIds.Contains(operation.OperationId))
                .OrderBy(static operation => operation.OperationId, StringComparer.Ordinal)
                .Select(static operation => new PendingOperationPlan
                {
                    OperationId = operation.OperationId,
                }),
        ];
    }

    /// <summary>
    /// A merged operation's query properties ride its body model; the model plan gains
    /// them here because schema binding cannot see operation placement.
    /// </summary>
    private static IReadOnlyList<ModelPlan> AttachRequestQueryProperties(IReadOnlyList<ModelPlan> models,
        IReadOnlyList<ClientPlan> clients, BindingErrorCollector errors)
    {
        var merged = clients
            .SelectMany(static client => client.Operations)
            .Where(static operation => operation.QueryRequest is { RidesRequestBody: true })
            .ToDictionary(static operation => operation.QueryRequest!.TypeName, StringComparer.Ordinal);
        if (merged.Count is 0)
        {
            return models;
        }

        var result = new List<ModelPlan>(models.Count);
        foreach (var model in models)
        {
            if (!merged.TryGetValue(model.Name, out var operation))
            {
                result.Add(model);
                continue;
            }

            _ = merged.Remove(model.Name);
            if (model is not ObjectModelPlan objectModel)
            {
                errors.Add(BindingErrorCategory.Operation, model.Name, "a merged request body must bind to an object model");
                continue;
            }

            result.Add(objectModel with
            {
                RequestQueryProperties = operation.QueryRequest!.Properties
            });
        }

        foreach (var missing in merged.Keys.Order(StringComparer.Ordinal))
        {
            errors.Add(BindingErrorCategory.Operation, missing, "a merged request body model is absent from the model closure");
        }

        return result;
    }

    /// <summary>
    /// A DTO sharing a model's name would make the emitted DTO recursively typed through
    /// same-namespace resolution and collapse two registry identities into one; both
    /// directions refuse before composition instead of hiding behind a Distinct.
    /// </summary>
    private static void CheckDtoNameCollisions(GeneratedTypeNameInventory inventory, BindingErrorCollector errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in inventory.Claims.Where(static claim => claim.Role is GeneratedTypeNameInventory.ClaimRole.EnvelopeDto)
                     .OrderBy(static claim => claim.Name, StringComparer.Ordinal))
        {
            var dtoName = claim.Name;
            if (inventory.OtherClaims(claim).Any(static other => other.Role is not (GeneratedTypeNameInventory.ClaimRole.EnvelopeDto or GeneratedTypeNameInventory.ClaimRole.Hoisted)))
            {
                errors.Add(
                    BindingErrorCategory.Naming,
                    dtoName,
                    $"envelope DTO name '{dtoName}' collides with a generated model name");
            }

            if (!seen.Add(dtoName))
            {
                errors.Add(
                    BindingErrorCategory.Naming,
                    dtoName,
                    $"envelope DTO name '{dtoName}' is derived by multiple operations");
            }
        }
    }

    /// <summary>Wrapped envelopes deserialize through internal DTOs, which join the serializer registry.</summary>
    private static RegistryPlan ComposeRegistry(RegistryPlan schemaRegistry, IReadOnlyList<ClientPlan> clients)
    {
        var dtoNames = clients
            .SelectMany(static client => client.Operations)
            .Select(static operation => operation.Envelope?.EnvelopeDtoTypeName)
            .OfType<string>();
        var streamCauseTypes = clients
            .SelectMany(static client => client.Operations)
            .Select(static operation => operation.Stream?.CauseTypeName)
            .OfType<string>();
        // Bare shapes without a DTO carry their payload type straight through, so a
        // non-named payload (a list or a dictionary) must register its own accessor here;
        // a DTO-wrapped payload rides the DTO above instead.
        var payloadEntries = clients
            .SelectMany(static client => client.Operations)
            .Select(static operation => operation.Envelope)
            .Where(static envelope => envelope is
            {
                EnvelopeDtoTypeName: null,
                PayloadType: not null and not NamedTypeReferencePlan,
                // Raw bytes never reach the serializer.
                Kind: not EnvelopeKind.Binary,
            })
            .Select(static envelope => envelope!.PayloadType!);
        return new RegistryPlan
        {
            // Uniqueness is guaranteed by CheckDtoNameCollisions before composition.
            TypeNames =
            [
                .. schemaRegistry
                    .TypeNames.Concat(dtoNames)
                    .Concat(streamCauseTypes)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ],
            PayloadEntries = [.. payloadEntries],
        };
    }

    private static ReadOnlyCollection<SpecOperation> SelectOperations(OperationSelection selection,
        Dictionary<string, SpecOperation> operationsById, BindingErrorCollector errors)
    {
        var selected = new List<SpecOperation>(selection.OperationIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operationId in selection.OperationIds)
        {
            if (string.IsNullOrWhiteSpace(operationId))
            {
                errors.Add(BindingErrorCategory.Selection, "profile", "selected operation ID cannot be blank");
                continue;
            }

            if (!seen.Add(operationId))
            {
                errors.Add(BindingErrorCategory.Selection, operationId, "selected operation ID is duplicated");
                continue;
            }

            if (!operationsById.TryGetValue(operationId, out var operation))
            {
                errors.Add(BindingErrorCategory.Selection, operationId, "selected operation does not exist in the ingested spec");
                continue;
            }

            selected.Add(operation);
        }

        return Array.AsReadOnly([.. selected]);
    }
}
