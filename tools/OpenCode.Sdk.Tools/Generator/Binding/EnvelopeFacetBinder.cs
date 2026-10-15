using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Binds the <see cref="OperationPlan.Envelope"/> facet of a non-streaming success.</summary>
internal sealed class EnvelopeFacetBinder(OperationFacetContext context)
{
    private readonly OperationFacetContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public EnvelopePlan? Bind(SpecResponse success)
    {
        ArgumentNullException.ThrowIfNull(success);

        if (success.StatusCode is 204)
        {
            return BindNoContentEnvelope(success);
        }

        if (IsBinary(success))
        {
            return BindBinaryEnvelope();
        }

        if (success.ContentType is not { IsJson: true } || success.Schema is null)
        {
            _context.Refuse("the success response must carry a JSON schema");
            return null;
        }

        // Read once and threaded through: SingleKeyMember refuses a malformed wrapper by name, so
        // resolving it twice would run that refusal twice for one operation.
        var singleKeyMember = success.EnvelopeShape is SpecEnvelopeShape.SingleKey
            ? SingleKeyMember(success.Schema)
            : null;
        var payload = BindPayload(success, singleKeyMember);
        var locationTypeName = success.EnvelopeShape is SpecEnvelopeShape.DataLocation
            ? BindLocationSibling(success.Schema)
            : null;
        if (success.EnvelopeShape is SpecEnvelopeShape.DataLocation && locationTypeName is null)
        {
            return null;
        }

        if (payload is null)
        {
            return null;
        }

        // A single-key body wears its payload's own key; every other wrapped shape uses the
        // dialect's 'data'. The payload binder already refused a malformed wrapper, so the key
        // is present by the time this reads it.
        var wireMemberName = success.EnvelopeShape is SpecEnvelopeShape.SingleKey
            ? singleKeyMember?.Name
            : "data";
        if (wireMemberName is null)
        {
            return null;
        }

        var responseTypeName = OperationNamePolicy.ResponseTypeName(_context.Operation);
        var payloadName = DerivePayloadName(
            responseTypeName,
            success.EnvelopeShape is SpecEnvelopeShape.SingleKey
                ? CSharpNamePolicy.ToPascalCase(wireMemberName)
                : OperationNamePolicy.PayloadName(_context.Operation));
        if (payloadName is null)
        {
            return null;
        }

        var kind = KindOf(success);
        return new EnvelopePlan
        {
            ResponseTypeName = responseTypeName,
            AdapterTypeName = $"{responseTypeName}Adapter",
            PayloadName = payloadName,
            PayloadType = payload,
            Kind = kind,
            WireMemberName = wireMemberName,
            SuccessStatusCode = 200,
            EnvelopeDtoTypeName = kind is EnvelopeKind.Bare ? null : $"{responseTypeName}Envelope",
            LocationTypeName = locationTypeName,
            Documentation = new TypeDocumentationPlan
            {
                Summary = kind is EnvelopeKind.Bare ? null : success.Schema.Description ?? _context.Resolve(success.Schema).Description,
            },
        };
    }

    private EnvelopeKind KindOf(SpecResponse success) => success.EnvelopeShape switch
    {
        // A single-key body reads exactly like a data wrapper once the DTO carries its key.
        SpecEnvelopeShape.Data or SpecEnvelopeShape.SingleKey => EnvelopeKind.Data,
        SpecEnvelopeShape.CursorData => EnvelopeKind.CursorList,
        SpecEnvelopeShape.DataLocation => DataLocationKind(success.Schema!),
        SpecEnvelopeShape.Bare or SpecEnvelopeShape.None
            or SpecEnvelopeShape.DataHasMore or _ => EnvelopeKind.Bare,
    };

    /// <summary>
    /// An <c>application/octet-stream</c> success whose schema is <c>string</c> with
    /// <c>format: binary</c>: the document's own spelling of raw bytes, the only binary vocabulary
    /// the binder admits (ADR-0029).
    /// </summary>
    private bool IsBinary(SpecResponse success) =>
        success.ContentType is { Stripped: "application/octet-stream" }
        && success.Schema is not null
        && _context.Resolve(success.Schema) is PrimitiveNode { Kind: PrimitiveKind.String, Format: "binary" };

    /// <summary>
    /// The envelope of a raw-byte success: the bytes as <c>Content</c> and the server's
    /// <c>Content-Type</c> beside them; nothing reaches the serializer, and error statuses keep
    /// their JSON bodies.
    /// </summary>
    private EnvelopePlan BindBinaryEnvelope()
    {
        var responseTypeName = OperationNamePolicy.ResponseTypeName(_context.Operation);
        return new EnvelopePlan
        {
            ResponseTypeName = responseTypeName,
            AdapterTypeName = $"{responseTypeName}Adapter",
            PayloadName = "Content",
            PayloadType = new BinaryTypeReferencePlan
            {
                IsNullable = false,
                JsonNullRepresentation = JsonNullRepresentation.ClrNull,
            },
            Kind = EnvelopeKind.Binary,
            SuccessStatusCode = 200,
        };
    }

