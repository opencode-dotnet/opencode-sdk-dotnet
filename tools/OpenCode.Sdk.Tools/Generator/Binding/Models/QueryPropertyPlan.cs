namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record QueryPropertyPlan
{
    public required string WireName { get; init; }

    public required string PropertyName { get; init; }

    public required QueryValueKind Kind { get; init; }

    /// <summary>Gets the generated C# enum type name; set only for <see cref="QueryValueKind.Enum"/> and <see cref="QueryValueKind.EnumList"/>.</summary>
    public string? EnumTypeName { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Gets a value indicating whether the wire parameter is required, which the emitted
    /// property carries as C# <c>required</c> and non-nullable.
    /// </summary>
    public required bool IsRequired { get; init; }

    /// <summary>Gets a value indicating whether the property is inherited from the <c>ListRequest</c> base.</summary>
    public required bool IsInherited { get; init; }
}
