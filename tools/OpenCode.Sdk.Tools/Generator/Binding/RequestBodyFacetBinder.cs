using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Binds the <see cref="OperationPlan.RequestBody"/> facet from the operation's declared body.</summary>
internal sealed class RequestBodyFacetBinder(OperationFacetContext context)
{
    private readonly OperationFacetContext _context = context.CreateLocal();

    public FacetResult<RequestBodyPlan?> Bind()
    {
        var body = _context.Operation.RequestBody;
        if (body is null)
        {
            return new FacetResult<RequestBodyPlan?>.Bound(null);
        }

        if (body.ContentType is { Stripped: "application/octet-stream" }
            && _context.Resolve(body.Schema) is PrimitiveNode { Kind: PrimitiveKind.String, Format: "binary" })
        {
            return BindBinary(body);
        }

        if (body.ContentType is not { IsJson: true })
        {
            _context.Refuse("the request body must carry a JSON schema");
            return _context.Refused<RequestBodyPlan?>();
        }

        if (!body.IsRequired)
        {
            _context.Refuse("the request body must be declared required");
            return _context.Refused<RequestBodyPlan?>();
        }

        if (body.Schema is not RefNode reference || _context.Resolve(body.Schema) is not ObjectNode target)
        {
            _context.Refuse("the request body must reference an object schema");
            return _context.Refused<RequestBodyPlan?>();
        }

        if (!_context.TypeNames.TryGetValue(reference.Target, out var typeName))
        {
            _context.Errors.Add(BindingErrorCategory.Naming, reference.Target, "request body schema has no unique C# type name");
            return _context.Refused<RequestBodyPlan?>();
        }

        // The name resolver claims every selected body root with the operation-derived
        // request name; a mismatch means the ownership map and this binding disagree.
        var expected = OperationNamePolicy.RequestTypeName(_context.Operation);
        if (!string.Equals(typeName, expected, StringComparison.Ordinal))
        {
            _context.Errors.Add(
                BindingErrorCategory.Naming,
                reference.Target,
                $"request body resolved to '{typeName}' instead of the operation-derived '{expected}'");
            return _context.Refused<RequestBodyPlan?>();
        }

        return new FacetResult<RequestBodyPlan?>.Bound(new RequestBodyPlan
        {
            TypeName = typeName,
            ParameterName = ReservedNamePolicy.RequestParameter,
            IsOptional = target.Properties.All(static property => !property.IsRequired),
        });
    }

    /// <summary>
    /// A raw-byte body the document spells as <c>application/octet-stream</c> with a
    /// <c>string</c>/<c>binary</c> schema: the caller's stream, sent as it is (ADR-0029). A body the
    /// document leaves optional would need a bodiless overload nothing on the pin asks for.
    /// </summary>
    private FacetResult<RequestBodyPlan?> BindBinary(SpecRequestBody body)
    {
        if (!body.IsRequired)
        {
            _context.Refuse("the request body must be declared required");
            return _context.Refused<RequestBodyPlan?>();
        }

        return new FacetResult<RequestBodyPlan?>.Bound(new RequestBodyPlan
        {
            TypeName = "Stream",
            ParameterName = ReservedNamePolicy.ContentParameter,
            IsOptional = false,
            IsBinary = true,
        });
    }
}
