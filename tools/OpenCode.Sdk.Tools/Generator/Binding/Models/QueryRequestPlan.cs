namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record QueryRequestPlan
{
    public TypeDocumentationPlan Documentation { get; init; } = new();

    public required string TypeName { get; init; }

    /// <summary>Gets a value indicating whether the record derives from the <c>ListRequest</c> base.</summary>
    public required bool DerivesFromListRequest { get; init; }

    /// <summary>
    /// Gets a value indicating whether the query properties ride the operation's request
    /// body model instead of a standalone record; the type names match by construction.
    /// </summary>
    public bool RidesRequestBody { get; init; }

    /// <summary>
    /// Gets a value indicating whether any bound parameter is required, which makes the
    /// request itself required on the emitted method and route builder.
    /// </summary>
    public bool HasRequiredMember => Properties.Any(static property => property.IsRequired);

    /// <summary>Gets every bound query parameter in wire order, including base-inherited ones.</summary>
    public required IReadOnlyList<QueryPropertyPlan> Properties
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<QueryPropertyPlan>());
}
