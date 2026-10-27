using System.Diagnostics;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Binds one selected operation, publishing independent facet refusals in binder order.</summary>
internal sealed class SingleOperationBinder(OperationFacetContext context, ErrorUnionBindingResult errorUnion)
{
    private readonly OperationFacetContext _context = context;
    private readonly ErrorUnionBindingResult _errorUnion = errorUnion;

    public BoundOperation? Bind()
    {
        if (!_context.Curation.Groups.TryGetValue(_context.Operation.Segments[0], out var row))
        {
            // The curation validator already reported the missing row.
            return null;
        }

        if (!new OperationWireShapeWall(_context, row.Emission).Check())
        {
            return null;
        }

        var success = _context.Operation.Responses.Single(static response => response.StatusCode is 200 or 204);
        var stream = success.IsSse ? _context.Publish(new StreamFacetBinder(_context).Bind(success)) : new FacetResult<StreamPlan?>.Bound(null);
        var envelope = success.IsSse ? new FacetResult<EnvelopePlan?>.Bound(null) : _context.Publish(new EnvelopeFacetBinder(_context).Bind(success));
        if (success.IsSse && _context.Operation.RequestBody is not null)
        {
            _context.Refuse("streaming operations must not carry a request body");
            return null;
        }

        var errorMap = _context.Publish(new ErrorMapBinder(_context, _errorUnion).Bind(), _errorUnion.Errors);
        var declaredHeaders = BindDeclaredHeaders();
        var parameters = BindParameters(row, declaredHeaders);
        var queryBinder = new QueryRequestFacetBinder(_context);
        var query = _context.Publish(queryBinder.Bind());
        var body = _context.Publish(new RequestBodyFacetBinder(_context).Bind());
        query = MergeRequests(query, body, queryBinder.DiagnosticRequest);
        var handle = parameters?.Any(static parameter => parameter.IsHandleParameter) ?? false;
        var (methodName, routeMemberName, rowRequired) = ResolveNames(row, handle);
        if (rowRequired is not null)
        {
            _context.Refuse(rowRequired);
            return null;
        }

        if (methodName is null || routeMemberName is null)
        {
            return null;
        }

        var pagination = _context.Publish(new PaginationFacetBinder(_context).Bind(methodName, parameters,
            query is FacetResult<QueryRequestPlan?>.Bound q ? q.Value : queryBinder.DiagnosticRequest,
            body is FacetResult<RequestBodyPlan?>.Bound b ? b.Value : null,
            envelope is FacetResult<EnvelopePlan?>.Bound e ? e.Value : null));
        if (stream is not FacetResult<StreamPlan?>.Bound boundStream || envelope is not FacetResult<EnvelopePlan?>.Bound boundEnvelope
            || errorMap is not FacetResult<ErrorMapPlan>.Bound boundErrors || parameters is null
            || query is not FacetResult<QueryRequestPlan?>.Bound boundQuery || body is not FacetResult<RequestBodyPlan?>.Bound boundBody
            || pagination is not FacetResult<PaginationPlan?>.Bound boundPagination)
        {
            return null;
        }

        return new BoundOperation
        {
            OperationId = _context.Operation.OperationId,
            Row = row,
            IsHandleOperation = handle,
            Plan = new OperationPlan
            {
                OperationId = _context.Operation.OperationId,
                MethodName = methodName,
                HttpMethod = _context.Operation.Method,
                RouteTemplate = _context.Operation.Path,
                ExperimentalDiagnostic = ExperimentalRoutePolicy.IsExperimental(_context.Operation.Path) ? row.ExperimentalDiagnostic : null,
                RouteContainerName = row.ClientName ?? CSharpNamePolicy.ToPascalCase(_context.Operation.Segments[0]),
                RouteMemberName = routeMemberName,
                Parameters = parameters,
                DeclaredHeaders = declaredHeaders,
                QueryRequest = boundQuery.Value,
                RequestBody = boundBody.Value,
                Envelope = boundEnvelope.Value,
                Stream = boundStream.Value,
                Pagination = boundPagination.Value,
                ErrorMap = boundErrors.Value,
                Summary = _context.Operation.Summary,
                Description = _context.Operation.Description,
            },
        };
    }

    /// <summary>The row or mechanical name, and the refusal when neither may name the operation.</summary>
    private (string? MethodName, string? RouteMemberName, string? RowRequired) ResolveNames(GroupCuration row, bool handle)
    {
        var curatedName = _context.Curation.OperationNames.FirstOrDefault(operationName =>
            string.Equals(operationName.OperationId, _context.Operation.OperationId, StringComparison.Ordinal));
        if (curatedName is null && (OperationNamePolicy.RowRequiredProblem(_context.Operation)
                                   ?? OperationNamePolicy.AmbiguousPluralProblem(_context.Operation, handle: handle)) is { } rowRequired)
        {
            return (null, null, rowRequired);
        }

        var method = OperationNamePolicy.MethodName(_context.Operation, curatedName, handle);
        var route = OperationNamePolicy.RouteMemberName(_context.Operation, row.Placement, curatedName, handle);
        return (method, route, curatedName is null && (method is null || route is null)
            ? "the operation's names cannot be derived mechanically: the group does not pluralize naively"
            : null);
    }

