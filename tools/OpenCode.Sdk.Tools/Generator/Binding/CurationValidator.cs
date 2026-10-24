using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static class CurationValidator
{
    public static void Validate(BindingContext context, IReadOnlyList<SpecOperation> selected)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selected);

        var selectedIds = selected.Select(static operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);
        var selectedGroups = selected.Select(GetGroup).ToHashSet(StringComparer.Ordinal);
        var documentIds = context.Document.Operations.Select(static operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);
        var documentGroups = context.Document.Operations.Select(GetGroup).ToHashSet(StringComparer.Ordinal);
        ValidateGroups(selected, selectedGroups, documentGroups, context.Curation, context.Errors);
        ValidateOperationNames(selected, documentIds, context.Curation, context.Errors);
        ValidateParameterNames(selected, context.Curation, context.Errors);
        QueryParameterCurationValidator.Validate(context.Document, selected, context.Curation, context.Errors);
        ValidateEnvelopeNames(selectedIds, documentIds, context.Curation, context.Errors);
        ValidateTransportOwned(context.Document, selectedIds, context.Curation, context.Errors);
        ValidateDeclined(documentIds, selectedIds, context.Curation, context.Errors);
    }

    public static void ValidateCollapsedSchemas(BindingContext context, StabilizeDuplicateCollapse collapse)
    {
        // Mechanical folds already refuse their schema-name rows as redundant, not missing.
        ValidateSchemaNames(context.Document, context.Reachable, context.Curation with
        {
            SchemaNames = [.. context.Curation.SchemaNames.Where(row => !collapse.Aliases.ContainsKey(row.Schema))],
        }, context.Errors);
        ValidateEnumMemberNames(context.Document, context.Reachable, context.Curation, context.Errors);
    }

    private static void ValidateSchemaNames(SpecDocument document, ReachableSchemaSet reachable,
        GenerationCuration curation, BindingErrorCollector errors)
    {
        var reachableKeys = reachable.GraphKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var schemaName in curation.SchemaNames)
        {
            if (!document.Schemas.ContainsKey(schemaName.Schema))
            {
                errors.Add(BindingErrorCategory.Curation, schemaName.Schema,
                    "curated schema does not exist in the spec");
            }
            else if (!reachableKeys.Contains(schemaName.Schema))
            {
                errors.Add(BindingErrorCategory.Curation, schemaName.Schema,
                    "curated schema is not referenced by the selected profile");
            }

            if (!CSharpNamePolicy.IsValidIdentifier(schemaName.DotNetName))
            {
                errors.Add(BindingErrorCategory.Naming, schemaName.Schema,
                    $"schema name '{schemaName.DotNetName}' is not a valid C# identifier");
            }
        }
    }

    /// <summary>
    /// A member-name row must name a value the document's enum carries, with a valid identifier
    /// that no other value of the enum — mechanically or by another row — already takes.
    /// </summary>
    private static void ValidateEnumMemberNames(SpecDocument document, ReachableSchemaSet reachable,
        GenerationCuration curation, BindingErrorCollector errors)
    {
        var reachableKeys = reachable.GraphKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var row in curation.EnumMemberNames)
        {
            var subject = row.Schema + "/" + row.Value;
            if (!CSharpNamePolicy.IsValidIdentifier(row.DotNetName))
            {
                errors.Add(BindingErrorCategory.Naming, subject, $"enum member name '{row.DotNetName}' is not a valid C# identifier");
            }

            if (!document.Schemas.TryGetValue(row.Schema, out var schema))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "curated schema does not exist in the spec");
                continue;
            }

            if (!reachableKeys.Contains(row.Schema))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "curated schema is not referenced by the selected profile");
            }

            if (schema is not EnumNode enumNode)
            {
                errors.Add(BindingErrorCategory.Curation, subject, "curated schema is not an enum");
                continue;
            }

            if (!enumNode.Values.Contains(row.Value, StringComparer.Ordinal))
            {
                errors.Add(BindingErrorCategory.Curation, subject, $"curated enum does not carry the value '{row.Value}'");
                continue;
            }

            foreach (var other in enumNode.Values)
            {
                if (string.Equals(other, row.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                var otherName = EnumMemberNamePolicy.Name(row.Schema, other, curation.EnumMemberNames);
                if (string.Equals(otherName, row.DotNetName, StringComparison.Ordinal))
                {
                    errors.Add(BindingErrorCategory.Naming, subject,
                        $"enum member name '{row.DotNetName}' collides with the name of value '{other}'");
                }
            }
        }
    }

    private static void ValidateOperationNames(IReadOnlyList<SpecOperation> selected, HashSet<string> documentIds,
        GenerationCuration curation, BindingErrorCollector errors)
    {
        var operations = selected.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);
        foreach (var operationName in curation.OperationNames)
        {
            if (!documentIds.Contains(operationName.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, operationName.OperationId,
                    "curated operation does not exist in the spec");
            }
            else if (!operations.ContainsKey(operationName.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, operationName.OperationId,
                    "curated operation is not selected by the current profile");
            }

            if (operations.TryGetValue(operationName.OperationId, out var operation)
                && string.Equals(operation.Segments[^1], "list", StringComparison.Ordinal))
            {
                var handle = curation.Groups.TryGetValue(GetGroup(operation), out var group)
                             && group.Placement is GroupPlacement.Client
                             && operation.Parameters.Any(parameter => parameter.Location is SpecParameterLocation.Path
                                 && string.Equals(parameter.Name, group.HandleParameter, StringComparison.Ordinal));
                var derived = OperationNamePolicy.MethodName(operation, handle: handle);
                if (string.Equals(operationName.MethodName, derived, StringComparison.Ordinal))
                {
                    errors.Add(BindingErrorCategory.Curation, operationName.OperationId,
                        $"operationNames row for '{operationName.OperationId}' repeats the list naming rule's derived value '{derived}'");
                }
            }

            if (!CSharpNamePolicy.IsValidIdentifier(operationName.MethodName)
                || !operationName.MethodName.EndsWith(OperationNamePolicy.AsyncSuffix, StringComparison.Ordinal)
                || operationName.MethodName.Length == OperationNamePolicy.AsyncSuffix.Length)
            {
                errors.Add(BindingErrorCategory.Naming, operationName.OperationId,
                    $"method name '{operationName.MethodName}' must be a valid C# identifier ending in '{OperationNamePolicy.AsyncSuffix}'");
            }
        }
    }

    /// <summary>
    /// A parameter-name row names a route value of a selected operation, once, with a reason and a
    /// camel-cased identifier that differs from the mechanical one. A handle parameter names one
    /// working object, so every operation of a handle family that carries it must name it the same
    /// way, across every group the family merges.
    /// </summary>
    private static void ValidateParameterNames(IReadOnlyList<SpecOperation> selected, GenerationCuration curation,
        BindingErrorCollector errors)
    {
        var operations = selected.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);
        foreach (var row in curation.ParameterNames)
        {
            var subject = row.OperationId + ":" + row.Parameter;
            if (!operations.TryGetValue(row.OperationId, out var operation))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "curated operation is not selected by the current profile");
            }
            else if (!operation.Parameters.Any(parameter => parameter.Location is SpecParameterLocation.Path
                                                            && string.Equals(parameter.Name, row.Parameter, StringComparison.Ordinal)))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "curated parameter is not a route value of the operation");
            }

            if (!CSharpNamePolicy.IsValidIdentifier(row.DotNetName) || !char.IsLower(row.DotNetName[0]))
            {
                errors.Add(BindingErrorCategory.Naming, subject,
                    $"parameter name '{row.DotNetName}' must be a valid camel-cased C# identifier");
            }
            else if (string.Equals(row.DotNetName, CSharpNamePolicy.ToCamelCase(row.Parameter), StringComparison.Ordinal))
            {
                errors.Add(BindingErrorCategory.Curation, subject,
                    $"parameter name '{row.DotNetName}' changes no name; the mechanical rule already gives it");
            }
        }

        // Groups sharing a client name merge into one family, so the family, not the wire group,
        // is the unit whose handle parameter must read the same everywhere.
        foreach (var family in curation
                     .Groups
                     .Where(static pair => pair.Value.HandleParameter is not null)
                     .GroupBy(static pair => pair.Value.ClientName ?? pair.Key, StringComparer.Ordinal)
                     .OrderBy(static family => family.Key, StringComparer.Ordinal))
        {
            var names = family
                .SelectMany(pair => selected
                    .Where(operation => string.Equals(GetGroup(operation), pair.Key, StringComparison.Ordinal)
                                        && operation.Parameters.Any(parameter => parameter.Location is SpecParameterLocation.Path
                                                                                 && string.Equals(parameter.Name, pair.Value.HandleParameter, StringComparison.Ordinal)))
                    .Select(operation => curation.ParameterNames.FirstOrDefault(row =>
                            string.Equals(row.OperationId, operation.OperationId, StringComparison.Ordinal)
                            && string.Equals(row.Parameter, pair.Value.HandleParameter, StringComparison.Ordinal))
                        ?.DotNetName ?? string.Empty))
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (names > 1)
            {
                errors.Add(BindingErrorCategory.Curation, family.Key,
                    $"every operation of the handle family must name handle parameter '{family.First().Value.HandleParameter}' the same way");
            }
        }
    }

    private static void ValidateGroups(IReadOnlyList<SpecOperation> selected, HashSet<string> selectedGroups,
        HashSet<string> documentGroups, GenerationCuration curation, BindingErrorCollector errors)
    {
        foreach (var group in selectedGroups.Where(group => !curation.Groups.ContainsKey(group)).Order(StringComparer.Ordinal))
        {
            errors.Add(BindingErrorCategory.Curation, group, "selected operation group has no curation row");
        }

        foreach (var (wireName, group) in curation.Groups.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!documentGroups.Contains(wireName))
            {
                errors.Add(BindingErrorCategory.Curation, wireName, "curated group does not exist in the spec");
            }
            else if (!selectedGroups.Contains(wireName))
            {
                errors.Add(BindingErrorCategory.Curation, wireName, "curated group is not selected by the current profile");
            }

            ValidateGroupShape(wireName, group, selected, errors);
            ValidateGroupEmission(wireName, group, selected, errors);
            ValidateExperimentalDiagnostic(wireName, group, selected, errors);
        }

        // Groups sharing a client name merge into one client family whose configuration is
        // taken from a single row, so every row's family declarations must agree exactly. A
        // divergent handle row would fork the family; a divergent emission row is worse, since
        // one row's accessibility would silently govern another row's operations — including a
        // header parameter admitted per-row landing on a public client.
        foreach (var clientName in curation
                     .Groups
                     .Where(static pair => pair.Value is { Placement: GroupPlacement.Client, ClientName: not null })
                     .GroupBy(static pair => pair.Value.ClientName!, StringComparer.Ordinal)
                     .Where(static family => family
                         .Select(static pair => (pair.Value.HandleName, pair.Value.HandleParameter, pair.Value.Emission, pair.Value.ExperimentalDiagnostic))
                         .Distinct()
                         .Skip(1)
                         .Any())
                     .Select(static family => family.Key)
                     .Order(StringComparer.Ordinal))
        {
            errors.Add(
                BindingErrorCategory.Curation,
                clientName,
                $"groups sharing client '{clientName}' must declare identical handle and emission configuration and the same experimentalDiagnostic");
        }

        // An id names one family, so groups that share it must be one merged client.
        foreach (var diagnostic in curation.Groups
                     .Where(static pair => pair.Value.ExperimentalDiagnostic is not null)
                     .GroupBy(static pair => pair.Value.ExperimentalDiagnostic!, StringComparer.Ordinal)
                     .Where(static groups => groups
                         .Select(static pair => (pair.Value.Placement,
                             Family: pair.Value.Placement is GroupPlacement.Client ? pair.Value.ClientName ?? pair.Key : pair.Key))
                         .Distinct().Skip(1).Any())
                     .Select(static groups => groups.Key)
                     .Order(StringComparer.Ordinal))
        {
            errors.Add(BindingErrorCategory.Curation, diagnostic,
                $"experimentalDiagnostic '{diagnostic}' is shared by groups that are not one merged family");
        }
    }

    private static void ValidateExperimentalDiagnostic(string wireName, GroupCuration group,
        IReadOnlyList<SpecOperation> selected, BindingErrorCollector errors)
    {
        var experimental = selected.Any(operation => string.Equals(GetGroup(operation), wireName, StringComparison.Ordinal)
            && ExperimentalRoutePolicy.IsExperimental(operation.Path));
        if (experimental && group.ExperimentalDiagnostic is null)
        {
            errors.Add(BindingErrorCategory.Curation, wireName, "selected experimental route requires experimentalDiagnostic");
        }

        if (group.ExperimentalDiagnostic is not { } id)
        {
            return;
        }

        if (!experimental)
        {
            errors.Add(BindingErrorCategory.Curation, wireName, "experimentalDiagnostic requires a selected experimental route");
        }

        if (id.Length != 11 || !id.StartsWith("OPENCODE", StringComparison.Ordinal)
            || !id.Skip(8).All(static character => character is >= '0' and <= '9'))
        {
            errors.Add(BindingErrorCategory.Curation, wireName,
                $"experimentalDiagnostic '{id}' must match ^OPENCODE[0-9]{{3}}$");
        }
    }

    private static void ValidateGroupShape(string wireName, GroupCuration group, IReadOnlyList<SpecOperation> selected, BindingErrorCollector errors)
    {
        switch (group.Placement)
        {
            case GroupPlacement.Root when group.ClientName is not null || group.HandleName is not null || group.HandleParameter is not null:
                errors.Add(BindingErrorCategory.Curation, wireName, "root group cannot declare clientName, handleName, or handleParameter");
                break;
            case GroupPlacement.Client when string.IsNullOrWhiteSpace(group.ClientName):
                errors.Add(BindingErrorCategory.Curation, wireName, "client group must declare clientName");
                break;
            case not (GroupPlacement.Root or GroupPlacement.Client):
                // System.Text.Json admits numeric enum spellings even under the string
                // converter, so an out-of-range placement must fail here, not drop silently.
                errors.Add(
                    BindingErrorCategory.Curation,
                    wireName,
                    $"placement value '{((int)group.Placement).ToString(System.Globalization.CultureInfo.InvariantCulture)}' is not a recognized group placement");
                break;
        }

        if (group.ClientName is not null && !CSharpNamePolicy.IsValidIdentifier(group.ClientName))
        {
            errors.Add(BindingErrorCategory.Naming, wireName, $"client name '{group.ClientName}' is not a valid C# identifier");
        }

        if (group.HandleName is not null && !CSharpNamePolicy.IsValidIdentifier(group.HandleName))
        {
            errors.Add(BindingErrorCategory.Naming, wireName, $"handle name '{group.HandleName}' is not a valid C# identifier");
        }

        if ((group.HandleName is not null) != (group.HandleParameter is not null))
        {
            errors.Add(BindingErrorCategory.Curation, wireName, "handleName and handleParameter must be declared together");
            return;
        }

        if (group.HandleParameter is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(group.HandleParameter))
        {
            errors.Add(BindingErrorCategory.Curation, wireName, "handleParameter cannot be blank");
            return;
        }

        ValidateHandleParameterCoverage(wireName, group, selected, errors);
    }

    /// <summary>
    /// The internal-raw mode hands a family's public surface to hand-written code (ADR-0021),
    /// so it only means something on a client-placed group that actually emits operations;
    /// every other spelling would be a silently inert row.
    /// </summary>
    private static void ValidateGroupEmission(string wireName, GroupCuration group, IReadOnlyList<SpecOperation> selected,
        BindingErrorCollector errors)
    {
        if (group.Emission is not (EmissionMode.Public or EmissionMode.InternalRaw))
        {
            // System.Text.Json admits numeric enum spellings even under the string
            // converter, so an out-of-range emission must fail here, not drop silently.
            errors.Add(
                BindingErrorCategory.Curation,
                wireName,
                $"emission value '{((int)group.Emission).ToString(System.Globalization.CultureInfo.InvariantCulture)}' is not a recognized group emission");
            return;
        }

        if (group.Emission is not EmissionMode.InternalRaw)
        {
            return;
        }

        if (group.Placement is GroupPlacement.Root)
        {
            errors.Add(BindingErrorCategory.Curation, wireName, "root group cannot declare internalRaw emission");
        }

        if (!selected.Any(operation => string.Equals(GetGroup(operation), wireName, StringComparison.Ordinal)))
        {
            errors.Add(BindingErrorCategory.Curation, wireName, "internalRaw emission requires at least one selected operation");
        }
    }

    /// <summary>
    /// Coverage is selection-scoped: during staged generation only selected operations can
    /// witness the handle parameter, and a group with no selected operations already fails
    /// the global orphan check.
    /// </summary>
    private static void ValidateHandleParameterCoverage(string wireName, GroupCuration group, IReadOnlyList<SpecOperation> selected,
        BindingErrorCollector errors)
    {
        if (group.Placement is not GroupPlacement.Client)
        {
            return;
        }

        var groupOperations = selected
            .Where(operation => string.Equals(GetGroup(operation), wireName, StringComparison.Ordinal))
            .ToArray();
        if (groupOperations.Length is 0)
        {
            return;
        }

        var covered = groupOperations.Any(operation => operation.Parameters.Any(parameter => parameter is
        {
            Location: SpecParameterLocation.Path,
            IsRequired: true,
        } && string.Equals(parameter.Name, group.HandleParameter, StringComparison.Ordinal)));
        if (!covered)
        {
            errors.Add(
                BindingErrorCategory.Curation,
                wireName,
                $"handle parameter '{group.HandleParameter}' does not name a required path parameter on any selected operation in the group");
        }
    }

    /// <summary>
    /// Curated payload names are overrides only, so validation covers orphans and identifier
    /// legality, never presence. The envelope binder refuses redundant rows against the
    /// mechanical name for the response shape.
    /// </summary>
    private static void ValidateEnvelopeNames(HashSet<string> selectedIds, HashSet<string> documentIds,
        GenerationCuration curation, BindingErrorCollector errors)
    {
        foreach (var row in curation.EnvelopePayloadNames.OrderBy(static row => row.OperationId, StringComparer.Ordinal))
        {
            var operationId = row.OperationId;
            var payloadName = row.Name;
            if (!documentIds.Contains(operationId))
            {
                errors.Add(BindingErrorCategory.Curation, operationId, "curated operation does not exist in the spec");
            }
            else if (!selectedIds.Contains(operationId))
            {
                errors.Add(BindingErrorCategory.Curation, operationId, "curated operation is not selected by the current profile");
            }

            if (!CSharpNamePolicy.IsValidIdentifier(payloadName))
            {
                errors.Add(BindingErrorCategory.Naming, operationId, $"payload name '{payloadName}' is not a valid C# identifier");
            }
        }
    }

    /// <summary>
    /// A transport-owned row pins a fingerprint over an operation the profile never selects
    /// (ADR-0021's hand-written doors depend on its shape without the compiler ever seeing it),
    /// so this checks against <paramref name="document"/>'s full ingested operation set, not the
    /// selected subset every other curation row is validated against. A row over a selected
    /// operation is refused: the operation would be generated and hand-written at once, and the
    /// binder relies on the row to keep its operation out of the pending set.
    /// </summary>
    private static void ValidateTransportOwned(SpecDocument document, HashSet<string> selectedIds, GenerationCuration curation,
        BindingErrorCollector errors)
    {
        var operationsById = document.Operations.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);
        foreach (var row in curation.TransportOwned)
        {
            if (!IsSha256Hex(row.SubtreeSha256))
            {
                errors.Add(
                    BindingErrorCategory.Curation,
                    row.OperationId,
                    "transport-owned curation must declare subtreeSha256 as 64 lowercase hex characters");
                continue;
            }

            if (!operationsById.TryGetValue(row.OperationId, out var operation))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId, "curated operation does not exist in the spec");
                continue;
            }

            if (selectedIds.Contains(row.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId, "transport-owned operation cannot be selected");
            }

            var computed = TransportOwnedFingerprint.ComputeSha256(operation);
            if (!string.Equals(computed, row.SubtreeSha256, StringComparison.Ordinal))
            {
                errors.Add(
                    BindingErrorCategory.Curation,
                    row.OperationId,
                    "transport-owned operation subtree no longer matches the committed subtreeSha256 "
                    + $"(declared '{row.SubtreeSha256}', computed '{computed}'); review the reshaped operation and repin");
            }
        }
    }

    /// <summary>
    /// A declined row records an operation the generator cannot reach and the maintainer has
    /// decided to leave unreached, so — like a transport-owned row — it is judged against
    /// <paramref name="documentIds"/>, the full ingested operation set, not the selected subset.
    /// A row over an operation the document no longer declares refuses instead of lingering:
    /// when upstream removes or reshapes the operation, retiring the row is a conscious diff.
    /// A row over a selected operation refuses because the two states contradict, and a row that
    /// repeats a transport-owned row refuses because both states would claim the same operation
    /// and the marker would count it twice. The remaining wall — that a declined operation must
    /// actually be walled — needs the bindability probe and therefore lives in
    /// <see cref="GenerationCoordinator"/>, which owns the probe.
    /// </summary>
    private static void ValidateDeclined(HashSet<string> documentIds, HashSet<string> selectedIds, GenerationCuration curation,
        BindingErrorCollector errors)
    {
        var transportOwnedIds = curation.TransportOwned.Select(static row => row.OperationId).ToHashSet(StringComparer.Ordinal);
        foreach (var operationId in curation.Declined.Select(static row => row.OperationId))
        {
            if (!documentIds.Contains(operationId))
            {
                errors.Add(BindingErrorCategory.Curation, operationId, "curated operation does not exist in the spec");
                continue;
            }

            if (selectedIds.Contains(operationId))
            {
                errors.Add(BindingErrorCategory.Curation, operationId, "declined operation cannot be selected");
            }

            if (transportOwnedIds.Contains(operationId))
            {
                errors.Add(BindingErrorCategory.Curation, operationId, "declined operation cannot also be transport-owned");
            }
        }
    }

    private static bool IsSha256Hex(string? candidate) =>
        candidate is { Length: 64 } && candidate.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string GetGroup(SpecOperation operation) => operation.Segments[0];
}
