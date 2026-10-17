using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Folds upstream Effect's stabilize duplicates — component names spelled
/// <c>&lt;base&gt;_&lt;N&gt;</c> that repeat a structurally identical <c>&lt;base&gt;</c> once per
/// use site — into their base before names resolve. This is the same class of mechanical
/// dialect-artifact rule as the <c>*Encoded</c> projection strip
/// (<see cref="ProjectionArtifactNamePolicy"/>): it keys on shape and spelling only, never on an
/// operation id or a family name (ADR-0013).
/// </summary>
/// <remarks>
/// Only a reachable component key whose base exists and is not itself suffixed is a candidate,
/// so the rule never chains: <c>A_1_2</c> is left alone rather than followed through <c>A_1</c> to
/// <c>A</c>. Any other spelling gets no implicit alias and surfaces exactly as an undeclared
/// duplicate does today — a duplicate error tag refuses, a duplicate model breaks the public API
/// baseline — so the worst case is loud. Identity is judged on the graph as it will be bound, so
/// the candidate set shrinks to a fixpoint: a refused duplicate can break a pair that only matched
/// through it, and every survivor is <see cref="SchemaNodeComparer.DeepEquals"/> under the map the
/// survivors themselves form.
/// Non-equivalent candidates require explicit name curation to remain distinct; the binder's
/// ordinary curation and naming checks validate those rows after this policy resolves aliases. A
/// row naming a candidate that folds is refused as redundant.
/// </remarks>
internal static class StabilizeDuplicatePolicy
{
    /// <summary>Resolves aliases and reports non-equivalent candidates without explicit names.</summary>
    public static StabilizeDuplicateCollapse Resolve(SpecDocument document, ReachableSchemaSet reachable,
        IReadOnlyList<SchemaNameCuration> schemaNames, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reachable);
        ArgumentNullException.ThrowIfNull(schemaNames);
        ArgumentNullException.ThrowIfNull(errors);

        var candidates = CollectCandidates(document, reachable);
        if (candidates.Count is 0)
        {
            return StabilizeDuplicateCollapse.Empty;
        }

        var explicitlyNamed = schemaNames.Select(static row => row.Schema).ToHashSet(StringComparer.Ordinal);
        var responseRoots = reachable.ResponseRootKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var (schema, baseKey) in ReduceToFixpoint(document, candidates))
        {
            // A reason-bearing name can preserve a distinct shape. The ordinary curation
            // and name-resolution walls still validate the row and reject collisions.
            if (explicitlyNamed.Contains(schema)
                && !responseRoots.Contains(schema)
                && SchemaNameResolver.IsNominal(document.Schemas[schema], document.Schemas))
            {
                continue;
            }

            errors.Add(
                BindingErrorCategory.Schema,
                schema,
                $"stabilize duplicate '{schema}' is not structurally identical to its base '{baseKey}'");
        }

        // A row that names a candidate the collapse folds distinguishes nothing any more: the
        // refresh that made the pair equivalent is the moment to retire it, so it is refused
        // rather than carried along silently (the schemaAliases validator treats a redundant
        // alias row the same way).
        foreach (var (schema, baseKey) in candidates.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (explicitlyNamed.Contains(schema))
            {
                errors.Add(
                    BindingErrorCategory.Curation,
                    schema,
                    $"stabilize duplicate '{schema}' folds into '{baseKey}' mechanically, so its schemaNames row is redundant; remove the row");
            }
        }

        return new StabilizeDuplicateCollapse { Aliases = candidates, };
    }

    /// <summary>Every reachable component spelled as a stabilize duplicate of a base the graph declares.</summary>
    private static Dictionary<string, string> CollectCandidates(SpecDocument document, ReachableSchemaSet reachable)
    {
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in reachable.GraphKeys)
        {
            if (TryGetStabilizeBase(key, out var baseKey)
                && !TryGetStabilizeBase(baseKey, out _)
                && document.Schemas.ContainsKey(key)
                && document.Schemas.ContainsKey(baseKey))
            {
                candidates[key] = baseKey;
            }
        }

        return candidates;
    }

    /// <summary>
    /// Drops every candidate that is not deeply equal to its base under the surviving map and
    /// repeats until nothing drops, so a refusal cascades into the pairs that leaned on it.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> ReduceToFixpoint(SpecDocument document,
        Dictionary<string, string> candidates)
    {
        var refused = new List<KeyValuePair<string, string>>();
        bool dropped;
        do
        {
            dropped = false;
            foreach (var candidate in candidates.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToArray())
            {
                if (IsSpellingOfBase(document, candidate, candidates))
                {
                    continue;
                }

                _ = candidates.Remove(candidate.Key);
                refused.Add(candidate);
                dropped = true;
            }
        }
        while (dropped);

        return [.. refused.OrderBy(static pair => pair.Key, StringComparer.Ordinal)];
    }

    private static bool IsSpellingOfBase(SpecDocument document, KeyValuePair<string, string> candidate,
        IReadOnlyDictionary<string, string> aliases) =>
        document.Schemas.TryGetValue(candidate.Key, out var duplicate)
        && document.Schemas.TryGetValue(candidate.Value, out var target)
        && SchemaNodeComparer.DeepEquals(duplicate, target, document.Schemas, aliases);

    /// <summary>
    /// Recognizes the component spelling <c>&lt;base&gt;_&lt;N&gt;</c> where <c>N</c> is a
    /// canonical positive integer. Promoted inline keys carry a <c>#</c> and are never component
    /// names, so upstream's suffix convention cannot be read into one by accident.
    /// </summary>
    private static bool TryGetStabilizeBase(string key, [NotNullWhen(true)] out string? baseKey)
    {
        baseKey = null;
        if (key.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        var separator = key.AsSpan().LastIndexOf('_');
        if (separator <= 0 || separator == key.Length - 1)
        {
            return false;
        }

        var suffix = key.AsSpan(separator + 1);
        if (suffix[0] is '0')
        {
            return false;
        }

        foreach (var digit in suffix)
        {
            if (!char.IsAsciiDigit(digit))
            {
                return false;
            }
        }

        baseKey = key[..separator];
        return true;
    }
}
