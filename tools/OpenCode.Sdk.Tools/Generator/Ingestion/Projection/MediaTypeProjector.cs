using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal static class MediaTypeProjector
{
    internal static MediaTypeProjection? ProjectMediaEntry(KeyValuePair<string, IOpenApiMediaType> mediaEntry, string root, string pointer,
        ProjectionState state, out string mediaPointer)
    {
        mediaPointer = GraphKeyBuilder.Append(GraphKeyBuilder.Append(pointer, "content"), mediaEntry.Key);
        var mediaLocation = string.Concat(root, mediaPointer);
        return Project(mediaEntry.Key, mediaEntry.Value, mediaLocation, state.Errors);
    }

    public static MediaTypeProjection? Project(string raw, IOpenApiMediaType media, string location, IngestionErrorCollector errors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentNullException.ThrowIfNull(errors);

        SpecMediaType contentType;
        try
        {
            contentType = SpecMediaType.Create(raw);
        }
        catch (ArgumentException exception)
        {
            errors.Add(location, exception.Message);
            return null;
        }

        if (media is not OpenApiMediaType concrete)
        {
            errors.Add(location, $"media type implementation '{media.GetType().Name}' is not supported");
            return null;
        }

        return new MediaTypeProjection(contentType, ExtractEffectStream(concrete, contentType, location, errors));
    }

    private static System.Text.Json.Nodes.JsonNode? ExtractEffectStream(OpenApiMediaType media, SpecMediaType contentType, string location,
        IngestionErrorCollector errors)
    {
        // x-effect-stream is the one media extension with declared semantics (the SSE item
        // contract); it must be JSON and may only sit on text/event-stream. Other extensions
        // carry no generator semantics and are ignored.
        if (media.Extensions is null || !media.Extensions.TryGetValue("x-effect-stream", out var extension))
        {
            return null;
        }

        if (!contentType.IsEventStream)
        {
            errors.Add(location, "media extension 'x-effect-stream' is supported only on text/event-stream");
            return null;
        }

        if (extension is JsonNodeExtension json)
        {
            return json.Node;
        }

        errors.Add(location, "media extension 'x-effect-stream' must contain JSON");
        return null;
    }
}
