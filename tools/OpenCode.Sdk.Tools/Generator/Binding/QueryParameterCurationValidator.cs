using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Validates the two query-parameter classification sections against the document. A row must
/// name a query parameter of a selected operation that the document still projects as a plain
/// string; a row whose parameter is gone, or that the document now declares as a number, is
/// refused so it retires instead of outliving its reason (ADR-0037). That every plain-string
/// parameter has a row is the binder's refusal, not this check's.
/// </summary>
internal static class QueryParameterCurationValidator
{
    public static void Validate(SpecDocument document, IReadOnlyList<SpecOperation> selected, GenerationCuration curation,
        BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(errors);

        var documentIds = document.Operations.Select(static operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);
        var operations = selected.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);
        var typed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in curation.QueryParameterTypes)
        {
            var subject = row.OperationId + ":" + row.Parameter;
            if (!typed.Add(subject))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "query parameter type curation is duplicated");
            }

            ValidateSubject(document, documentIds, operations, row.OperationId, row.Parameter, subject, errors);
            ValidateTypedRow(row, subject, errors);
        }

        var strings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in curation.StringQueryParameters)
        {
            var subject = row.OperationId + ":" + row.Parameter;
            if (!strings.Add(subject))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "string query parameter curation is duplicated");
            }

            if (typed.Contains(subject))
            {
                errors.Add(BindingErrorCategory.Curation, subject,
                    "the query parameter is classified twice: a queryParameterTypes row types it, so its stringQueryParameters row must go");
            }

            ValidateSubject(document, documentIds, operations, row.OperationId, row.Parameter, subject, errors);
            if (string.IsNullOrWhiteSpace(row.Reason))
            {
                errors.Add(BindingErrorCategory.Curation, subject, "string query parameter curation must declare a reason");
            }
        }
    }

    private static void ValidateTypedRow(QueryParameterTypeCuration row, string subject, BindingErrorCollector errors)
    {
        if (QueryParameterTypePolicy.KindOf(row.DotNetType) is null)
        {
            errors.Add(BindingErrorCategory.Curation, subject,
                $"query parameter type '{row.DotNetType}' is not '{QueryParameterTypePolicy.Int32Keyword}' or '{QueryParameterTypePolicy.Int64Keyword}'");
        }

        if (string.IsNullOrWhiteSpace(row.Reason))
        {
            errors.Add(BindingErrorCategory.Curation, subject, "query parameter type curation must declare a reason");
        }

        if (string.IsNullOrWhiteSpace(row.Evidence.Path) || string.IsNullOrWhiteSpace(row.Evidence.Anchor))
        {
            errors.Add(BindingErrorCategory.Curation, subject,
                "query parameter type curation must name its evidence: the upstream file and the text that declares the decode");
        }
    }

    private static void ValidateSubject(SpecDocument document, HashSet<string> documentIds,
        Dictionary<string, SpecOperation> operations, string operationId, string parameterName, string subject,
        BindingErrorCollector errors)
    {
        if (!operations.TryGetValue(operationId, out var operation))
        {
            errors.Add(BindingErrorCategory.Curation, subject, documentIds.Contains(operationId)
                ? "curated operation is not selected by the current profile"
                : "the operation no longer exists in the document; remove this row");
            return;
        }

        var parameter = operation.Parameters.FirstOrDefault(candidate => candidate.Location is SpecParameterLocation.Query
                                                                        && string.Equals(candidate.Name, parameterName, StringComparison.Ordinal));
        if (parameter is null)
        {
            errors.Add(BindingErrorCategory.Curation, subject, $"the operation has no query parameter '{parameterName}'; remove this row");
            return;
        }

        switch (ResolveValue(document, parameter.Schema))
        {
            case PrimitiveNode { Kind: PrimitiveKind.String, Format: null }:
                return;
            case PrimitiveNode { Kind: PrimitiveKind.Integer or PrimitiveKind.Number }:
                errors.Add(BindingErrorCategory.Curation, subject,
                    $"the document now declares query parameter '{parameterName}' as a number; remove this row");
                return;
            default:
                errors.Add(BindingErrorCategory.Curation, subject,
                    $"query parameter '{parameterName}' is no longer a plain string; remove this row");
                return;
        }
    }

    /// <summary>Follows references and one nullable wrapper to the value a query parameter carries.</summary>
    private static SchemaNode ResolveValue(SpecDocument document, SchemaNode schema)
    {
        var resolved = Resolve(document, schema);
        return resolved is NullableNode { Format: null } nullable ? Resolve(document, nullable.Inner) : resolved;
    }

    private static SchemaNode Resolve(SpecDocument document, SchemaNode schema)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = schema;
        while (current is RefNode reference && visited.Add(reference.Target)
                                            && document.Schemas.TryGetValue(reference.Target, out var target))
        {
            current = target;
        }

        return current;
    }
}
