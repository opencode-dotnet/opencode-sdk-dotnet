using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static class ReachableSchemaCollector
{
    public static ReachableSchemaSet Collect(SpecDocument document, IReadOnlyList<SpecOperation> operations, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(errors);

        var traversal = new ReachabilityTraversal(document.Schemas, errors);
        foreach (var operation in operations)
        {
            foreach (var parameter in operation.Parameters)
            {
                if (parameter.Location is SpecParameterLocation.Path)
                {
                    traversal.Visit(parameter.Schema);
                    continue;
                }

                // Query parameter schemas otherwise generate no models: the query binder
                // consumes them shape-wise and fails closed on anything it does not
                // recognize. The exception is an enum outside the spine profiles, which the
                // request property is typed with and therefore needs as a public model.
                if (parameter.Location is SpecParameterLocation.Query
                    && QueryEnumShapePolicy.ResolveModelKey(parameter.Schema, document.Schemas) is { } enumKey)
                {
                    traversal.VisitKey(enumKey);
                }
            }

            if (operation.RequestBody is not null)
            {
                traversal.Visit(operation.RequestBody.Schema);
            }

            foreach (var response in operation.Responses)
            {
                traversal.VisitResponse(operation.OperationId, response);
            }
        }

        return traversal.Snapshot() with
        {
            RequestReachableKeys = new RequestReachableSchemaWalker(document.Schemas).Collect(operations),
        };
    }

    private sealed class ReachabilityTraversal(IReadOnlyDictionary<string, SchemaNode> graph, BindingErrorCollector errors)
    {
        private readonly BindingErrorCollector _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        private readonly IReadOnlyDictionary<string, SchemaNode> _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _responseRoots = new(StringComparer.Ordinal);
        private readonly HashSet<string> _streamCauseKeys = new(StringComparer.Ordinal);

        public void VisitResponse(string operationId, SpecResponse response)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
            ArgumentNullException.ThrowIfNull(response);

            var schema = response.Schema;
            if (schema is RefNode reference)
            {
                // Data-carrying wrappers are envelope structure, never models — component-named
                // wrappers join the op-inline roots in the excluded set.
                var isInlineRoot = reference.Target.StartsWith($"op:{operationId}#", StringComparison.Ordinal);
                var isWrapperShape = response.EnvelopeShape
                    is SpecEnvelopeShape.Data or SpecEnvelopeShape.CursorData or SpecEnvelopeShape.DataLocation
                        or SpecEnvelopeShape.SingleKey;

                // A bare success body is the payload itself, so its promoted inline root is a
                // model like any other — SchemaNameResolver names it from the operation rather
                // than from the excluded root's own spelling.
                var isPayloadRoot = !response.IsSse
                                    && response.StatusCode is 200
                                    && response.EnvelopeShape is SpecEnvelopeShape.Bare;

                // An event frame is framing the engine consumes, never a model: the stream
                // yields what the frame's data encodes, not the frame.
                if ((isInlineRoot && !isPayloadRoot) || isWrapperShape || response.IsSse)
                {
                    _ = _responseRoots.Add(reference.Target);
                }

                if (response.IsSse)
                {
                    ExcludeFrameDataString(reference.Target);
                }

                if (response.EnvelopeShape is SpecEnvelopeShape.CursorData)
                {
                    ExcludeCursorObject(reference.Target);
                }
            }

            if (schema is not null)
            {
                Visit(schema);
            }

            if (response.EffectStream?.CauseSchema is { } causeSchema)
            {
                Visit(causeSchema, isStreamCause: true);
            }

            if (response.EffectStream?.ErrorSchema is { } errorSchema)
            {
                Visit(errorSchema);
            }
        }

        /// <summary>
        /// The frame's data is a JSON-encoded string wrapper; the payload it encodes is the model,
        /// and the wrapper itself has no C# shape.
        /// </summary>
        private void ExcludeFrameDataString(string frameKey)
        {
            if (!_graph.TryGetValue(frameKey, out var frameSchema) || frameSchema is not ObjectNode frame)
            {
                return;
            }

            foreach (var property in frame.Properties)
            {
                if (property is { Name: "data", Schema: RefNode dataReference })
                {
                    _ = _responseRoots.Add(dataReference.Target);
                }
            }
        }

        /// <summary>The cursor object rides the envelope contract (hand-written spine), never the model closure.</summary>
        private void ExcludeCursorObject(string wrapperKey)
        {
            if (!_graph.TryGetValue(wrapperKey, out var wrapperSchema) || wrapperSchema is not ObjectNode wrapper)
            {
                return;
            }

            foreach (var property in wrapper.Properties)
            {
                if (property is { Name: "cursor", Schema: RefNode cursorReference })
                {
                    _ = _responseRoots.Add(cursorReference.Target);
                }
            }
        }

        public void Visit(SchemaNode schema)
        {
            Visit(schema, isStreamCause: false);
        }

        /// <summary>Reaches a schema the caller resolved to a graph key rather than to a node.</summary>
        public void VisitKey(string target)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(target);
            VisitReference(target, isStreamCause: false);
        }

        private void Visit(SchemaNode schema, bool isStreamCause)
        {
            ArgumentNullException.ThrowIfNull(schema);
            if (schema is RefNode reference)
            {
                VisitReference(reference.Target, isStreamCause);
                return;
            }

            // A structural union collapsed to one semantic value has no nominal branch
            // models. Traversing its promoted refinement branches would emit dead public types.
            if (schema is UnionNode { Classification: UnionClassification.Structural } union
                && UnstructuredUnionPolicy.Collapse(union, _graph) is not null)
            {
                return;
            }

            foreach (var child in schema.Children)
            {
                Visit(child, isStreamCause);
            }
        }

        public ReachableSchemaSet Snapshot() =>
            new()
            {
                GraphKeys = [.. _keys.Order(StringComparer.Ordinal)],
                ResponseRootKeys = [.. _responseRoots.Order(StringComparer.Ordinal)],
                StreamCauseKeys = [.. _streamCauseKeys.Order(StringComparer.Ordinal)],
            };

        private void VisitReference(string target, bool isStreamCause)
        {
            if (isStreamCause)
            {
                _ = _streamCauseKeys.Add(target);
            }

            if (!_keys.Add(target))
            {
                return;
            }

            if (!_graph.TryGetValue(target, out var targetSchema))
            {
                _errors.Add(BindingErrorCategory.Schema, target, "referenced schema is absent from the ingested graph");
                return;
            }

            Visit(targetSchema, isStreamCause);
        }
    }
}
