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

        var context = new QueryParameterValidationContext(document,
            document.Operations.Select(static operation => operation.OperationId).ToHashSet(StringComparer.Ordinal),
            selected.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal), errors);
        var typed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in curation.QueryParameterTypes)
        {
            var subject = row.OperationId + ":" + row.Parameter;
            _ = typed.Add(subject);

            ValidateSubject(context, row.OperationId, row.Parameter, subject);
            ValidateTypedRow(row, subject, errors);
        }

        foreach (var row in curation.StringQueryParameters)
        {
            var subject = row.OperationId + ":" + row.Parameter;
            if (typed.Contains(subject))
            {
                errors.Add(BindingErrorCategory.Curation, subject,
                    "the query parameter is classified twice: a queryParameterTypes row types it, so its stringQueryParameters row must go");
            }

            ValidateSubject(context, row.OperationId, row.Parameter, subject);
        }
    }

    private static void ValidateTypedRow(QueryParameterTypeCuration row, string subject, BindingErrorCollector errors)
    {
        if (QueryParameterTypePolicy.KindOf(row.DotNetType) is null)
        {
            errors.Add(BindingErrorCategory.Curation, subject,
                $"query parameter type '{row.DotNetType}' is not '{QueryParameterTypePolicy.Int32Keyword}' or '{QueryParameterTypePolicy.Int64Keyword}'");
        }

        if (string.IsNullOrWhiteSpace(row.Evidence.Path) || string.IsNullOrWhiteSpace(row.Evidence.Anchor))
        {
            errors.Add(BindingErrorCategory.Curation, subject,
                "query parameter type curation must name its evidence: the upstream file and the text that declares the decode");
        }
    }

    private static void ValidateSubject(QueryParameterValidationContext context, string operationId, string parameterName, string subject)
    {
        var errors = context.Errors;
        if (!context.Operations.TryGetValue(operationId, out var operation))
        {
            errors.Add(BindingErrorCategory.Curation, subject, context.DocumentIds.Contains(operationId)
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

        switch (ResolveValue(context.Document, parameter.Schema))
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

    /// <summary>Follows references and one nullable wrapper to the value a query parameter carries, as the binder does.</summary>
    private static SchemaNode ResolveValue(SpecDocument document, SchemaNode schema)
    {
        var resolved = RefChain.Resolve(schema, document.Schemas).Node;
        return resolved is NullableNode { Format: null } nullable ? RefChain.Resolve(nullable.Inner, document.Schemas).Node : resolved;
    }

    private sealed record QueryParameterValidationContext(SpecDocument Document, IReadOnlySet<string> DocumentIds,
        IReadOnlyDictionary<string, SpecOperation> Operations, BindingErrorCollector Errors);
}
