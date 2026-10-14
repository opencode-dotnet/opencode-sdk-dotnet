using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Decides which client-side artifacts carry an experimental diagnostic. A public client whose
/// operations are all experimental under one id carries it on the class, and its methods then
/// carry nothing more. The compiler reports a member of a marked class only where the class is
/// named, so every way into a marked class carries the id too: the root property that returns it
/// and a handle factory on an unmarked collection. A route container whose routes are all
/// experimental carries the id on the class whatever the family's emission, because the routes
/// are public even when the family's clients are not; the root property of an internal-raw family
/// returns its hand-written door and takes the id its whole raw family carries.
/// </summary>
internal static class ExperimentalClientPolicy
{
    public static List<ClientPlan> Apply(IReadOnlyList<ClientPlan> clients, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(errors);

        foreach (var client in clients.Where(static client => client.Operations.Count > 0
                     && client.Operations.All(static operation => operation.ExperimentalDiagnostic is not null)))
        {
            var ids = client.Operations
                .Select(static operation => operation.ExperimentalDiagnostic!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length > 1)
            {
                errors.Add(BindingErrorCategory.Curation, client.Name,
                    $"experimental client '{client.Name}' carries multiple diagnostic ids: {string.Join(", ", ids)}");
            }
        }

        var containers = clients
            .SelectMany(static client => client.Operations)
            .GroupBy(static operation => operation.RouteContainerName, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => Diagnostic([.. group]), StringComparer.Ordinal);
        // A row the validator already refused can leave a client unnamed; binding reports that
        // error, so the policy only has to stay out of its way.
        var families = clients
            .Where(static client => client.Name is not null)
            .GroupBy(static client => client.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => Diagnostic([.. group.First().Operations]), StringComparer.Ordinal);

        // A hand-written door stands for its whole raw family, the handle's operations included,
        // so a family whose collection has none still marks the property that returns its door.
        var rawFamilies = clients
            .Where(static client => client is { Name: not null, Emission: EmissionMode.InternalRaw })
            .GroupBy(static client => client.ContainerName ?? client.Name, StringComparer.Ordinal)
            .SelectMany(static family =>
            {
                var id = Diagnostic([.. family.SelectMany(static client => client.Operations)]);
                return family.Select(client => (client.Name, Id: id));
            })
            .GroupBy(static entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.Ordinal);
        var marked = clients
            .Select(client => client with
            {
                ExperimentalDiagnostic = client.Emission is EmissionMode.Public ? Lookup(families, client.Name) : null,
                Operations = [.. client.Operations.Select(operation => operation with
                {
                    RouteContainerExperimentalDiagnostic = containers[operation.RouteContainerName],
                })],
            })
            .ToArray();
        return [.. marked.Select(client => client with
        {
            HandleFactory = client.HandleFactory is { } factory && client.ExperimentalDiagnostic is null
                ? factory with { ExperimentalDiagnostic = Lookup(families, factory.HandleTypeName) }
                : client.HandleFactory,
            SubClients = [.. client.SubClients.Select(reference => reference with
            {
                ExperimentalDiagnostic = Returned(families, rawFamilies, reference.TypeName),
            })],
        })];
    }

    private static string? Lookup(Dictionary<string, string?> families, string? name) =>
        name is null ? null : families.GetValueOrDefault(name);

    /// <summary>The id of the client a root property returns: a generated client by its name, a hand-written door by the raw client it wraps.</summary>
    private static string? Returned(Dictionary<string, string?> families, Dictionary<string, string?> rawFamilies, string? typeName)
    {
        if (typeName is null)
        {
            return null;
        }

        return families.TryGetValue(typeName, out var id) ? id : Lookup(rawFamilies, CSharpNamePolicy.ToRawClientName(typeName));
    }

    /// <summary>The one id every operation carries, or none when any operation is stable or the ids differ.</summary>
    private static string? Diagnostic(IReadOnlyList<OperationPlan> operations) =>
        operations.Count > 0
        && operations.All(operation => operation.ExperimentalDiagnostic is not null
                                       && string.Equals(operation.ExperimentalDiagnostic, operations[0].ExperimentalDiagnostic, StringComparison.Ordinal))
            ? operations[0].ExperimentalDiagnostic
            : null;
}
