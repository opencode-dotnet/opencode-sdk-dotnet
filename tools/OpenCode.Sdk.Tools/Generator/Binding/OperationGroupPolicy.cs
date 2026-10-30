using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Assigns each operation to the group that owns it: the longest curated dotted prefix of its identifier.</summary>
/// <remarks>
/// A <c>groups</c> row keyed by a dotted prefix owns every operation whose identifier starts with it,
/// and a longer curated prefix wins over a shorter one. The identifier itself is never remapped; naming
/// sees it from the prefix's last segment on, so <c>experimental.persistentPty.create</c> names as
/// <c>persistentPty.create</c> would.
/// </remarks>
internal static class OperationGroupPolicy
{
    public static SpecDocument Apply(SpecDocument document, IReadOnlyDictionary<string, GroupCuration> groups)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(groups);
        var prefixes = groups.Keys.Where(static key => key.Contains('.', StringComparison.Ordinal))
            .OrderByDescending(static key => key.Length).ToArray();
        return prefixes.Length is 0
            ? document
            : document with { Operations = [.. document.Operations.Select(operation => Assign(operation, prefixes))] };
    }

    private static SpecOperation Assign(SpecOperation operation, string[] prefixes)
    {
        var prefix = prefixes.FirstOrDefault(prefix => operation.OperationId.StartsWith(prefix + ".", StringComparison.Ordinal));
        return prefix is null
            ? operation
            : operation with { Group = prefix, Segments = [.. operation.OperationId.Split('.').Skip(prefix.Split('.').Length - 1)] };
    }
}
