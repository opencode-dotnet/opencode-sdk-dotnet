using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Walls;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal sealed class OperationProjector
{
    private readonly ParameterProjector _parameters;
    private readonly RequestBodyProjector _requestBodies;
    private readonly ResponseProjector _responses;

    public OperationProjector(SchemaProjector schemaProjector)
    {
        ArgumentNullException.ThrowIfNull(schemaProjector);

        _parameters = new ParameterProjector(schemaProjector);
        _requestBodies = new RequestBodyProjector(schemaProjector);
        _responses = new ResponseProjector(schemaProjector, new EnvelopeClassifier());
    }

    public IReadOnlyList<SpecOperation> Project(LoadedSpec loaded, ProjectionState state,
        IReadOnlyDictionary<string, string> operationIdentities)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(operationIdentities);

        if (loaded.Raw is not JsonObject rawDocument || rawDocument["paths"] is not JsonObject rawPaths)
        {
            state.Errors.Add("document/paths", "raw paths object was unavailable");
            return Array.AsReadOnly(Array.Empty<SpecOperation>());
        }

        var context = new OperationProjectionContext(rawPaths, state, operationIdentities);
        foreach (var (path, pathItem) in loaded.Document.Paths)
        {
            ProjectPath(path, pathItem, context);
        }

        foreach (var subject in context.UnconsumedIdentitySubjects())
        {
            state.Errors.Add(subject, "operation identity curation subject does not exist in the document; retire the row");
        }

        return context.Snapshot();
    }

    private void ProjectPath(string path, IOpenApiPathItem pathItem, OperationProjectionContext context)
    {
        var pathLocation = $"paths/{path}";
        PathItemWallPolicy.Check(pathItem, pathLocation, context.State.Errors);
        if (pathItem is not OpenApiPathItem { Operations: not null } concrete)
        {
            return;
        }

        if (!context.TryGetRawPath(path, out var rawPathItem))
        {
            context.State.Errors.Add(pathLocation, "raw path item was unavailable");
            return;
        }

        foreach (var operation in concrete.Operations)
        {
            ProjectOperation(path, operation, rawPathItem, context);
        }
    }

    private void ProjectOperation(string path, KeyValuePair<HttpMethod, OpenApiOperation> source, JsonObject rawPathItem,
        OperationProjectionContext context)
    {
        var (method, operation) = source;
        if (!PathItemWallPolicy.IsAdmitted(method))
        {
            return;
        }

        var normalizedMethod = method.Method.ToLowerInvariant();
        var location = $"paths/{path}/{normalizedMethod}";
        if (rawPathItem[normalizedMethod] is not JsonObject rawOperation)
        {
            context.State.Errors.Add(location, "raw operation was unavailable");
            return;
        }

        var operationErrorCount = context.State.Errors.Count;
        var isWebSocket = OperationExtensionPolicy.Check(operation, location, context.State.Errors);
        if (string.IsNullOrWhiteSpace(operation.OperationId))
        {
            context.State.Errors.Add(location, "operationId is required");
            return;
        }

        var operationId = operation.OperationId;
        if (!context.TryRegisterOperationId(operationId))
        {
            context.State.Errors.Add(location, $"duplicate operationId '{operationId}'");
            return;
        }

        // An operation-identity row maps an upstream identity defect onto its intended
        // identity (ADR-0013); every downstream consumer sees only the intended identity,
        // and the mapped identity claims the same uniqueness space as ordinary ids.
        if (context.TryMapIdentity(operationId, out var intendedIdentity))
        {
            operationId = intendedIdentity;
            if (!context.TryRegisterOperationId(operationId))
            {
                context.State.Errors.Add(location, $"operation identity '{operationId}' collides with another operation");
                return;
            }
        }

        var identity = OperationIdentityParser.Parse(operationId, path, location, context.State.Errors);
        if (identity is null)
        {
            return;
        }

        var root = GraphKeyBuilder.Root($"op:{operationId}");
        var parameters = _parameters.Project(operation, rawOperation, root, context.State);
        PathTemplateValidator.Check(path, parameters, location, context.State.Errors);
        var requestBody = _requestBodies.Project(operation.RequestBody, root, context.State);
        var responses = _responses.Project(operation.Responses, root, context.State);
        if (context.State.Errors.Count != operationErrorCount)
        {
            return;
        }

        context.Add(new SpecOperation
        {
            OperationId = operationId,
            Segments = identity.Segments,
            Method = normalizedMethod,
            Path = path,
            HasWildcardPath = identity.HasWildcardPath,
            IsWebSocket = isWebSocket,
            IsSse = responses.Any(static response => response.IsSse),
            IsDeprecated = operation.Deprecated,
            Summary = operation.Summary,
            Description = operation.Description,
            Parameters = parameters,
            RequestBody = requestBody,
            Responses = responses,
        });
    }
}
