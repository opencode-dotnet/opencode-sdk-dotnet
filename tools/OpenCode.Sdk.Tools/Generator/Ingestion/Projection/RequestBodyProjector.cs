using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal sealed class RequestBodyProjector
{
    private readonly SchemaProjector _schemaProjector;

    public RequestBodyProjector(SchemaProjector schemaProjector)
    {
        _schemaProjector = schemaProjector ?? throw new ArgumentNullException(nameof(schemaProjector));
    }

    public SpecRequestBody? Project(IOpenApiRequestBody? requestBody, string root, ProjectionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(state);

        if (requestBody is null)
        {
            return null;
        }

        var pointer = GraphKeyBuilder.Append(string.Empty, "requestBody");
        var location = string.Concat(root, pointer);
        var errorCount = state.Errors.Count;
        if (requestBody is not OpenApiRequestBody)
        {
            state.Errors.Add(location, $"request body implementation '{requestBody.GetType().Name}' is not supported");
            return null;
        }

        // A multi-media body has no single serialization the generated method could pick.
        if (requestBody.Content is not { Count: 1 } content)
        {
            state.Errors.Add(location, "request body must contain exactly one media type");
            return null;
        }

        var mediaEntry = content.Single();
        var media = MediaTypeProjector.ProjectMediaEntry(mediaEntry, root, pointer, state, out var mediaPointer);
        if (media is null)
        {
            return null;
        }

        if (mediaEntry.Value.Schema is null)
        {
            state.Errors.Add(string.Concat(root, mediaPointer), "request body media schema is required");
            return null;
        }

        var schema = _schemaProjector.Project(mediaEntry.Value.Schema, root, GraphKeyBuilder.Append(mediaPointer, "schema"), state);
        return schema is null || state.Errors.Count != errorCount
            ? null
            : new SpecRequestBody
            {
                ContentType = media.ContentType,
                Schema = schema,
                IsRequired = requestBody.Required,
            };
    }
}
