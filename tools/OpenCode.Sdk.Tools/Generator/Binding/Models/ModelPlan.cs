namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal abstract record ModelPlan
{
    public TypeDocumentationPlan Documentation { get; init; } = new();

    public required string Name { get; init; }

    public required string Namespace { get; init; }

    /// <summary>Gets the id the type carries when only experimental operations of one family reach it.</summary>
    public string? ExperimentalDiagnostic { get; init; }

    public string? Description { get; init; }
}