    /// <summary>Binds the payload the classified envelope shape carries.</summary>
    private TypeReferencePlan? BindPayload(SpecResponse success, SpecProperty? singleKeyMember) =>
        success.EnvelopeShape switch
        {
            SpecEnvelopeShape.Bare => BindBarePayload(success.Schema!),
            SpecEnvelopeShape.Data => BindDataPayload(success.Schema!),
            SpecEnvelopeShape.SingleKey => BindSingleKeyPayload(success.Schema!, singleKeyMember),
            SpecEnvelopeShape.CursorData => BindCursorListPayload(success.Schema!),
            SpecEnvelopeShape.DataLocation => BindDataLocationPayload(success.Schema!),
            SpecEnvelopeShape.None or SpecEnvelopeShape.DataHasMore or _ =>
                _context.RefuseNull<TypeReferencePlan>($"envelope shape '{success.EnvelopeShape}' is not supported"),
        };

    private static NamedTypeReferencePlan Named(string name) => new()
    {
        Name = name,
        IsNullable = false,
        JsonNullRepresentation = JsonNullRepresentation.ClrNull,
    };

    private static ListTypeReferencePlan ListOf(TypeReferencePlan element) => new()
    {
        ElementType = element,
        IsNullable = false,
        JsonNullRepresentation = JsonNullRepresentation.ClrNull,
    };

    private string? DerivePayloadName(string responseTypeName, string? mechanicalName)
    {
        var payloadName = _context.Curation.EnvelopePayloadNames.TryGetValue(_context.Operation.OperationId, out var curated)
            ? curated
            : mechanicalName;
        if (payloadName is null)
        {
            _context.Refuse("the payload name cannot be derived mechanically: the group does not pluralize naively; curate an envelope payload name");
            return null;
        }

        // Mechanical names are PascalCase by construction, but the wall covers both origins.
        if (!CSharpNamePolicy.IsValidIdentifier(payloadName))
        {
            _context.Errors.Add(
                BindingErrorCategory.Naming,
                _context.Operation.OperationId,
                $"payload name '{payloadName}' is not a valid C# identifier");
            return null;
        }

        if (ReservedNamePolicy.PayloadNames.Contains(payloadName)
            || string.Equals(payloadName, responseTypeName, StringComparison.Ordinal))
        {
            _context.Errors.Add(
                BindingErrorCategory.Naming,
                _context.Operation.OperationId,
                $"payload name '{payloadName}' collides with the response spine of '{responseTypeName}'");
            return null;
        }

        return payloadName;
    }

    private EnvelopePlan? BindNoContentEnvelope(SpecResponse success)
    {
        if (success.ContentType is not null || success.Schema is not null)
        {
            _context.Refuse("a 204 success must not carry content");
            return null;
        }

        var responseTypeName = OperationNamePolicy.ResponseTypeName(_context.Operation);
        return new EnvelopePlan
        {
            ResponseTypeName = responseTypeName,
            AdapterTypeName = $"{responseTypeName}Adapter",
            PayloadName = null,
            PayloadType = null,
            Kind = EnvelopeKind.NoContent,
            SuccessStatusCode = 204,
            EnvelopeDtoTypeName = null,
        };
    }

    private TypeReferencePlan? BindBarePayload(SchemaNode schema)
    {
        if (schema is RefNode reference && _context.TypeNames.TryGetValue(reference.Target, out var name))
        {
            return Named(name);
        }

        var bound = _context.Types.Bind(_context.Operation.OperationId, "payload", schema);
        if (bound is null)
        {
            return _context.RefuseNull<TypeReferencePlan>("success payload does not bind to a supported type plan");
        }

        // A bare body has no wrapper to carry a null/absent distinction and no registered
        // context accessor for a nullable root (SerializerTypeNamePolicy.ContextPropertyName
        // throws on one); unlike a Data/DataLocation wrapper's 'data' member, nullable stays
        // refused here rather than resurfacing as a late generation-time throw.
        return bound.IsNullable
            ? _context.RefuseNull<TypeReferencePlan>("a bare success body cannot represent null")
            : bound;
    }

    private TypeReferencePlan? BindDataPayload(SchemaNode schema)
    {
        if (schema is not RefNode reference
            || !_context.Document.Schemas.TryGetValue(reference.Target, out var target)
            || target is not ObjectNode { Properties: [{ Name: "data", IsRequired: true } data] })
        {
            return _context.RefuseNull<TypeReferencePlan>("envelope payload must be a required reference to a named schema");
        }

        if (data.Schema is RefNode payload && _context.TypeNames.TryGetValue(payload.Target, out var name))
        {
            return Named(name);
        }

        return _context.Types.Bind(reference.Target, "data", data.Schema)
               ?? _context.RefuseNull<TypeReferencePlan>("success payload does not bind to a supported type plan");
    }

