namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>Describes one located failure discovered while binding selected operations.</summary>
/// <param name="Category">The binding stage that refused the input.</param>
/// <param name="Subject">The operation, schema, or curation key associated with the failure.</param>
/// <param name="Problem">A description of the failure.</param>
public sealed record BindingError(BindingErrorCategory Category, string Subject, string Problem)
{
    internal BindingErrorKind Kind { get; init; }
}
