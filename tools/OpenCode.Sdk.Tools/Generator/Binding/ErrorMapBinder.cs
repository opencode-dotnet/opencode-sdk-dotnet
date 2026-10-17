using System.Globalization;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class ErrorMapBinder(OperationFacetContext context, ErrorUnionBindingResult errorUnion)
{
    private readonly OperationFacetContext _context = context.CreateLocal();
    private readonly ErrorUnionBindingResult _errorUnion = errorUnion;

    public FacetResult<ErrorMapPlan> Bind()
    {
        var statuses = new List<ErrorStatusPlan>();
        var complete = true;
        foreach (var response in _context.Operation.Responses.Where(static response => response.StatusCode is >= 400))
        {
            var tags = BindErrorStatus(response);
            if (tags is null)
            {
                complete = false;
                continue;
            }
            statuses.Add(new ErrorStatusPlan { StatusCode = response.StatusCode, Tags = tags });
        }

        return complete
            ? new FacetResult<ErrorMapPlan>.Bound(new ErrorMapPlan { Statuses = [.. statuses.OrderBy(static status => status.StatusCode)] })
            : _context.Refused<ErrorMapPlan>();
    }

    private IReadOnlyList<ErrorTagPlan>? BindErrorStatus(SpecResponse response)
    {
        if (response.ContentType is not { IsJson: true } || response.Schema is null)
        {
            return RefuseNullTags("error responses must carry a JSON schema");
        }

        var targets = ResolveErrorTargets(response.Schema);
        if (targets is null)
        {
            return RefuseNullTags("error responses must reference tagged error schemas");
        }

        var tags = new List<ErrorTagPlan>(targets.Count);
        foreach (var (key, node) in targets)
        {
            if (_errorUnion.Refusals.TryGetValue(key, out var refusal))
            {
                if (refusal.Category is BindingErrorCategory.Naming)
                {
                    // An unnamed closure member has no marker verdict yet; its marker remains an independent wall.
                    if (ErrorMarkerPolicy.Resolve(node, out var markerProblem) is null)
                    {
                        return RefuseNullTags($"error responses must reference tagged error schemas: '{key}': {markerProblem}");
                    }
                    _context.Errors.Append([refusal]);
                    return null;
                }
                return RefuseNullTags($"error responses must reference tagged error schemas: '{key}': {refusal.Problem}");
            }

            if (_errorUnion.Variants.TryGetValue(key, out var variant))
            {
                tags.Add(new ErrorTagPlan { Tag = variant.Tag, TypeName = variant.TypeName });
                continue;
            }

            // Untagged/name-data objects and stream causes do not belong to the error-union closure.
            if (ErrorMarkerPolicy.Resolve(node, out var problem) is not { } marker)
            {
                return RefuseNullTags($"error responses must reference tagged error schemas: '{key}': {problem}");
            }

            if (!_context.TypeNames.TryGetValue(key, out var typeName))
            {
                _context.Errors.Add(BindingErrorCategory.Naming, key, "error schema has no unique C# type name");
                return null;
            }

            tags.Add(new ErrorTagPlan { Tag = marker.Value, TypeName = typeName });
        }

        var duplicate = tags.GroupBy(static tag => tag.Tag, StringComparer.Ordinal).FirstOrDefault(static tag => tag.Skip(1).Any());
        if (duplicate is not null)
        {
            return RefuseNullTags($"status '{response.StatusCode.ToString(CultureInfo.InvariantCulture)}' declares duplicate error tag '{duplicate.Key}'");
        }

        return [.. tags.OrderBy(static tag => tag.Tag, StringComparer.Ordinal)];
    }

    /// <summary>Resolves a direct object or one level of marked-union branches, not arbitrary schema topology.</summary>
    private List<KeyValuePair<string, ObjectNode>>? ResolveErrorTargets(SchemaNode schema)
    {
        if (schema is not RefNode reference || !_context.Document.Schemas.TryGetValue(reference.Target, out var target))
        {
            return null;
        }

        switch (target)
        {
            case ObjectNode objectNode:
                return [new KeyValuePair<string, ObjectNode>(reference.Target, objectNode)];
            case UnionNode { Classification: UnionClassification.Marked } union:
                var branches = new List<KeyValuePair<string, ObjectNode>>(union.Branches.Count);
                foreach (var branch in union.Branches)
                {
                    if (branch is not RefNode branchReference
                        || !_context.Document.Schemas.TryGetValue(branchReference.Target, out var branchTarget)
                        || branchTarget is not ObjectNode branchNode)
                    {
                        return null;
                    }
                    branches.Add(new KeyValuePair<string, ObjectNode>(branchReference.Target, branchNode));
                }
                return branches;
            default:
                return null;
        }
    }

    private IReadOnlyList<ErrorTagPlan>? RefuseNullTags(string problem)
    {
        _context.Refuse(problem);
        return null;
    }
}
