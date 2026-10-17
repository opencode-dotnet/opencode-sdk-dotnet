using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Binds the <see cref="OperationPlan.Pagination"/> facet: a cursor-list operation whose
/// request derives from the <c>ListRequest</c> base additionally gains an enumeration method.
/// </summary>
internal sealed class PaginationFacetBinder(OperationFacetContext context)
{
    private readonly OperationFacetContext _context = context.CreateLocal();

    public FacetResult<PaginationPlan?> Bind(string methodName, IReadOnlyList<OperationParameterPlan>? parameters,
        QueryRequestPlan? queryRequest, RequestBodyPlan? requestBody,
        EnvelopePlan? envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        if (queryRequest is not { DerivesFromListRequest: true, RidesRequestBody: false }
            || envelope is not
            {
                Kind: EnvelopeKind.CursorList,
                PayloadName: { } payloadName,
                ResponseTypeName: { } pageTypeName,
                PayloadType: ListTypeReferencePlan { ElementType: NamedTypeReferencePlan { Name: { } itemTypeName } },
            })
        {
            return new FacetResult<PaginationPlan?>.Bound(null);
        }

        if (!string.Equals(_context.Operation.Method, "get", StringComparison.Ordinal)
            || requestBody is not null
            || parameters is null
            || parameters.Any(static parameter => !parameter.IsHandleParameter))
        {
            return new FacetResult<PaginationPlan?>.Bound(null);
        }

        // The traversal core takes the one-page method as a fixed three-argument delegate,
        // so an extra emitted parameter would break the method-group conversion.
        if (_context.Operation.Parameters.Any(static parameter => parameter.Location is SpecParameterLocation.Header))
        {
            return new FacetResult<PaginationPlan?>.Bound(null);
        }

        // A continuation is the caller's first request with a new cursor, so the traversal must be
        // able to start one from no request at all; a required filter has no value to carry there.
        if (queryRequest.HasRequiredMember)
        {
            return new FacetResult<PaginationPlan?>.Bound(null);
        }

        var enumerationMethodName = OperationNamePolicy.EnumerationMethodName(methodName);
        if (enumerationMethodName is null)
        {
            _context.Errors.Add(
                BindingErrorCategory.Naming,
                _context.Operation.OperationId,
                $"cursor-list method '{methodName}' must be an asynchronous List method before its enumeration name can be derived");
            return _context.Refused<PaginationPlan?>();
        }

        return new FacetResult<PaginationPlan?>.Bound(new PaginationPlan
        {
            MethodName = enumerationMethodName,
            RequestTypeName = queryRequest.TypeName,
            PageTypeName = pageTypeName,
            ItemTypeName = itemTypeName,
            PayloadName = payloadName,
        });
    }
}
