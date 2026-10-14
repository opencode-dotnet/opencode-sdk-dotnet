namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record ClientReferencePlan
{
    public required string PropertyName { get; init; }

    public required string TypeName { get; init; }

    /// <summary>Gets the id the property carries when the client it returns is experimental.</summary>
    public string? ExperimentalDiagnostic { get; init; }
}
