using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Selects the property a marked union dispatches on, and with it the branches that tag the
/// property with a literal and the branch that tags it with a prefix instead. Candidates are
/// the first branch's literal marker properties in document order, then its prefix marker
/// properties, so a union whose first branch is the arm still finds its discriminator.
/// </summary>
/// <remarks>
/// Membership of the arm is decided by the discriminating property alone: a literal-tagged
/// branch that also carries an unrelated prefix marker — a templated identifier, say — stays
/// an ordinary variant, because that marker discriminates nothing.
/// </remarks>
internal static class UnionDiscriminatorSelector
{
    /// <summary>Returns the discriminator every branch answers, or null when no candidate qualifies.</summary>
    public static UnionDiscriminator? Select(IReadOnlyList<ResolvedUnionBranch> branches)
    {
        ArgumentNullException.ThrowIfNull(branches);

        if (branches.Count is 0)
        {
            return null;
        }

        var candidates = branches[0]
            .Markers.Select(static candidate => candidate.PropertyName)
            .Concat(branches[0].PrefixMarkers.Select(static candidate => candidate.PropertyName));
        foreach (var property in candidates)
        {
            if (Qualify(branches, property) is { } discriminator)
            {
                return discriminator;
            }
        }

        return null;
    }

    /// <summary>
    /// Qualifies one candidate property: every branch must tag it with a literal — one kind, and
    /// a value distinct from every other literal carrier — or stand as a prefix carrier, meaning
    /// it tags the property with no literal but does tag something with a prefix. A branch that
    /// tags the property neither way disqualifies the candidate outright. Several prefix carriers
    /// still qualify, so the arity refusal can name them.
    /// </summary>
    private static UnionDiscriminator? Qualify(IReadOnlyList<ResolvedUnionBranch> branches, string property)
    {
        var literalBranches = new List<ResolvedUnionBranch>(branches.Count);
        var prefixBranches = new List<ResolvedUnionBranch>();
        var values = new HashSet<string>(StringComparer.Ordinal);
        LiteralMarker? marker = null;
        foreach (var branch in branches)
        {
            var literal = branch.Markers.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.PropertyName, property));
            if (literal is null)
            {
                if (branch.PrefixMarkers.Count is 0)
                {
                    return null;
                }

                prefixBranches.Add(branch);
                continue;
            }

            if ((marker is not null && literal.Kind != marker.Kind) || !values.Add(literal.Value))
            {
                return null;
            }

            marker ??= literal;
            literalBranches.Add(branch);
        }

        // The kind comes from a literal carrier, so a candidate every branch tags with a prefix
        // names no kind and cannot discriminate.
        return marker is null ? null : new UnionDiscriminator(marker, literalBranches, prefixBranches);
    }
}
