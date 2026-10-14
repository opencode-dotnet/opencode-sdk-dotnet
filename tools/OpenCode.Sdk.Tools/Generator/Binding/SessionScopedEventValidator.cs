using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The session-scoped event row names schemas, unions and members the document must have; every
/// mismatch refuses generation. The interface's name needs no check here: it is reserved, so no
/// generated type can take it.
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
    }
}