    /// <summary>
    /// Reads the sole required member of a single-key wrapper — the key the payload arrives
    /// under. Ingestion only classifies an operation-declared inline object this way, so a
    /// component the dialect names keeps its own identity and never reaches here. Requiredness is
    /// this binder's wall, not the classifier's: <c>EnvelopeClassifier</c> admits any inline
    /// object with exactly one property, because a key's name is all it can see, and a wrapper
    /// whose sole property is optional has no payload the envelope can promise. It is refused
    /// here by name rather than silently reclassified as a bare body.
    /// </summary>
    private SpecProperty? SingleKeyMember(SchemaNode schema) =>
        schema is RefNode reference
        && _context.Document.Schemas.TryGetValue(reference.Target, out var target)
        && target is ObjectNode { Properties: [{ IsRequired: true } member] }
            ? member
            : _context.RefuseNull<SpecProperty>("single-key envelope must reference an object requiring exactly one property");

    private TypeReferencePlan? BindSingleKeyPayload(SchemaNode schema, SpecProperty? singleKeyMember)
    {
        if (singleKeyMember is not { } member || schema is not RefNode reference)
        {
            return null;
        }

        // The value is the payload itself: a named component keeps its identity, and every
        // other shape — a represented-nullable reference, a list of primitives — reaches the
        // type machinery exactly as a 'data' member does.
        if (member.Schema is RefNode payload && _context.TypeNames.TryGetValue(payload.Target, out var name))
        {
            return Named(name);
        }

        return _context.Types.Bind(reference.Target, member.Name, member.Schema)
               ?? _context.RefuseNull<TypeReferencePlan>("success payload does not bind to a supported type plan");
    }

    private ListTypeReferencePlan? BindCursorListPayload(SchemaNode schema)
    {
        if (schema is not RefNode reference
            || !_context.Document.Schemas.TryGetValue(reference.Target, out var target)
            || target is not ObjectNode wrapper)
        {
            return _context.RefuseNull<ListTypeReferencePlan>("cursor-list envelope must reference an object schema");
        }

        var data = wrapper.Properties.FirstOrDefault(static property => property.Name is "data");
        var cursor = wrapper.Properties.FirstOrDefault(static property => property.Name is "cursor");
        if (wrapper.Properties.Count is not 2 || data is not { IsRequired: true } || cursor is not { IsRequired: true })
        {
            return _context.RefuseNull<ListTypeReferencePlan>("cursor-list envelope must require exactly 'data' and 'cursor'");
        }

        if (!SpineShapePolicy.IsListCursorShape(_context, cursor.Schema))
        {
            return _context.RefuseNull<ListTypeReferencePlan>(
                "cursor-list 'cursor' must be the optional-nullable previous/next cursor object");
        }

        // Items must reference top-level components: a promoted inline item would take its
        // name from the excluded response root, so the dialect keeps list items nominal.
        if (data.Schema is not ArrayNode { Item: RefNode item }
            || item.Target.Contains('#', StringComparison.Ordinal)
            || !_context.TypeNames.TryGetValue(item.Target, out var itemName))
        {
            return _context.RefuseNull<ListTypeReferencePlan>("cursor-list 'data' must be an array of a named component schema");
        }

        return ListOf(Named(itemName));
    }

