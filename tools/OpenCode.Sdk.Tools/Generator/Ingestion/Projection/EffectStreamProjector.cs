using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

/// <summary>Projects the typed semantics carried by an <c>x-effect-stream</c> extension.</summary>
internal sealed class EffectStreamProjector
{
    private readonly SchemaProjector _schemas;

    public EffectStreamProjector(SchemaProjector schemas)
    {
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
    }

    public SpecEffectStreamContract? Project(JsonNode? source, string root, string mediaPointer, ProjectionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(mediaPointer);
        ArgumentNullException.ThrowIfNull(state);

        if (source is null)
        {
            return null;
        }

        var pointer = GraphKeyBuilder.Append(mediaPointer, "x-effect-stream");
        var location = string.Concat(root, pointer);
        if (source is not JsonObject extension)
        {
            state.Errors.Add(location, "media extension 'x-effect-stream' must contain a JSON object");
            return null;
        }

        return new SpecEffectStreamContract
        {
            Encoding = ReadString(extension, "encoding", root, pointer, state.Errors),
            CauseSchema = ProjectSchema(extension, "causeSchema", root, pointer, state),
            ErrorSchema = ProjectSchema(extension, "errorSchema", root, pointer, state),
            FailureEventName = ReadString(extension, "failureEvent", root, pointer, state.Errors),
        };
    }

    private SchemaNode? ProjectSchema(JsonObject extension, string name, string root, string pointer, ProjectionState state)
    {
        if (!extension.TryGetPropertyValue(name, out var raw))
        {
            return null;
        }

        var schemaPointer = GraphKeyBuilder.Append(pointer, name);
        var location = string.Concat(root, schemaPointer);
        if (raw is null)
        {
            state.Errors.Add(location, $"'{name}' must contain a schema");
            return null;
        }

        var parsed = OpenApiModelFactory.Parse<OpenApiSchema>(
            raw.ToJsonString(),
            OpenApiSpecVersion.OpenApi3_1,
            state.Document,
            out var diagnostic,
            "json");
        foreach (var error in diagnostic.Errors)
        {
            state.Errors.Add(location, error.Message);
        }

        if (diagnostic.Errors.Count > 0)
        {
            return null;
        }

        if (parsed is null)
        {
            state.Errors.Add(location, $"'{name}' did not produce a schema");
            return null;
        }

        return _schemas.Project(parsed, root, schemaPointer, state);
    }

    private static string? ReadString(JsonObject extension, string name, string root, string pointer, IngestionErrorCollector errors)
    {
        if (!extension.TryGetPropertyValue(name, out var raw))
        {
            return null;
        }

        if (raw is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        errors.Add(string.Concat(root, GraphKeyBuilder.Append(pointer, name)), $"'{name}' must contain a string");
        return null;
    }
}
