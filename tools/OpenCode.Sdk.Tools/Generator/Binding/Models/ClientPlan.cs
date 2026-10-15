namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record ClientPlan
{
    public TypeDocumentationPlan Documentation { get; init; } = new();

    public required string Name { get; init; }

    public required string Namespace { get; init; }

    public required ClientRole Role { get; init; }

    /// <summary>Gets how the client reaches callers; the root client always emits public.</summary>
    public EmissionMode Emission { get; init; }

    /// <summary>Gets the id a public client carries when every operation it emits is experimental under it.</summary>
    public string? ExperimentalDiagnostic { get; init; }

    /// <summary>Gets the feature folder the family's files live in; <see langword="null"/> keeps the root client at the project root.</summary>
    public string? ContainerName { get; init; }

    /// <summary>Gets the group client properties exposed by the root client.</summary>
    public required IReadOnlyList<ClientReferencePlan> SubClients
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<ClientReferencePlan>());

    /// <summary>Gets the handle factory a collection client exposes, when its group declares a handle.</summary>
    public HandleFactoryPlan? HandleFactory { get; init; }

    /// <summary>Gets the partially applied parameter a handle client carries as immutable state.</summary>
    public OperationParameterPlan? HandleParameter { get; init; }

    public required IReadOnlyList<OperationPlan> Operations
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<OperationPlan>());
}
