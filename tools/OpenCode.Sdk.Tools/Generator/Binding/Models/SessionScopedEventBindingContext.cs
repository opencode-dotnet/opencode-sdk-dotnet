using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>The normalized graph and bound surface a session-scoped event rule is judged against.</summary>
internal sealed record SessionScopedEventBindingContext
{
    public required SpecDocument Document { get; init; }

    public required IReadOnlyDictionary<string, string> TypeNames { get; init; }

    public required SchemaBindingResult Schemas { get; init; }

    public required IReadOnlyList<ClientPlan> Clients { get; init; }

    public required SessionScopedEventCuration Row { get; init; }
}
