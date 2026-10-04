using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Binding.Abstractions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class SpecBinder(
    ReachableSchemaCollector reachableSchemas,
    StabilizeDuplicatePolicy stabilizeDuplicates,
    CurationValidator curationValidator,
    SchemaAliasValidator schemaAliasValidator,
    SchemaAliasApplier schemaAliases,
    SchemaNameResolver schemaNames,
    SchemaPlanBinder schemaPlans,
    OperationPlanBinder operationPlans) : ISpecBinder
{
    private readonly CurationValidator _curationValidator = curationValidator ?? throw new ArgumentNullException(nameof(curationValidator));
    private readonly OperationPlanBinder _operationPlans = operationPlans ?? throw new ArgumentNullException(nameof(operationPlans));
    private readonly ReachableSchemaCollector _reachableSchemas = reachableSchemas ?? throw new ArgumentNullException(nameof(reachableSchemas));
    private readonly SchemaAliasApplier _schemaAliases = schemaAliases ?? throw new ArgumentNullException(nameof(schemaAliases));
    private readonly SchemaAliasValidator _schemaAliasValidator = schemaAliasValidator ?? throw new ArgumentNullException(nameof(schemaAliasValidator));
    private readonly SchemaNameResolver _schemaNames = schemaNames ?? throw new ArgumentNullException(nameof(schemaNames));
    private readonly SchemaPlanBinder _schemaPlans = schemaPlans ?? throw new ArgumentNullException(nameof(schemaPlans));
    private readonly StabilizeDuplicatePolicy _stabilizeDuplicates = stabilizeDuplicates ?? throw new ArgumentNullException(nameof(stabilizeDuplicates));

    public EmitPlan Bind(SpecDocument document, OperationSelection selection, GenerationCuration curation)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(curation);

        var errors = new BindingErrorCollector();
        var operationsById = document.Operations.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);
        var selected = SelectOperations(selection, operationsById, errors);
        var reachable = _reachableSchemas.Collect(document, selected, errors);
        // Upstream stabilize's per-site duplicates collapse mechanically, ahead of curation:
        // curated rows are then judged against the graph as it will be bound, and a row that
        // repeats a mechanical fold is refused as redundant instead of shadowing it.
        var collapse = _stabilizeDuplicates.Resolve(document, reachable, curation.SchemaNames, errors);
        _curationValidator.Validate(document, selected, reachable, curation, errors);
        _schemaAliasValidator.Validate(document, reachable, curation, collapse, errors);

        // Aliases are validated against the raw document above; the collapse happens before
        // names resolve so the rest of the pipeline never sees the duplicates. Reachability
        // recollects on the transformed document because an alias target's promoted inline
        // children only become reachable through the rewritten references.
        var aliased = _schemaAliases.Apply(document, collapse, curation.SchemaAliases);
        if (!ReferenceEquals(aliased, document))
        {
            document = aliased;
            var aliasedById = document.Operations.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);
            selected = Array.AsReadOnly([.. selected.Select(operation => aliasedById[operation.OperationId])]);
            reachable = _reachableSchemas.Collect(document, selected, errors);
        }

        // Type names are resolved exactly once per bind; schema and operation binding
        // consume the same map so neither can reinterpret a schema under another name.
        var typeNames = _schemaNames.Resolve(document, reachable, selected, curation, errors);
        var schemaResult = _schemaPlans.Bind(document, reachable, curation, typeNames, errors);
        var clients = _operationPlans.Bind(document, selected, curation, typeNames, errors);
        var selectedIds = selected.Select(static operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);
        // A transport-owned operation is neither selected nor pending. Leaving it out of the
        // pending set hides no drift only because the curation validator above proves every
        // row on every bind — existence, reason, and the subtree fingerprint are that wall.
        var transportOwnedIds = curation.TransportOwned.Select(static row => row.OperationId).ToHashSet(StringComparer.Ordinal);
        // A declined operation is likewise neither selected nor pending: a standing wall refuses
        // it and the maintainer decided the wall stands. The validator above proves every row
        // (existence, reason, no overlap with selection or a transport-owned row); the
        // coordinator's probe proves the remaining claim — that the wall is real.
        var declinedIds = curation.Declined.Select(static row => row.OperationId).ToHashSet(StringComparer.Ordinal);
        var pending = document
            .Operations
            .Where(operation => !selectedIds.Contains(operation.OperationId)
                                && !transportOwnedIds.Contains(operation.OperationId)
                                && !declinedIds.Contains(operation.OperationId))
            .OrderBy(static operation => operation.OperationId, StringComparer.Ordinal)
            .Select(static operation => new PendingOperationPlan
            {
                OperationId = operation.OperationId,
            })
            .ToArray();

        var models = AttachRequestQueryProperties(schemaResult.Models, clients, errors);
        var masked = SecretMemberPolicy.MaskUnknownPayloads(SecretMemberPolicy.Apply(models, curation, errors), schemaResult.Unions);
        CheckDtoNameCollisions(schemaResult.Registry, clients, errors);
        errors.ThrowIfAny();
        return new EmitPlan
        {
            SelectedOperationIds = selection.OperationIds,
            Models = masked.Models,
            Unions = masked.Unions,
            HoistedInterfaces = schemaResult.HoistedInterfaces,
            Registry = ComposeRegistry(schemaResult.Registry, clients),
            Clients = clients,
            PendingOperations = pending,
            TransportOwnedOperationIds = [.. transportOwnedIds.Order(StringComparer.Ordinal)],
            DeclinedOperations =
            [
                .. curation
                    .Declined
                    .DistinctBy(static row => row.OperationId, StringComparer.Ordinal)
                    .OrderBy(static row => row.OperationId, StringComparer.Ordinal)
                    .Select(static row => new DeclinedOperationPlan { OperationId = row.OperationId, Reason = row.Reason })
            ],
            ImplicitAliases = collapse,
        };
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
    private static void CheckDtoNameCollisions(RegistryPlan schemaRegistry, IReadOnlyList<ClientPlan> clients,
        BindingErrorCollector errors)
    {
        var modelNames = schemaRegistry.TypeNames.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dtoName in clients
                     .SelectMany(static client => client.Operations)
                     .Select(static operation => operation.Envelope?.EnvelopeDtoTypeName)
                     .OfType<string>()
                     .OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (modelNames.Contains(dtoName))
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
