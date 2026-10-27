using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static class RequestBodyRootNameResolver
{
    /// <summary>
    /// Every selected operation's nominal body root — inline or component-referenced — is
    /// named from its operation identity, the mechanical <c>{Subject}{Verb}Request</c> rule
    /// (public names never surface raw operation ids or dotted component spellings).
    /// </summary>
    /// <remarks>
    /// A claim renames a root only when the selected bodies and the root's own references are its
    /// sole reachable referrers; another schema or operation root refuses. Two selected operations
    /// claiming one root under different names refuse. Nested component dependencies stay shared.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Resolve(IReadOnlyList<SpecOperation> selected,
        ClaimReferrerValidator referrers, BindingErrorCollector errors)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in selected)
        {
            if (operation.RequestBody?.Schema is not RefNode reference)
            {
                continue;
            }

            var requestName = OperationNamePolicy.RequestTypeName(operation);
            if (names.TryGetValue(reference.Target, out var existing) && !StringComparer.Ordinal.Equals(existing, requestName))
            {
                errors.Add(
                    BindingErrorCategory.Naming,
                    reference.Target,
                    $"request body root is claimed as both '{existing}' and '{requestName}' by selected operations");
                continue;
            }

            names[reference.Target] = requestName;
        }

        foreach (var key in names.Keys)
        {
            referrers.Check(key, "request-body", selected.Select(static operation => operation.RequestBody?.Schema).OfType<RefNode>(), [key], errors);
        }

        return new ReadOnlyDictionary<string, string>(names);
    }
}