    private FacetResult<QueryRequestPlan?> MergeRequests(FacetResult<QueryRequestPlan?> query, FacetResult<RequestBodyPlan?> body,
        QueryRequestPlan? request)
    {
        if (request is null || body is not FacetResult<RequestBodyPlan?>.Bound { Value: { IsBinary: false } })
        {
            return query;
        }

        // Only the location selector can ride the body model; raw-byte bodies keep a standalone query.
        if (request.Properties.Any(static property => property.Kind is not QueryValueKind.Location))
        {
            var local = _context.CreateLocal();
            return _context.Publish(local.RefuseFacet<QueryRequestPlan?>("operations mixing a request body and query parameters are supported only for the location selector"));
        }

        // Diagnostic facts never admit a refused query, even when the surviving properties can merge.
        return query is FacetResult<QueryRequestPlan?>.Bound
            ? new FacetResult<QueryRequestPlan?>.Bound(request with { RidesRequestBody = true }) : query;
    }

    /// <summary>The wire wall has admitted these headers; their declaration order carries into the plan.</summary>
    private IReadOnlyList<DeclaredHeaderPlan> BindDeclaredHeaders() =>
    [
        .. _context.Operation.Parameters
            .Where(static parameter => parameter.Location is SpecParameterLocation.Header)
            .Select(static parameter => new DeclaredHeaderPlan
            {
                WireName = parameter.Name,
                Name = CSharpNamePolicy.ToCamelCase(parameter.Name),
            }),
    ];

    private string? CuratedParameterName(string wireName) =>
        _context.Curation.ParameterNames.FirstOrDefault(row =>
                string.Equals(row.OperationId, _context.Operation.OperationId, StringComparison.Ordinal)
                && string.Equals(row.Parameter, wireName, StringComparison.Ordinal))?.DotNetName;

    private IReadOnlyList<OperationParameterPlan>? BindParameters(GroupCuration row, IReadOnlyList<DeclaredHeaderPlan> declaredHeaders)
    {
        var plans = _context.Operation.Parameters
            .Where(static parameter => parameter.Location is SpecParameterLocation.Path)
            .Select(parameter => new OperationParameterPlan
            {
                WireName = parameter.Name,
                Name = CuratedParameterName(parameter.Name) ?? CSharpNamePolicy.ToCamelCase(parameter.Name),
                TypeName = "string",
                IsHandleParameter = row.Placement is GroupPlacement.Client
                                    && string.Equals(parameter.Name, row.HandleParameter, StringComparison.Ordinal),
            }).ToList();
        // Route values and headers share the emitted signature's namespace.
        var names = plans.Select(static plan => plan.Name).Concat(declaredHeaders.Select(static header => header.Name)).ToArray();
        var duplicate = names.GroupBy(static name => name, StringComparer.Ordinal).FirstOrDefault(static name => name.Skip(1).Any());
        if (duplicate is not null)
        {
            _context.Errors.Add(BindingErrorCategory.Naming, _context.Operation.OperationId, $"multiple parameters map to C# name '{duplicate.Key}'");
            return null;
        }

        var reserved = names.Where(static name => ReservedNamePolicy.ParameterNames.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        if (reserved.Length > 0)
        {
            foreach (var name in reserved)
            {
                _context.Errors.Add(BindingErrorCategory.Naming, _context.Operation.OperationId,
                    $"parameter name '{name}' is reserved by the emitted method signature");
            }
            return null;
        }

        var errorCount = _context.Errors.Count;
        foreach (var parameter in plans.Where(static plan => CSharpNamePolicy.IsKeyword(plan.Name)))
        {
            _context.Errors.Add(BindingErrorCategory.Curation, $"{_context.Operation.OperationId}:{parameter.WireName}",
                $"route value '{parameter.Name}' is a C# keyword: add a parameterNames row that names it", BindingErrorKind.MissingRow);
        }
        foreach (var header in declaredHeaders.Where(static header => CSharpNamePolicy.IsKeyword(header.Name)))
        {
            _context.Errors.Add(BindingErrorCategory.Naming, _context.Operation.OperationId,
                $"header parameter '{header.Name}' is a C# keyword, and no curation row renames a header");
        }

        return _context.Errors.Count == errorCount ? [.. plans.OrderBy(plan => TemplatePosition(plan.WireName))] : null;
    }

    private int TemplatePosition(string wireName)
    {
        var position = _context.Operation.Path.IndexOf($"{{{wireName}}}", StringComparison.Ordinal);
        Debug.Assert(position >= 0, "Ingestion guarantees every path parameter appears in the route template.");
        return position;
    }
}
