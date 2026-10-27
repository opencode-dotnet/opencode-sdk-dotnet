using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>A key-wide naming claim must not rename an unrelated reachable wire role.</summary>
internal sealed class ClaimReferrerValidator(SpecDocument document, ReachableSchemaSet reachable, IReadOnlyList<SpecOperation> selected)
{
    private readonly SpecDocument _document = document ?? throw new ArgumentNullException(nameof(document));
    private readonly ReachableSchemaSet _reachable = reachable ?? throw new ArgumentNullException(nameof(reachable));
    private readonly IReadOnlyList<SpecOperation> _selected = selected ?? throw new ArgumentNullException(nameof(selected));

    public void Check(string key, string claim, IEnumerable<SchemaNode> claimingEdges,
        IReadOnlyCollection<string> claimedKeys, BindingErrorCollector errors)
    {
        // Equal reference records can belong to different wire roles; edge exemptions use reference identity.
        var exemptions = claimingEdges.ToHashSet(ReferenceEqualityComparer.Instance);
        var referrers = _reachable.GraphKeys
            .Where(owner => !claimedKeys.Contains(owner, StringComparer.Ordinal)
                && _document.Schemas.TryGetValue(owner, out var schema) && References(schema))
            .Select(static owner => $"schema '{owner}'")
            .Concat(_selected.Where(operation => OperationRoots(operation).Any(References))
                .Select(static operation => $"operation '{operation.OperationId}'"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (referrers.Length > 0)
        {
            errors.Add(BindingErrorCategory.Naming, key,
                $"{claim} claim cannot rename a key also referenced by {string.Join(", ", referrers)}");
        }

        bool References(SchemaNode? schema) => schema is not null && !exemptions.Contains(schema)
            && ((schema is RefNode reference && string.Equals(reference.Target, key, StringComparison.Ordinal))
                || schema.Children.Any(References));
    }

    private static IEnumerable<SchemaNode?> OperationRoots(SpecOperation operation) => operation.Parameters
        .Select(static parameter => parameter.Schema).Prepend(operation.RequestBody?.Schema)
        .Concat(operation.Responses.SelectMany(static response => new[]
        {
            response.Schema, response.EffectStream?.CauseSchema, response.EffectStream?.ErrorSchema,
        }));
}
