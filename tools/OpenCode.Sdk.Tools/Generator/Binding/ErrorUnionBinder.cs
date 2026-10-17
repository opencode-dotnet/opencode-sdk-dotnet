using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class ErrorUnionBinder(SchemaBindingContext context)
{
    private readonly SchemaBindingContext _context = context with { Errors = new BindingErrorCollector() };
    private readonly BindingErrorCollector _publishedErrors = context.Errors;

    public ErrorUnionBindingResult Bind()
    {
        var errorsInClosure = new List<KeyValuePair<string, ObjectNode>>();
        foreach (var key in _context.Reachable.GraphKeys)
        {
            if (!_context.ResponseRootKeys.Contains(key)
                && !_context.StreamCauseKeys.Contains(key)
                && _context.Document.Schemas.TryGetValue(key, out var schema)
                && schema is ObjectNode { ErrorStyle: not ErrorStyle.None } objectNode)
            {
                errorsInClosure.Add(new KeyValuePair<string, ObjectNode>(key, objectNode));
            }
        }

        var variants = new List<UnionVariantPlan>(errorsInClosure.Count);
        var edges = new List<UnionChildEdge>();
        var admitted = new Dictionary<string, UnionVariantPlan>(StringComparer.Ordinal);
        var refused = new Dictionary<string, BindingError>(StringComparer.Ordinal);
        foreach (var (key, objectNode) in errorsInClosure.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!_context.TypeNames.TryGetValue(key, out var typeName))
            {
                refused.Add(key, _context.Errors.Add(BindingErrorCategory.Naming, key, "error schema has no unique C# type name"));
                continue;
            }

            if (ErrorMarkerPolicy.Resolve(objectNode, out var problem) is not { } marker)
            {
                refused.Add(key, _context.Errors.Add(BindingErrorCategory.Schema, key, problem));
                continue;
            }

            edges.Add(new UnionChildEdge(key, ErrorMarkerPolicy.InterfaceTypeName));
            var variant = new UnionVariantPlan
            {
                TypeName = typeName,
                Tag = marker.Value,
                MarkerWireName = marker.PropertyName,
            };
            variants.Add(variant);
            admitted.Add(key, variant);
        }

        UnionPlan? plan = null;
        if (variants.Count > 0 && HasUniqueErrorTags(variants, _context.Errors))
        {
            plan = new UnionPlan
            {
                Name = ErrorMarkerPolicy.InterfaceTypeName,
                ConceptName = ErrorMarkerPolicy.ErrorConceptName,
                Namespace = GeneratedNamespace.Models,
                UnknownTypeName = ErrorMarkerPolicy.UnknownTypeName,
                MarkerWireName = ErrorMarkerPolicy.WireName,
                MarkerName = ErrorMarkerPolicy.MarkerMemberName,
                MarkerKind = LiteralKind.String,
                Variants = [.. variants.OrderBy(static variant => variant.TypeName, StringComparer.Ordinal)],
                Description = "Represents a typed error returned by the opencode API.",
            };
        }

        var errors = _context.Errors.Snapshot();
        _publishedErrors.Append(errors);
        return new ErrorUnionBindingResult(new UnionBindingResult(null, plan, plan is null ? [] : edges.AsReadOnly()),
            new ReadOnlyDictionary<string, UnionVariantPlan>(admitted), new ReadOnlyDictionary<string, BindingError>(refused), errors);
    }

    /// <summary>
    /// Duplicate tags would poison dispatch; refuse naming their sorted owners. Structurally
    /// identical duplicates can be resolved with a schema alias.
    /// </summary>
    private static bool HasUniqueErrorTags(IReadOnlyList<UnionVariantPlan> variants, BindingErrorCollector errors)
    {
        var duplicates = variants
            .GroupBy(static variant => variant.Tag, StringComparer.Ordinal)
            .Where(static group => group.Skip(1).Any())
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in duplicates)
        {
            var owners = string.Join(", ", group.Select(static variant => variant.TypeName).Order(StringComparer.Ordinal));
            errors.Add(BindingErrorCategory.Schema, ErrorMarkerPolicy.ErrorConceptName, $"multiple error schemas declare tag '{group.Key}' ({owners})");
        }

        return duplicates.Length is 0;
    }
}
