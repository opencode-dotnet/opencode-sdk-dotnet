using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Recognizes the required top-level identifier by schema identity, not by its CLR string representation.</summary>
internal sealed class SessionScopedEventProjectionPolicy
{
    private readonly SessionScopedEventBindingContext _context;
    private readonly Dictionary<string, ObjectNode> _nodes;
    private readonly Dictionary<string, ObjectModelPlan> _models;

    public SessionScopedEventProjectionPolicy(SessionScopedEventBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _nodes = context.TypeNames
            .Where(pair => context.Document.Schemas[pair.Key] is ObjectNode)
            .ToDictionary(static pair => pair.Value, pair => (ObjectNode)context.Document.Schemas[pair.Key], StringComparer.Ordinal);
        _models = context.Schemas.Models.OfType<ObjectModelPlan>().ToDictionary(static model => model.Name, StringComparer.Ordinal);
    }

    public SessionScopedEventPlan? Project(ObjectModelPlan model, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(errors);
        var row = _context.Row;
        if (model.Properties.FirstOrDefault(property => string.Equals(property.WireName, row.PayloadMember, StringComparison.Ordinal))
                is not { Type: NamedTypeReferencePlan payloadType } ownPayload
            || !_nodes.TryGetValue(payloadType.Name, out var payload)
            || !_models.TryGetValue(payloadType.Name, out var payloadModel)
            || payload.Properties.FirstOrDefault(property => string.Equals(property.Name, row.Member, StringComparison.Ordinal))
                is not { IsRequired: true, Schema: RefNode identifierReference }
            || !string.Equals(identifierReference.Target, row.Identifier, StringComparison.Ordinal))
        {
            return null;
        }

        if (!ownPayload.IsRequired || payloadType.IsNullable)
        {
            errors.Add(BindingErrorCategory.Schema, model.Name, "a session-scoped event payload must be required and non-nullable");
            return null;
        }

        var ownIdentifier = payloadModel.Properties.Single(property => string.Equals(property.WireName, row.Member, StringComparison.Ordinal));
        if (ownIdentifier.Type is not NamedTypeReferencePlan { Name: "string", IsNullable: false })
        {
            errors.Add(BindingErrorCategory.Schema, model.Name, "a session-scoped event identifier must bind to a non-nullable string");
            return null;
        }

        return new SessionScopedEventPlan
        {
            InterfaceName = row.Interface,
            MemberName = row.InterfaceMember,
            PayloadPropertyName = ownPayload.Name,
            IdentifierPropertyName = ownIdentifier.Name,
        };
    }
}