    private TypeReferencePlan? BindDataLocationPayload(SchemaNode schema)
    {
        var wrapper = ResolveDataLocationWrapper(schema);
        if (wrapper is null)
        {
            return null;
        }

        // A named top-level component payload keeps its own identity. An inline object
        // ingestion promoted into the graph reaches the same lookup, because
        // SchemaNameResolver's DataLocation arm claims that promoted key under the
        // operation-scoped payload name. What makes that reliable is the shared shape check both
        // sides run (EnvelopeWrapperShape.IsDataLocation), not the arm order below: a RefNode the
        // resolver named neither way reaches one of the arms that follow, each of which either
        // recognizes a shape of its own or refuses — no resurrection via the type machinery's
        // mechanical fallback name.
        var data = wrapper.Properties.Single(static property => property.Name is "data");
        if (data.Schema is RefNode datum && _context.TypeNames.TryGetValue(datum.Target, out var datumName))
        {
            return Named(datumName);
        }

        // The same claim covers a promoted inline list item, so a named component item and a
        // promoted one share this lookup too.
        if (data.Schema is ArrayNode { Item: RefNode item }
            && _context.TypeNames.TryGetValue(item.Target, out var itemName))
        {
            return ListOf(Named(itemName));
        }

        // 'data' may also be a RefNode naming an ARRAY component directly — vcs.branches' exact
        // shape, Vcs.BranchList = {"type":"array","items":{"type":"string"}} — rather than
        // wrapping the array inline at this position. Resolve the reference through the ref
        // graph and, only when the resolved target is itself an array, apply the same item
        // logic as the arm above: a named item keeps its own identity, and every other item
        // shape (here, a primitive string) falls through to the type machinery, which walks the
        // very same reference (RefNode -> ArrayNode -> item) on its own. This does not widen the
        // guard below: the guard exists to stop a RefNode resolving to a NOMINAL
        // (object/enum/union) target with a failed name lookup from resurrecting a
        // mechanically-derived name, and that refusal is unchanged — only a ref resolving to an
        // ARRAY gains this path, keyed on shape (ref -> array), never on operation id. Resolve
        // only follows RefNode chains — it does not unwrap a NullableNode, and its own visited
        // set stops a ref alias cycle by handing back the cycling RefNode — so a nullable-wrapped
        // array or a cycling ref falls through this arm unmatched and reaches the same guard,
        // refusing exactly as before; intentional, since this dialect has no NullableNode-
        // unwrapping step of its own yet.
        if (data.Schema is RefNode arrayReference && _context.Resolve(arrayReference) is ArrayNode resolvedArray)
        {
            if (resolvedArray.Item is RefNode resolvedItem
                && _context.TypeNames.TryGetValue(resolvedItem.Target, out var resolvedItemName))
            {
                return ListOf(Named(resolvedItemName));
            }

            return _context.Types.Bind(_context.Operation.OperationId, "data", data.Schema)
                   ?? _context.RefuseNull<TypeReferencePlan>("success payload does not bind to a supported type plan");
        }

        // A RefNode or ArrayNode that reached here already failed the name lookups above (a
        // target the resolver left unnamed — a collapsed structural union, or a schema the
        // dialect excludes from naming — or a ref that does not resolve to an array); the
        // dialect keeps refusing those exactly as before instead of letting the type machinery
        // resurrect a mechanically-derived name. Every other shape (a dictionary, for one)
        // delegates to the type machinery.
        if (data.Schema is RefNode or ArrayNode)
        {
            return _context.RefuseNull<TypeReferencePlan>(
                "location envelope 'data' must reference a named component schema, or be an array of one");
        }

        return _context.Types.Bind(_context.Operation.OperationId, "data", data.Schema)
               ?? _context.RefuseNull<TypeReferencePlan>("success payload does not bind to a supported type plan");
    }

    /// <summary>
    /// The payload binder owns the wrapper walls; the sibling and kind readers resolve
    /// leniently because a malformed wrapper is already refused once.
    /// </summary>
    private ObjectNode? ResolveDataLocationWrapper(SchemaNode schema)
    {
        if (schema is not RefNode reference
            || !_context.Document.Schemas.TryGetValue(reference.Target, out var target)
            || target is not ObjectNode wrapper)
        {
            return _context.RefuseNull<ObjectNode>("location envelope must reference an object schema");
        }

        return EnvelopeWrapperShape.IsDataLocation(wrapper, out _)
            ? wrapper
            : _context.RefuseNull<ObjectNode>("location envelope must require exactly 'data' and 'location'");
    }

    private string? BindLocationSibling(SchemaNode schema)
    {
        if (schema is not RefNode reference
            || !_context.Document.Schemas.TryGetValue(reference.Target, out var target)
            || target is not ObjectNode wrapper
            || wrapper.Properties.FirstOrDefault(static property => property.Name is "location") is not { IsRequired: true } location)
        {
            return null;
        }

        // A promoted inline sibling would take its name from the excluded response root,
        // so the dialect keeps the location echo nominal.
        return location.Schema is RefNode sibling
               && !sibling.Target.Contains('#', StringComparison.Ordinal)
               && _context.TypeNames.TryGetValue(sibling.Target, out var name)
            ? name
            : _context.RefuseNull("the location sibling must reference a named component schema");
    }

    private EnvelopeKind DataLocationKind(SchemaNode schema) =>
        schema is RefNode reference
        && _context.Document.Schemas.TryGetValue(reference.Target, out var target)
        && target is ObjectNode wrapper
        && wrapper.Properties.FirstOrDefault(static property => property.Name is "data") is { } data
        // A literal inline array resolves to itself; a RefNode naming an array component (the
        // vcs.branches shape) resolves through the ref graph to the same ArrayNode — one check
        // classifies both.
        && _context.Resolve(data.Schema) is ArrayNode
            ? EnvelopeKind.DataLocationList
            : EnvelopeKind.DataLocation;
}
