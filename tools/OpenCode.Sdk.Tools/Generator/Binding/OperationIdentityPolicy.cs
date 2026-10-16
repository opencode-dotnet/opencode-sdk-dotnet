using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Validates the operation-identity curation rows and builds the subject-to-intended-identity
/// map ingestion consumes. Validation runs before ingestion — the map is an ingestion input,
/// so the post-bind validator would be too late — while document-dependent checks (an unknown
/// subject, an identity collision) stay with ingestion, which sees the raw document.
/// </summary>
internal static class OperationIdentityPolicy
{
    public static IReadOnlyDictionary<string, string> BuildMap(GenerationCuration curation)
    {
        ArgumentNullException.ThrowIfNull(curation);

        var errors = new BindingErrorCollector();
        if (!CurationRowContract.Validate(nameof(curation.OperationIdentities), curation.OperationIdentities, errors))
        {
            errors.ThrowIfAny();
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in curation.OperationIdentities)
        {
            // A well-formed subject needs no repair; such a row is a misuse of the mechanism,
            // not a defect map.
            if (OperationIdentityParser.IsWellFormed(row.OperationId))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId,
                    "operation identity subject already satisfies the protocol convention; identity rows exist only for upstream identity defects");
            }

            if (string.IsNullOrWhiteSpace(row.Identity) || !OperationIdentityParser.IsWellFormed(row.Identity))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId,
                    $"intended identity '{row.Identity}' must satisfy the protocol convention");
                continue;
            }

            if (!identities.Add(row.Identity))
            {
                errors.Add(BindingErrorCategory.Curation, row.OperationId,
                    $"intended identity '{row.Identity}' is claimed by more than one identity row");
            }

            _ = map.TryAdd(row.OperationId, row.Identity);
        }

        errors.ThrowIfAny();
        return map;
    }
}
