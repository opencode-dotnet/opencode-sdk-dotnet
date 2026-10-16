using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The alias walls, which carry the drift contract: a deleted source or target orphans the row, a
/// dereferenced source goes dormant, and any structural divergence — the tag included — breaks the
/// identity check. Every upstream move on the duplicate is loud. A row the mechanical
/// stabilize-duplicate collapse already implies is refused as redundant, so curation carries only
/// the duplicates no convention recognizes. This is the one curation section whose validation
/// needs the collapse, which is why it is its own validator rather than a sixth check inside
/// <see cref="CurationValidator"/>.
/// </summary>
internal sealed class SchemaAliasValidator
{
    private readonly StringComparer _comparer = StringComparer.Ordinal;

    public void Validate(SpecDocument document, ReachableSchemaSet reachable, GenerationCuration curation,
        StabilizeDuplicateCollapse collapse, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reachable);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(collapse);
        ArgumentNullException.ThrowIfNull(errors);

        var reachableKeys = reachable.GraphKeys.ToHashSet(_comparer);
        var sources = curation.SchemaAliases.Select(static row => row.Schema).ToHashSet(_comparer);
        // Identity is judged on the graph as it will be bound, so one alias can be what makes
        // a second pair identical — the mechanical collapse included.
        // A duplicated source is refused by the row contract; Compose tolerates it.
        var aliasTargets = SchemaAliasApplier.Compose(collapse, curation.SchemaAliases);
        foreach (var alias in curation.SchemaAliases)
        {
            if (collapse.Aliases.TryGetValue(alias.Schema, out var implied))
            {
                errors.Add(BindingErrorCategory.Curation, alias.Schema,
                    $"schema alias is redundant: the stabilize-duplicate collapse already folds it into '{implied}'");
                continue;
            }

            if (string.Equals(alias.Schema, alias.AliasOf, StringComparison.Ordinal))
            {
                errors.Add(BindingErrorCategory.Curation, alias.Schema, "schema alias cannot target itself");
                continue;
            }

            if (!document.Schemas.TryGetValue(alias.Schema, out var source))
            {
                errors.Add(BindingErrorCategory.Curation, alias.Schema, "aliased schema does not exist in the spec");
                continue;
            }

            if (!document.Schemas.TryGetValue(alias.AliasOf, out var target))
            {
                errors.Add(BindingErrorCategory.Curation, alias.Schema, $"alias target '{alias.AliasOf}' does not exist in the spec");
                continue;
            }

            if (!reachableKeys.Contains(alias.Schema))
            {
                errors.Add(BindingErrorCategory.Curation, alias.Schema, "aliased schema is not referenced by the selected profile");
            }

            if (!SchemaNodeComparer.DeepEquals(source, target, document.Schemas, aliasTargets))
            {
                errors.Add(BindingErrorCategory.Curation, alias.Schema, "aliased schemas must be structurally identical");
            }
        }

        ValidateAliasChains(curation, collapse, sources, errors);
    }

    /// <summary>
    /// A chain leaves the applier rewriting a reference onto a key it has already deleted from the
    /// graph, so all three shapes of it refuse by name: a curated row whose target another curated
    /// row aliases, a curated row whose target the mechanical collapse folds, and a curated row
    /// over the very base the mechanical collapse folds duplicates into. The last two are mirror
    /// images — a key-only lookup would catch one direction and leave the other to surface as an
    /// unnamed reachability failure. One row yields one refusal: a row that is a chain twice over
    /// is still one broken row, and naming it twice would read as two.
    /// </summary>
    private static void ValidateAliasChains(GenerationCuration curation, StabilizeDuplicateCollapse collapse,
        HashSet<string> sources, BindingErrorCollector errors)
    {
        foreach (var alias in curation
                     .SchemaAliases
                     .Where(static alias => !string.Equals(alias.Schema, alias.AliasOf, StringComparison.Ordinal))
                     .OrderBy(static alias => alias.Schema, StringComparer.Ordinal))
        {
            if (collapse.Aliases.TryGetValue(alias.AliasOf, out var foldedTarget))
            {
                errors.Add(BindingErrorCategory.Curation, alias.Schema,
                    "schema aliases cannot chain: the stabilize-duplicate collapse already folds the target "
                    + $"'{alias.AliasOf}' into '{foldedTarget}'");
            }
            else if (sources.Contains(alias.AliasOf))
            {
                errors.Add(BindingErrorCategory.Curation, alias.Schema, "schema aliases cannot chain");
            }
            else if (FindDuplicateFoldedInto(collapse, alias.Schema) is { } duplicate)
            {
                errors.Add(BindingErrorCategory.Curation, alias.Schema,
                    "schema aliases cannot chain: the stabilize-duplicate collapse already folds "
                    + $"'{duplicate}' into '{alias.Schema}'");
            }
        }
    }

    /// <summary>The ordinally first stabilize duplicate the collapse folds into <paramref name="schema"/>, if any.</summary>
    private static string? FindDuplicateFoldedInto(StabilizeDuplicateCollapse collapse, string schema) =>
        collapse
            .Aliases.Where(fold => string.Equals(fold.Value, schema, StringComparison.Ordinal))
            .Select(static fold => fold.Key)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
}
