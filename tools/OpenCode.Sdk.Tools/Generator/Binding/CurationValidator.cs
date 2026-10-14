using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class CurationValidator
{
    private readonly StringComparer _comparer = StringComparer.Ordinal;

    public void Validate(SpecDocument document, IReadOnlyList<SpecOperation> selected, ReachableSchemaSet reachable,
        GenerationCuration curation, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(reachable);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(errors);

        var selectedIds = selected.Select(static operation => operation.OperationId).ToHashSet(_comparer);
        var selectedGroups = selected.Select(GetGroup).ToHashSet(_comparer);
        var documentIds = document.Operations.Select(static operation => operation.OperationId).ToHashSet(_comparer);
        var documentGroups = document.Operations.Select(GetGroup).ToHashSet(_comparer);
        ValidateGroups(selected, selectedGroups, documentGroups, curation, errors);
        ValidateOperationNames(selectedIds, documentIds, curation, errors);
        ValidateParameterNames(selected, curation, errors);
        ValidateSchemaNames(document, reachable, curation, errors);
        ValidateEnumMemberNames(document, reachable, curation, errors);
        ValidateEnvelopeNames(selectedIds, documentIds, curation, errors);
        ValidateTransportOwned(document, selectedIds, curation, errors);
        ValidateDeclined(documentIds, selectedIds, curation, errors);
    }

    private static void ValidateSchemaNames(SpecDocument document, ReachableSchemaSet reachable,
        GenerationCuration curation, BindingErrorCollector errors)
    {
        var reachableKeys = reachable.GraphKeys.ToHashSet(StringComparer.Ordinal);
        var curated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schemaName in curation.SchemaNames)
        {
            if (!curated.Add(schemaName.Schema))
            {
                errors.Add(BindingErrorCategory.Curation, schemaName.Schema,
                    "schema name curation is duplicated");
            }

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

            if (string.IsNullOrWhiteSpace(schemaName.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, schemaName.Schema,
                    "schema name curation must declare a reason");
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
        var curated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in curation.EnumMemberNames)
        {
            var subject = row.Schema + "/" + row.Value;
            if (!curated.Add(subject))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "enum member name curation is duplicated");
            }

            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "enum member name curation must declare a reason");
            }

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

    private static void ValidateOperationNames(HashSet<string> selectedIds, HashSet<string> documentIds,
        GenerationCuration curation, BindingErrorCollector errors)
    {
        var curated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operationName in curation.OperationNames)
        {
            if (!curated.Add(operationName.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, operationName.OperationId,
                    "operation name curation is duplicated");
            }

            if (!documentIds.Contains(operationName.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, operationName.OperationId,
                    "curated operation does not exist in the spec");
            }
            else if (!selectedIds.Contains(operationName.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, operationName.OperationId,
                    "curated operation is not selected by the current profile");
            }

            if (string.IsNullOrWhiteSpace(operationName.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, operationName.OperationId,
                    "operation name curation must declare a reason");
            }

            if (!CSharpNamePolicy.IsValidIdentifier(operationName.MethodName)
                || !operationName.MethodName.EndsWith("Async", StringComparison.Ordinal)
                || operationName.MethodName.Length is 5)
            {
                errors.Add(BindingErrorCategory.Naming, operationName.OperationId,
                    $"method name '{operationName.MethodName}' must be a valid C# identifier ending in 'Async'");
            }
        }
    }

    /// <summary>
    /// A parameter-name row names a route value of a selected operation, once, with a reason and a
    /// camel-cased identifier. A handle parameter names one working object, so every operation of a
    /// handle family that carries it must name it the same way.
    /// </summary>
    private void ValidateParameterNames(IReadOnlyList<SpecOperation> selected, GenerationCuration curation,
        BindingErrorCollector errors)
    {
        var operations = selected.ToDictionary(static operation => operation.OperationId, _comparer);
        var curated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in curation.ParameterNames)
        {
            var subject = row.OperationId + ":" + row.Parameter;
            if (!curated.Add(subject))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "parameter name curation is duplicated");
            }

            if (!operations.TryGetValue(row.OperationId, out var operation))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "curated operation is not selected by the current profile");
            }
            else if (!operation.Parameters.Any(parameter => parameter.Location is SpecParameterLocation.Path
                                                            && string.Equals(parameter.Name, row.Parameter, StringComparison.Ordinal)))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "curated parameter is not a route value of the operation");
            }

            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "parameter name curation must declare a reason");
            }

            if (!CSharpNamePolicy.IsValidIdentifier(row.DotNetName) || !char.IsLower(row.DotNetName[0]))
            {
                errors.Add(BindingErrorCategory.Naming, subject,
                    $"parameter name '{row.DotNetName}' must be a valid camel-cased C# identifier");
            }
        }

        foreach (var (wireName, group) in curation.Groups.Where(static pair => pair.Value.HandleParameter is not null))
        {
            var names = selected
                .Where(operation => string.Equals(GetGroup(operation), wireName, StringComparison.Ordinal)
                                    && operation.Parameters.Any(parameter => parameter.Location is SpecParameterLocation.Path
                                                                             && string.Equals(parameter.Name, group.HandleParameter, StringComparison.Ordinal)))
                .Select(operation => curation.ParameterNames.FirstOrDefault(row =>
                        string.Equals(row.OperationId, operation.OperationId, StringComparison.Ordinal)
                        && string.Equals(row.Parameter, group.HandleParameter, StringComparison.Ordinal))
                    ?.DotNetName ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (names > 1)
            {
                errors.Add(BindingErrorCategory.Curation, wireName,
                    $"every operation of the handle family must name handle parameter '{group.HandleParameter}' the same way");
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
            // Placement is an API decision (ADR-0019); like every other curation row it
            // documents itself or refuses.
            if (string.IsNullOrWhiteSpace(group.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, wireName, "group curation must carry a reason");
            }

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
                         .Select(static pair => (pair.Value.HandleName, pair.Value.HandleParameter, pair.Value.Emission))
                         .Distinct()
                         .Skip(1)
                         .Any())
                     .Select(static family => family.Key)
                     .Order(StringComparer.Ordinal))
        {
            errors.Add(
                BindingErrorCategory.Curation,
                clientName,
                $"groups sharing client '{clientName}' must declare identical handle and emission configuration");
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
    /// Payload names derive mechanically from the operation subject; curated entries are
    /// overrides only, so validation covers orphans and identifier
    /// legality, never presence.
    /// </summary>
    private static void ValidateEnvelopeNames(HashSet<string> selectedIds, HashSet<string> documentIds,
        GenerationCuration curation, BindingErrorCollector errors)
    {
        foreach (var (operationId, payloadName) in curation.EnvelopePayloadNames.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in curation.TransportOwned)
        {
            if (!seen.Add(row.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId, "transport-owned curation is duplicated");
            }

            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId, "transport-owned curation must declare a reason");
            }

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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in curation.Declined)
        {
            if (!seen.Add(row.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId, "declined curation is duplicated");
            }

            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId, "declined curation must declare a reason");
            }

            if (!documentIds.Contains(row.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId, "curated operation does not exist in the spec");
                continue;
            }

            if (selectedIds.Contains(row.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId, "declined operation cannot be selected");
            }

            if (transportOwnedIds.Contains(row.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId, "declined operation cannot also be transport-owned");
            }
        }
    }

    private static bool IsSha256Hex(string? candidate) =>
        candidate is { Length: 64 } && candidate.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string GetGroup(SpecOperation operation) => operation.Segments[0];
}
