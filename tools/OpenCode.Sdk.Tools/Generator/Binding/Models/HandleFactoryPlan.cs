namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record HandleFactoryPlan
{
    public required string MethodName { get; init; }

    public required string HandleTypeName { get; init; }

    public required OperationParameterPlan Parameter { get; init; }

    /// <summary>Gets the diagnostic the factory carries when it returns an experimental handle from a stable collection.</summary>
    public string? ExperimentalDiagnostic { get; init; }
}
