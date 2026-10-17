using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Binds the <see cref="OperationPlan.Stream"/> facet of a streaming success.</summary>
internal sealed class StreamFacetBinder(OperationFacetContext context)
{
    private readonly OperationFacetContext _context = context.CreateLocal();

    /// <summary>
    /// Reads a streaming success: the frame profile the contract declares, the payload its
    /// JSON-encoded data field carries, and the event name that reports a mid-stream failure.
    /// Every part is required — a stream whose shape is not fully declared is refused.
    /// </summary>
    public FacetResult<StreamPlan?> Bind(SpecResponse success)
    {
        ArgumentNullException.ThrowIfNull(success);

        if (success.ContentType is not { IsEventStream: true } || success.Schema is null)
        {
            return _context.RefuseFacet<StreamPlan?>("a streaming success must carry a text/event-stream schema");
        }

        if (success.Schema is not RefNode reference
            || !_context.Document.Schemas.TryGetValue(reference.Target, out var target)
            || target is not ObjectNode frame)
        {
            return _context.RefuseFacet<StreamPlan?>("the event frame must reference a named object schema");
        }

        var id = frame.Properties.FirstOrDefault(static property => property.Name is "id");
        var eventName = frame.Properties.FirstOrDefault(static property => property.Name is "event");
        var data = frame.Properties.FirstOrDefault(static property => property.Name is "data");
        if (frame.Properties.Count is not 3
            || frame.Properties.Any(static property => property.Name is not ("id" or "event" or "data"))
            || id is null
            || eventName is not { IsRequired: true }
            || data is not { IsRequired: true })
        {
            return _context.RefuseFacet<StreamPlan?>("the event frame must declare exactly 'id', 'event' and 'data', and require 'event' and 'data'");
        }

        if (!SpineShapePolicy.IsNullableUnformattedString(_context, id.Schema))
        {
            return _context.RefuseFacet<StreamPlan?>("the event frame 'id' must be a nullable string");
        }

        if (_context.Resolve(eventName.Schema) is not PrimitiveNode { Kind: PrimitiveKind.String, Format: null })
        {
            return _context.RefuseFacet<StreamPlan?>("the event frame 'event' must be a string");
        }

        var payload = BindFramePayload(data.Schema);
        var failure = BindEffectStream(success);
        if (payload is null || failure is null)
        {
            return _context.Refused<StreamPlan?>();
        }

        var responseTypeName = OperationNamePolicy.ResponseTypeName(_context.Operation);
        return new FacetResult<StreamPlan?>.Bound(new StreamPlan
        {
            PayloadTypeName = payload,
            AdapterTypeName = $"{responseTypeName}StreamAdapter",
            FailureEventName = failure.EventName,
            CauseTypeName = failure.CauseTypeName,
        });
    }

    /// <summary>The frame's data field is a JSON-encoded string; the stream yields what it encodes.</summary>
    private string? BindFramePayload(SchemaNode schema)
    {
        var node = schema is RefNode reference && _context.Document.Schemas.TryGetValue(reference.Target, out var target)
            ? target
            : schema;
        return node is JsonStringNode { Inner: RefNode inner } && _context.TypeNames.TryGetValue(inner.Target, out var name)
            ? name
            : _context.RefuseNull("the event frame's data must be a JSON-encoded string over a named schema");
    }

    private BoundEffectStream? BindEffectStream(SpecResponse success)
    {
        if (success.EffectStream is null)
        {
            return _context.RefuseNull<BoundEffectStream>("a streaming success must declare 'x-effect-stream'");
        }

        var extension = success.EffectStream;
        if (!string.Equals(extension.Encoding, "sse", StringComparison.Ordinal))
        {
            return _context.RefuseNull<BoundEffectStream>("'x-effect-stream.encoding' must equal 'sse'");
        }

        if (extension.FailureEventName is not { Length: > 0 } name)
        {
            return _context.RefuseNull<BoundEffectStream>("'x-effect-stream' must declare a non-empty 'failureEvent'");
        }

        if (string.Equals(name, "message", StringComparison.Ordinal))
        {
            return _context.RefuseNull<BoundEffectStream>("'x-effect-stream.failureEvent' must not equal 'message'");
        }

        if (extension.ErrorSchema is null || _context.Resolve(extension.ErrorSchema) is not NeverNode)
        {
            return _context.RefuseNull<BoundEffectStream>("'x-effect-stream.errorSchema' must be the never schema 'not: {}'");
        }

        if (extension.CauseSchema is not ArrayNode { Item: RefNode item }
            || !_context.Document.Schemas.TryGetValue(item.Target, out var itemSchema)
            || itemSchema is not UnionNode { Classification: UnionClassification.Marked }
            || !_context.TypeNames.TryGetValue(item.Target, out var itemTypeName))
        {
            return _context.RefuseNull<BoundEffectStream>(
                "'x-effect-stream.causeSchema' must be an array of a named marked union");
        }

        return new BoundEffectStream(name, $"{itemTypeName}[]");
    }

    private sealed record BoundEffectStream(string EventName, string CauseTypeName);
}
