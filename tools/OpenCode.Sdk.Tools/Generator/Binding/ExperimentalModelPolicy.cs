using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Finds the generated types only experimental operations reach: the operations of each id reach
/// them and no stable operation does, by the same derived reach the reach report uses. A type that
/// two ids reach alone has no single family to belong to, so it is refused rather than guessed.
/// </summary>
internal static class ExperimentalModelPolicy
{
    public static IReadOnlyDictionary<string, string> Collect(ReachScope scope,
        OwnershipCandidates candidates, GeneratedTypeNameInventory inventory, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(errors);

        var diagnostics = scope.Clients.SelectMany(static client => client.Operations)
            .ToDictionary(static operation => operation.OperationId, static operation => operation.ExperimentalDiagnostic, StringComparer.Ordinal);
        var stable = ModelReachCollector.CollectSubset(
            scope with { Selected = [.. scope.Selected.Where(static operation => !ExperimentalRoutePolicy.IsExperimental(operation.Path))] },
            candidates, errors);
        var reachedBy = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var subset in scope.Selected.Where(operation => diagnostics.GetValueOrDefault(operation.OperationId) is not null)
                     .GroupBy(operation => diagnostics[operation.OperationId]!, StringComparer.Ordinal))
        {
            var reached = ModelReachCollector.CollectSubset(scope with { Selected = [.. subset] }, candidates, errors);
            foreach (var name in reached.Where(name => inventory.Names.Contains(name) && !stable.Contains(name)))
            {
                if (!reachedBy.TryGetValue(name, out var ids))
                {
                    ids = new SortedSet<string>(StringComparer.Ordinal);
                    reachedBy.Add(name, ids);
                }

                _ = ids.Add(subset.Key);
            }
        }

        foreach (var (name, ids) in reachedBy.Where(static entry => entry.Value.Count > 1).OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            errors.Add(BindingErrorCategory.Curation, name,
                $"experimental-only type '{name}' is reached by multiple diagnostic ids: {string.Join(", ", ids)}");
        }

        return reachedBy.Where(static entry => entry.Value.Count is 1)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value.Single(), StringComparer.Ordinal);
    }
}
