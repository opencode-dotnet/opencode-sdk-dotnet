using System.Diagnostics;
using System.Globalization;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class OperationPlanBinder
{
    private const string ClientNamespace = "OpenCode.Sdk";
    private const string RootClientName = "OpenCodeClient";

    private readonly StringComparer _comparer = StringComparer.Ordinal;

    public IReadOnlyList<ClientPlan> Bind(SpecDocument document, IReadOnlyList<SpecOperation> selected,
        GenerationCuration curation, IReadOnlyDictionary<string, string> typeNames, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(typeNames);
        ArgumentNullException.ThrowIfNull(errors);

        var typeBinder = new TypePlanBinder(document.Schemas, typeNames, errors);
        var bound = new List<BoundOperation>(selected.Count);
        foreach (var operation in selected.OrderBy(static operation => operation.OperationId, _comparer))
        {
            var operationPlan = new SingleOperationBinder(document, operation, curation, typeNames, errors, typeBinder).Bind();
            if (operationPlan is not null)
            {
                bound.Add(operationPlan);
            }
        }

        if (bound.Count is 0)
        {
            return [];
        }

        var clients = AssembleClients(bound);
        CheckMemberCollisions(clients, errors);
        CheckTypeNameCollisions(clients, bound, typeNames, errors);
        return clients;
    }

    private List<ClientPlan> AssembleClients(List<BoundOperation> bound)
    {
        var rootOperations = bound
            .Where(static operation => operation.Row.Placement is GroupPlacement.Root)
            .Select(static operation => operation.Plan)
            .OrderBy(static plan => plan.MethodName, _comparer)
            .ToArray();
        var subClients = new List<ClientReferencePlan>();
        var clients = new List<ClientPlan>();

        // Wire groups sharing a curated client name merge into one client family; the
        // curation validator has already demanded identical handle declarations.
        foreach (var group in bound
                     .Where(static operation => operation.Row.Placement is GroupPlacement.Client)
                     .GroupBy(static operation => operation.Row.ClientName!, _comparer)
                     .OrderBy(static group => group.Key, _comparer))
        {
            var members = group.ToArray();
            var row = members[0].Row;
            var collectionName = $"{row.ClientName}Client";
            subClients.Add(new ClientReferencePlan
            {
                PropertyName = row.ClientName!,
                TypeName = collectionName,
            });
            clients.AddRange(CreateGroupClients(members, row, collectionName));
        }

        clients.Add(new ClientPlan
        {
            Name = RootClientName,
            Namespace = ClientNamespace,
            Role = ClientRole.Root,
            SubClients = [.. subClients.OrderBy(static reference => reference.PropertyName, _comparer)],
            Operations = rootOperations,
        });

        return
        [
            .. clients
                .OrderBy(static client => client.Role is ClientRole.Root ? 0 : 1)
                .ThenBy(static client => client.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Builds a family's clients. An internal-raw family takes raw type names throughout
    /// (ADR-0021): the public family name belongs to the hand-written door the root client
    /// keeps pointing at, so nothing generated may claim it.
    /// </summary>
    private IEnumerable<ClientPlan> CreateGroupClients(IReadOnlyList<BoundOperation> group, GroupCuration row, string collectionName)
    {
        var handleOperations = group.Where(static operation => operation.IsHandleOperation).ToArray();
        var collectionOperations = group
            .Where(static operation => !operation.IsHandleOperation)
            .Select(static operation => operation.Plan)
            .OrderBy(static plan => plan.MethodName, _comparer)
            .ToArray();
        var handleParameter = handleOperations
            .OrderBy(static operation => operation.Plan.MethodName, _comparer)
            .SelectMany(static operation => operation.Plan.Parameters)
            .FirstOrDefault(static parameter => parameter.IsHandleParameter);
        var collectionTypeName = ClientTypeName(collectionName, row.Emission);
        var handleTypeName = row.HandleName is null ? null : ClientTypeName(row.HandleName, row.Emission);

        yield return new ClientPlan
        {
            Name = collectionTypeName,
            Namespace = ClientNamespace,
            Role = ClientRole.Collection,
            Emission = row.Emission,
            ContainerName = row.ClientName,
            SubClients = [],
            HandleFactory = handleParameter is null
                ? null
                : new HandleFactoryPlan
                {
                    MethodName = $"Get{handleTypeName}",
                    HandleTypeName = handleTypeName!,
                    Parameter = handleParameter,
                },
            Operations = collectionOperations,
        };

        if (handleParameter is not null)
        {
            yield return new ClientPlan
            {
                Name = handleTypeName!,
                Namespace = ClientNamespace,
                Role = ClientRole.Handle,
                Emission = row.Emission,
                ContainerName = row.ClientName,
                SubClients = [],
                HandleParameter = handleParameter,
                Operations =
                [
                    .. handleOperations
                        .Select(static operation => operation.Plan)
                        .OrderBy(static plan => plan.MethodName, _comparer),
                ],
            };
        }
    }

    private static string ClientTypeName(string familyName, EmissionMode emission) =>
        emission is EmissionMode.InternalRaw ? CSharpNamePolicy.ToRawClientName(familyName) : familyName;

    private void CheckMemberCollisions(List<ClientPlan> clients, BindingErrorCollector errors)
    {
        foreach (var client in clients)
        {
            var members = client
                .Operations.Select(static operation => operation.MethodName)
                .Concat(client.Operations
                    .Where(static operation => operation.Pagination is not null)
                    .Select(static operation => operation.Pagination!.MethodName))
                .Concat(client.SubClients.Select(static reference => reference.PropertyName));
            if (client.HandleFactory is not null)
            {
                members = members.Append(client.HandleFactory.MethodName);
            }

            foreach (var member in members
                         .GroupBy(static member => member, _comparer)
                         .Where(static member => member.Skip(1).Any())
                         .OrderBy(static member => member.Key, _comparer))
            {
                errors.Add(BindingErrorCategory.Naming, client.Name, $"multiple members map to C# name '{member.Key}'");
            }
        }
    }

    private void CheckTypeNameCollisions(List<ClientPlan> clients, List<BoundOperation> bound,
        IReadOnlyDictionary<string, string> typeNames, BindingErrorCollector errors)
    {
        // Model names shadow across namespaces (consumer CS0104) and hand-written spine
        // names collide outright (CS0101), so both seed the owner set — reserved names
        // first, each model name individually, because a bulk union would swallow a
        // model/spine duplicate instead of refusing it.
        var owners = new HashSet<string>(_comparer);
        owners.UnionWith(ReservedNamePolicy.SpineTypeNames);
        foreach (var entry in typeNames
                     .Where(entry => !owners.Add(entry.Value) && ReservedNamePolicy.SpineTypeNames.Contains(entry.Value))
                     .OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            errors.Add(
                BindingErrorCategory.Naming,
                entry.Key,
                $"model type name '{entry.Value}' collides with the hand-written spine type '{entry.Value}'");
        }

        foreach (var name in clients.Select(static client => client.Name).Where(name => !owners.Add(name)))
        {
            errors.Add(BindingErrorCategory.Naming, name, $"client type name '{name}' collides with another generated type");
        }

        // A streaming operation declares no response type; its adapter name is claimed below.
        foreach (var operation in bound.Where(operation =>
                     operation.Plan.Envelope is { } envelope && !owners.Add(envelope.ResponseTypeName)))
        {
            errors.Add(
                BindingErrorCategory.Naming,
                operation.OperationId,
                $"multiple operations map to response type '{operation.Plan.Envelope!.ResponseTypeName}'");
        }

        foreach (var operation in bound.Where(operation =>
                     operation.Plan.Stream is { } stream && !owners.Add(stream.AdapterTypeName)))
        {
            errors.Add(
                BindingErrorCategory.Naming,
                operation.OperationId,
                $"multiple operations map to stream adapter '{operation.Plan.Stream!.AdapterTypeName}'");
        }

        // A merged request's type IS the body model, so only standalone query records
        // claim a name of their own.
        foreach (var operation in bound.Where(operation =>
                     operation.Plan.QueryRequest is { RidesRequestBody: false } query && !owners.Add(query.TypeName)))
        {
            errors.Add(
                BindingErrorCategory.Naming,
                operation.OperationId,
                $"request type name '{operation.Plan.QueryRequest!.TypeName}' collides with another generated type");
        }

        var routeMembers = new HashSet<string>(_comparer);
        foreach (var operation in bound.Where(operation =>
                     !routeMembers.Add($"{operation.Plan.RouteContainerName}.{operation.Plan.RouteMemberName}")))
        {
            errors.Add(
                BindingErrorCategory.Naming,
                operation.OperationId,
                $"multiple operations map to route member '{operation.Plan.RouteContainerName}.{operation.Plan.RouteMemberName}'");
        }
    }

    private sealed record BoundOperation
    {
        public required string OperationId { get; init; }

        public required string Group { get; init; }

        public required GroupCuration Row { get; init; }

        public required bool IsHandleOperation { get; init; }

        public required OperationPlan Plan { get; init; }
    }

    /// <summary>
    /// Binds one selected operation, batching every wire-contract refusal it finds. The
    /// per-facet work lives in the facet binders over the shared context; this class owns
    /// the orchestration order, the names, the body-and-query merge policy, the error map,
    /// and the path parameters. A new operation shape is one facet file plus one call here.
    /// </summary>
    private sealed class SingleOperationBinder(
        SpecDocument document,
        SpecOperation operation,
        GenerationCuration curation,
        IReadOnlyDictionary<string, string> typeNames,
        BindingErrorCollector errors,
        TypePlanBinder types)
    {
        private readonly OperationFacetContext _context = new(document, operation, curation, typeNames, errors, types);

        public BoundOperation? Bind()
        {
            var group = _context.Operation.Segments[0];
            if (!_context.Curation.Groups.TryGetValue(group, out var row))
            {
                // The curation validator already reported the missing row.
                return null;
            }

            if (!new OperationWireShapeWall(_context, row.Emission).Check())
            {
                return null;
            }

            var success = _context.Operation.Responses.Single(static response => response.StatusCode is 200 or 204);
            var stream = success.IsSse ? new StreamFacetBinder(_context).Bind(success) : null;
            var envelope = success.IsSse ? null : new EnvelopeFacetBinder(_context).Bind(success);
            if (success.IsSse && _context.Operation.RequestBody is not null)
            {
                _context.Refuse("streaming operations must not carry a request body");
                return null;
            }

            var errorMap = BindErrorMap();
            var declaredHeaders = BindDeclaredHeaders();
            var parameters = BindParameters(row, declaredHeaders);
            var optionalPlanErrorsBefore = _context.Errors.Count;
            var (queryRequest, requestBody) = BindRequests();

            var handle = parameters?.Any(static parameter => parameter.IsHandleParameter) ?? false;
            var (methodName, routeMemberName, rowRequired) = ResolveNames(row, handle);
            if (rowRequired is not null)
            {
                _context.Refuse(rowRequired);
                return null;
            }

            if (methodName is null || routeMemberName is null)
            {
                _context.Refuse("the operation's names cannot be derived mechanically: the group does not pluralize naively");
                return null;
            }

            var pagination = new PaginationFacetBinder(_context)
                .Bind(methodName, parameters, declaredHeaders, queryRequest, requestBody, envelope);

            if ((success.IsSse ? stream is null : envelope is null)
                || errorMap is null || parameters is null || _context.Errors.Count != optionalPlanErrorsBefore)
            {
                return null;
            }

            return new BoundOperation
            {
                OperationId = _context.Operation.OperationId,
                Group = group,
                Row = row,
                IsHandleOperation = handle,
                Plan = new OperationPlan
                {
                    OperationId = _context.Operation.OperationId,
                    MethodName = methodName,
                    HttpMethod = _context.Operation.Method,
                    RouteTemplate = _context.Operation.Path,
                    RouteContainerName = row.ClientName ?? CSharpNamePolicy.ToPascalCase(group),
                    RouteMemberName = routeMemberName,
                    Parameters = parameters,
                    DeclaredHeaders = declaredHeaders,
                    QueryRequest = queryRequest,
                    RequestBody = requestBody,
                    Envelope = envelope,
                    Stream = stream,
                    Pagination = pagination,
                    ErrorMap = errorMap,
                    Summary = _context.Operation.Summary,
                    Description = _context.Operation.Description,
                },
            };
        }

        /// <summary>
        /// The names a row or the mechanical policy give the operation; the third member is the
        /// refusal when neither may name it — the HTTP method is never a name source (ADR-0008).
        /// </summary>
        private (string? MethodName, string? RouteMemberName, string? RowRequired) ResolveNames(GroupCuration row, bool handle)
        {
            var curatedName = _context.Curation.OperationNames.FirstOrDefault(operationName =>
                string.Equals(operationName.OperationId, _context.Operation.OperationId, StringComparison.Ordinal));
            if (curatedName is null && OperationNamePolicy.RowRequiredProblem(_context.Operation) is { } rowRequired)
            {
                return (null, null, rowRequired);
            }

            return (OperationNamePolicy.MethodName(_context.Operation, curatedName, handle),
                OperationNamePolicy.RouteMemberName(_context.Operation, row.Placement, curatedName, handle),
                null);
        }

        private (QueryRequestPlan? Query, RequestBodyPlan? Body) BindRequests()
        {
            var queryRequest = new QueryRequestFacetBinder(_context).Bind();
            var requestBody = new RequestBodyFacetBinder(_context).Bind();
            // A raw-byte body is the caller's stream, not a model the query can ride, so its
            // query parameters keep their own request record beside it.
            if (queryRequest is null || requestBody is null || requestBody.IsBinary)
            {
                return (queryRequest, requestBody);
            }

            // A body and query merge into one uniform request model only for the location
            // channel; every other mix keeps the deliberate wall.
            if (queryRequest.Properties.Any(static property => property.Kind is not QueryValueKind.Location))
            {
                _context.Refuse("operations mixing a request body and query parameters are supported only for the location selector");
                return (queryRequest, requestBody);
            }

            return (queryRequest with { RidesRequestBody = true }, requestBody);
        }

        private ErrorMapPlan? BindErrorMap()
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

                statuses.Add(new ErrorStatusPlan
                {
                    StatusCode = response.StatusCode,
                    Tags = tags,
                });
            }

            return complete
                ? new ErrorMapPlan
                {
                    Statuses = [.. statuses.OrderBy(static status => status.StatusCode)],
                }
                : null;
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
                if (ErrorMarkerPolicy.Resolve(node, out var problem) is not { } marker)
                {
                    return RefuseNullTags($"error responses must reference tagged error schemas: '{key}': {problem}");
                }

                if (!_context.TypeNames.TryGetValue(key, out var typeName))
                {
                    _context.Errors.Add(BindingErrorCategory.Naming, key, "error schema has no unique C# type name");
                    return null;
                }

                tags.Add(new ErrorTagPlan
                {
                    Tag = marker.Value,
                    TypeName = typeName,
                });
            }

            var duplicate = tags.GroupBy(static tag => tag.Tag, StringComparer.Ordinal).FirstOrDefault(static tag => tag.Skip(1).Any());
            if (duplicate is not null)
            {
                return RefuseNullTags(
                    $"status '{response.StatusCode.ToString(CultureInfo.InvariantCulture)}' declares duplicate error tag '{duplicate.Key}'");
            }

            return [.. tags.OrderBy(static tag => tag.Tag, StringComparer.Ordinal)];
        }

        /// <summary>Resolves an error response schema into the object schemas carrying its tags.</summary>
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

        /// <summary>
        /// Binds the declared request headers. The wire-shape wall has already refused a
        /// header the emitted signature could not carry, and refused every header outside an
        /// internal-raw family, so the declaration order carries straight into the plan.
        /// </summary>
        private IReadOnlyList<DeclaredHeaderPlan> BindDeclaredHeaders() =>
        [
            .. _context.Operation
                .Parameters
                .Where(static parameter => parameter.Location is SpecParameterLocation.Header)
                .Select(static parameter => new DeclaredHeaderPlan
                {
                    WireName = parameter.Name,
                    Name = CSharpNamePolicy.ToCamelCase(parameter.Name),
                }),
        ];

        private IReadOnlyList<OperationParameterPlan>? BindParameters(GroupCuration row,
            IReadOnlyList<DeclaredHeaderPlan> declaredHeaders)
        {
            var plans = _context.Operation
                .Parameters
                .Where(static parameter => parameter.Location is SpecParameterLocation.Path)
                .Select(parameter => new OperationParameterPlan
                {
                    WireName = parameter.Name,
                    Name = CSharpNamePolicy.ToCamelCase(parameter.Name),
                    TypeName = "string",
                    IsHandleParameter = row.Placement is GroupPlacement.Client
                                        && string.Equals(parameter.Name, row.HandleParameter, StringComparison.Ordinal),
                })
                .ToList();

            // Route values and declared headers share one emitted signature, so they are
            // checked against one another rather than each within its own location.
            var names = plans
                .Select(static plan => plan.Name)
                .Concat(declaredHeaders.Select(static header => header.Name))
                .ToArray();
            var duplicate = names.GroupBy(static name => name, StringComparer.Ordinal).FirstOrDefault(static name => name.Skip(1).Any());
            if (duplicate is not null)
            {
                _context.Errors.Add(
                    BindingErrorCategory.Naming,
                    _context.Operation.OperationId,
                    $"multiple parameters map to C# name '{duplicate.Key}'");
                return null;
            }

            // Emitted signatures append these names after bind-time checks; a wire parameter
            // landing on one must fail here, never as an emitted compile error.
            var reserved = names
                .Where(static name => ReservedNamePolicy.ParameterNames.Contains(name))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (reserved.Length > 0)
            {
                foreach (var name in reserved)
                {
                    _context.Errors.Add(
                        BindingErrorCategory.Naming,
                        _context.Operation.OperationId,
                        $"parameter name '{name}' is reserved by the emitted method signature");
                }

                return null;
            }

            return [.. plans.OrderBy(plan => TemplatePosition(plan.WireName))];
        }

        private int TemplatePosition(string wireName)
        {
            var position = _context.Operation.Path.IndexOf($"{{{wireName}}}", StringComparison.Ordinal);
            Debug.Assert(position >= 0, "Ingestion guarantees every path parameter appears in the route template.");
            return position;
        }

        private IReadOnlyList<ErrorTagPlan>? RefuseNullTags(string problem)
        {
            _context.Refuse(problem);
            return null;
        }
    }
}
