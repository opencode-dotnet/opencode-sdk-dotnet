using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The session-scoped event row names schemas, unions and members the document must have, and an
/// interface that no generated type may already be called; every mismatch refuses generation.
/// </summary>
internal static class SessionScopedEventValidator
{
    public static void Validate(SessionScopedEventBindingContext context, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(errors);

        var row = context.Row;
        if (string.IsNullOrWhiteSpace(row.Reason))
        {
            errors.Add(BindingErrorCategory.Curation, "sessionScopedEvent", "session-scoped event curation must declare a reason");
        }

        if (!CSharpNamePolicy.IsValidIdentifier(row.Interface))
        {
            errors.Add(BindingErrorCategory.Naming, "sessionScopedEvent", $"interface name '{row.Interface}' is not a valid C# identifier");
        }

        if (!CSharpNamePolicy.IsValidIdentifier(row.InterfaceMember))
        {
            errors.Add(BindingErrorCategory.Naming, "sessionScopedEvent", $"interface member '{row.InterfaceMember}' is not a valid C# identifier");
        }

        if (string.IsNullOrWhiteSpace(row.Identifier) || !context.Document.Schemas.TryGetValue(row.Identifier, out var identifier))
        {
            errors.Add(BindingErrorCategory.Curation, "sessionScopedEvent", $"identifier schema does not exist in the document: '{row.Identifier}'");
        }
        else if (identifier is not PrimitiveNode { Kind: PrimitiveKind.String })
        {
            errors.Add(BindingErrorCategory.Curation, "sessionScopedEvent", $"identifier schema '{row.Identifier}' must be a non-nullable string");
        }

        if (row.Unions.Count is 0)
        {
            errors.Add(BindingErrorCategory.Curation, "sessionScopedEvent", "session-scoped event curation must name at least one event union");
        }

        foreach (var name in row.Unions.Where(name => !context.Schemas.Unions.Any(union => string.Equals(union.Name, name, StringComparison.Ordinal))))
        {
            errors.Add(BindingErrorCategory.Curation, "sessionScopedEvent", $"event union '{name}' is not a generated marked union");
        }

        if (string.IsNullOrWhiteSpace(row.PayloadMember) || string.IsNullOrWhiteSpace(row.Member))
        {
            errors.Add(BindingErrorCategory.Curation, "sessionScopedEvent", "session-scoped event member names cannot be blank");
        }

        var operations = context.Clients.SelectMany(static client => client.Operations).ToArray();
        var names = context.Schemas.Models.Select(static model => model.Name)
            .Concat(context.Schemas.Models.OfType<EnumModelPlan>().Select(static model => model.Name + "JsonConverter"))
            .Concat(context.Schemas.Models.OfType<StructuralUnionModelPlan>().Select(static model => model.KindTypeName))
            .Concat(context.Schemas.Models.OfType<StructuralUnionModelPlan>().Select(static model => model.Name + "JsonConverter"))
            .Concat(context.Schemas.Unions.SelectMany(static union => new[]
            {
                union.Name, union.UnknownTypeName, union.ConceptName + "JsonConverter", union.UnknownTypeName + "JsonConverter",
            }))
            .Concat(context.Schemas.HoistedInterfaces.Select(static hoisted => hoisted.Name))
            .Concat(context.Clients.Select(static client => client.Name))
            .Concat(operations.Select(static operation => operation.QueryRequest?.TypeName))
            .Concat(operations.Select(static operation => operation.Envelope?.ResponseTypeName))
            .Concat(operations.Select(static operation => operation.Envelope?.AdapterTypeName))
            .Concat(operations.Select(static operation => operation.Envelope?.EnvelopeDtoTypeName))
            .Concat(operations.Select(static operation => operation.Stream?.AdapterTypeName))
            .OfType<string>();
        if (names.Contains(row.Interface, StringComparer.Ordinal))
        {
            errors.Add(BindingErrorCategory.Naming, "sessionScopedEvent", $"interface name '{row.Interface}' collides with a generated type");
        }
    }
}
