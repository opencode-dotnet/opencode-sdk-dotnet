using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Marks the records of the curated event unions whose payload names the session they belong to,
/// so each answers the session-scoped event contract; a row nothing qualifies for is refused.
/// </summary>
internal static class SessionScopedEventBinder
{
    public static IReadOnlyList<ModelPlan> Bind(SessionScopedEventBindingContext context, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(errors);

        var priorErrors = errors.Count;
        SessionScopedEventValidator.Validate(context, errors);
        if (errors.Count != priorErrors)
        {
            return context.Schemas.Models;
        }

        var unions = context.Schemas.Unions.ToDictionary(static union => union.Name, StringComparer.Ordinal);
        var members = context.Row.Unions.SelectMany(name => UnionMemberCollector.Collect(unions[name], unions))
            .ToHashSet(StringComparer.Ordinal);
        var policy = new SessionScopedEventProjectionPolicy(context);
        var models = new List<ModelPlan>(context.Schemas.Models.Count);
        var qualified = false;
        foreach (var model in context.Schemas.Models)
        {
            if (model is ObjectModelPlan objectModel && members.Contains(model.Name) && policy.Project(objectModel, errors) is { } projection)
            {
                models.Add(objectModel with { SessionScopedEvent = projection });
                qualified = true;
            }
            else
            {
                models.Add(model);
            }
        }

        if (!qualified)
        {
            errors.Add(BindingErrorCategory.Curation, "sessionScopedEvent", "no event qualifies for the session-scoped event curation");
        }

        return Array.AsReadOnly([.. models]);
    }
}
