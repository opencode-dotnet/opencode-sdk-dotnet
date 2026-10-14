using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Documents operation-owned records from their bound identity, method and route.</summary>
internal static class OperationDocumentationPolicy
{
    public static string Summary(string role, OperationPlan operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(operation);
        return $"The {role} of '{operation.OperationId}' ({operation.HttpMethod.ToUpperInvariant()} {operation.RouteTemplate}).";
    }

    public static IReadOnlyList<ClientPlan> Apply(IReadOnlyList<ClientPlan> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        return [.. clients.Select(client => client with
        {
            Operations = [.. client.Operations.Select(operation => operation with
            {
                Envelope = operation.Envelope is { } envelope ? envelope with
                {
                    Documentation = envelope.Documentation with { Summary = envelope.Documentation.Summary ?? Summary("response", operation) },
                } : null,
                QueryRequest = operation.QueryRequest is { RidesRequestBody: false } query ? query with
                {
                    Documentation = query.Documentation with
                    {
                        Summary = Summary("query", operation),
                        Remarks = InheritedQueryDescriptions(query),
                    },
                } : operation.QueryRequest,
            })],
        })];
    }

    private static string? InheritedQueryDescriptions(QueryRequestPlan query)
    {
        var descriptions = query.Properties.Where(static property => property.IsInherited && !string.IsNullOrWhiteSpace(property.Description))
            .Select(static property => $"'{property.WireName}': {property.Description}").ToArray();
        return descriptions.Length is 0 ? null : string.Join(' ', descriptions);
    }
}
