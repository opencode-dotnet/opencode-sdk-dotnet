using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Assigns each operation to the group that owns it: the longest curated dotted prefix of its identifier.</summary>
/// <remarks>
/// A <c>groups</c> row keyed by a dotted prefix owns every operation whose identifier starts with it,
/// and a longer curated prefix wins over a shorter one. The identifier itself is never remapped; naming
/// sees it from the prefix's last segment on, except accessor-free internal-raw subgroups inherit
/// their nearest curated parent's effective naming boundary.
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
            : document with { Operations = [.. document.Operations.Select(operation => Assign(operation, prefixes, groups))] };
    }

    public static bool TryGetInheritedParent(string prefix, IReadOnlyDictionary<string, GroupCuration> groups, out string parent)
    {
        parent = groups.Keys.Where(key => prefix.StartsWith(key + ".", StringComparison.Ordinal))
            .OrderByDescending(static key => key.Length).FirstOrDefault() ?? string.Empty;
        return parent.Length > 0 && groups[prefix] is { Emission: EmissionMode.InternalRaw, RootAccessor: false };
    }

    private static SpecOperation Assign(SpecOperation operation, string[] prefixes, IReadOnlyDictionary<string, GroupCuration> groups)
    {
        var prefix = prefixes.FirstOrDefault(prefix => operation.OperationId.StartsWith(prefix + ".", StringComparison.Ordinal));
        return prefix is null
            ? operation
            : operation with
            {
                Group = prefix,
                Segments = [.. operation.OperationId.Split('.').Skip(GetNamingPrefix(prefix, groups).Split('.').Length - 1)],
            };
    }

    private static string GetNamingPrefix(string prefix, IReadOnlyDictionary<string, GroupCuration> groups)
    {
        while (TryGetInheritedParent(prefix, groups, out var parent))
        {
            prefix = parent;
        }

        return prefix;
    }
}
