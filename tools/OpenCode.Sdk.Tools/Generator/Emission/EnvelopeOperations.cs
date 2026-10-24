using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>Selects operations with a non-null envelope artifact name in stable ordinal name order.</summary>
internal static class EnvelopeOperations
{
    public static IEnumerable<OperationPlan> Naming(IReadOnlyList<ClientPlan> clients, Func<EnvelopePlan, string?> artifactName)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(artifactName);

        return clients
            .SelectMany(static client => client.Operations)
            .Where(operation => operation.Envelope is { } envelope && artifactName(envelope) is not null)
            .OrderBy(operation => artifactName(operation.Envelope!), StringComparer.Ordinal);
    }
}
