using System.Globalization;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The fail-closed wire-shape wall: HTTP method, path, body presence, parameter and status
/// shapes are refused up front so every facet binder reads a known-good operation. The
/// owning group's emission mode decides which header parameters the wall admits.
/// </summary>
internal sealed class OperationWireShapeWall(OperationFacetContext context, EmissionMode emission)
{
    private readonly OperationFacetContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly EmissionMode _emission = emission;

    public bool Check()
    {
        var before = _context.Errors.Count;
        var operation = _context.Operation;
        var isGet = string.Equals(operation.Method, "get", StringComparison.Ordinal);
        var isPost = string.Equals(operation.Method, "post", StringComparison.Ordinal);
        var isDelete = string.Equals(operation.Method, "delete", StringComparison.Ordinal);
        var isPatch = string.Equals(operation.Method, "patch", StringComparison.Ordinal);
        var isPut = string.Equals(operation.Method, "put", StringComparison.Ordinal);
        if (!isGet && !isPost && !isDelete && !isPatch && !isPut)
        {
            _context.Refuse($"HTTP method '{operation.Method}' is not supported");
        }

        if (operation.IsWebSocket)
        {
            _context.Refuse("WebSocket operations are not supported");
        }

        // A GET body has no defined semantics on any origin server and stays refused. DELETE is
        // different: RFC 9110 admits content when the origin server declares support, and this
        // origin server declares it in the pinned document, so the shape binds and the body is
        // sent. Any method the wall does not admit at all was already refused above.
        if (isGet && operation.RequestBody is not null)
        {
            _context.Refuse($"{operation.Method.ToUpperInvariant()} operations must not carry a request body");
        }

        // The pin demonstrates bodyless POST across twelve operations, so POST admits both
        // shapes; DELETE admits both for the same reason; PATCH and PUT keep the body
        // requirement until the pin shows otherwise.
        if ((isPatch || isPut) && operation.RequestBody is null)
        {
            _context.Refuse($"{operation.Method.ToUpperInvariant()} operations must carry a request body");
        }

        CheckParameterShapes();
        CheckStatusShape();
        return _context.Errors.Count == before;
    }

    private void CheckParameterShapes()
    {
        foreach (var parameter in _context.Operation.Parameters.Where(static parameter => parameter.Location is SpecParameterLocation.Header))
        {
            CheckHeaderShape(parameter);
        }

        foreach (var parameter in _context.Operation.Parameters.Where(static parameter => parameter.Location is SpecParameterLocation.Path))
        {
            if (parameter is not { IsRequired: true, IsDeepObject: false })
            {
                _context.Refuse($"path parameter '{parameter.Name}' must be required and plainly encoded");
                continue;
            }

            if (_context.Resolve(parameter.Schema) is not PrimitiveNode { Kind: PrimitiveKind.String })
            {
                _context.Refuse($"path parameter '{parameter.Name}' must be a plain string");
            }
        }
    }

    /// <summary>
    /// A header value is never curated and never emitted (ADR-0013), so only an internal-raw
    /// family — whose hand-written door supplies the value — can own one; a public family
    /// keeps the refusal. The admitted shape is the optional plain string the emitted
    /// signature carries, so an unrepresentable header fails here rather than silently
    /// emitting an omittable required header.
    /// </summary>
    private void CheckHeaderShape(SpecParameter parameter)
    {
        if (_emission is not EmissionMode.InternalRaw)
        {
            _context.Refuse($"header parameter '{parameter.Name}' has no runtime channel");
            return;
        }

        if (parameter is not { IsRequired: false, IsDeepObject: false }
            || _context.Resolve(parameter.Schema) is not NullableNode { Format: null } nullable
            || _context.Resolve(nullable.Inner) is not PrimitiveNode { Kind: PrimitiveKind.String, Format: null })
        {
            _context.Refuse($"header parameter '{parameter.Name}' must be optional, not deep-object, and declare a nullable string schema");
        }
    }

    private void CheckStatusShape()
    {
        var successes = _context.Operation.Responses.Where(static response => response.StatusCode is >= 200 and < 300).ToArray();
        if (successes.Length is not 1)
        {
            _context.Refuse("operation must declare exactly one success response");
        }
        else if (successes[0].StatusCode is not (200 or 204))
        {
            _context.Refuse("the success response must use status 200 or a content-free 204");
        }

        foreach (var response in _context.Operation.Responses
                     .Where(static response => response.StatusCode is < 200 or (>= 300 and < 400)))
        {
            _context.Refuse($"status '{response.StatusCode.ToString(CultureInfo.InvariantCulture)}' must be an error status");
        }
    }
}
