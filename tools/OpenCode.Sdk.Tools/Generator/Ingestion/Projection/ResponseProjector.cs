using System.Globalization;
using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal sealed class ResponseProjector
{
    private readonly EnvelopeClassifier _envelopes;
    private readonly EffectStreamProjector _effectStreams;
    private readonly SchemaProjector _schemaProjector;

    public ResponseProjector(SchemaProjector schemaProjector, EnvelopeClassifier envelopes)
    {
        _schemaProjector = schemaProjector ?? throw new ArgumentNullException(nameof(schemaProjector));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
        _effectStreams = new EffectStreamProjector(_schemaProjector);
    }

    public IReadOnlyList<SpecResponse> Project(OpenApiResponses? responses, string root, ProjectionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(state);

        if (responses is null)
        {
            return Array.AsReadOnly(Array.Empty<SpecResponse>());
        }

        var parsed = new List<KeyValuePair<int, IOpenApiResponse>>(responses.Count);
        var statusCodes = new HashSet<int>();
        foreach (var (key, response) in responses)
        {
            var keyLocation = string.Concat(root, GraphKeyBuilder.Append(GraphKeyBuilder.Append(string.Empty, "responses"), key));
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
            {
                state.Errors.Add(keyLocation, $"response key '{key}' must be an integer status code");
                continue;
            }

            if (!statusCodes.Add(statusCode))
            {
                state.Errors.Add(keyLocation, $"response status code '{statusCode.ToString(CultureInfo.InvariantCulture)}' is duplicated");
                continue;
            }

            parsed.Add(new KeyValuePair<int, IOpenApiResponse>(statusCode, response));
        }

        var projected = new List<SpecResponse>(parsed.Count);
        foreach (var entry in parsed.OrderBy(static entry => entry.Key))
        {
            var response = ProjectResponse(entry.Key, entry.Value, root, state);
            if (response is not null)
            {
                projected.Add(response);
            }
        }

        return Array.AsReadOnly([.. projected]);
    }

    private SpecResponse? ProjectResponse(int statusCode, IOpenApiResponse response, string root, ProjectionState state)
    {
        var status = statusCode.ToString(CultureInfo.InvariantCulture);
        var pointer = GraphKeyBuilder.Append(GraphKeyBuilder.Append(string.Empty, "responses"), status);
        var location = string.Concat(root, pointer);
        var errorCount = state.Errors.Count;
        if (response is not OpenApiResponse)
        {
            state.Errors.Add(location, $"response implementation '{response.GetType().Name}' is not supported");
            return null;
        }

        // Two media entries would leave the envelope/payload mapping ambiguous.
        if (response.Content is { Count: > 1 })
        {
            state.Errors.Add(location, "response content supports at most one media type");
            return null;
        }

        if (response.Content is not { Count: 1 } content)
        {
            return state.Errors.Count != errorCount
                ? null
                : new SpecResponse
                {
                    StatusCode = statusCode,
                    Description = response.Description,
                    ContentType = null,
                    Schema = null,
                    EnvelopeShape = _envelopes.Classify(contentType: null, schema: null, location, state.Errors),
                    IsSse = false,
                    EffectStream = null,
                };
        }

        var mediaEntry = content.Single();
        var media = MediaTypeProjector.ProjectMediaEntry(mediaEntry, root, pointer, state, out var mediaPointer);
        if (media is null)
        {
            return null;
        }

        SchemaNode? schema = null;
        if (mediaEntry.Value.Schema is not null)
        {
            schema = _schemaProjector.Project(mediaEntry.Value.Schema, root, GraphKeyBuilder.Append(mediaPointer, "schema"), state);
        }

        var effectStream = _effectStreams.Project(media.EffectStream, root, mediaPointer, state);

        var envelope = _envelopes.Classify(media.ContentType, mediaEntry.Value.Schema, location, state.Errors);
        return state.Errors.Count != errorCount
            ? null
            : new SpecResponse
            {
                StatusCode = statusCode,
                Description = response.Description,
                ContentType = media.ContentType,
                Schema = schema,
                EnvelopeShape = envelope,
                IsSse = media.ContentType.IsEventStream,
                EffectStream = effectStream,
            };
    }
}
