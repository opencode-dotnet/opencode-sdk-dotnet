using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>One property an object arm constrains to a set of string or boolean literals.</summary>
internal sealed record StructuralSentinelPlan
{
    /// <summary>Gets the wire property name.</summary>
    public required string Property
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value;
        }
    }

    /// <summary>Gets the JSON kind every admitted value shares.</summary>
    public required LiteralKind Kind { get; init; }

    /// <summary>Gets the admitted values in their deterministic textual form, in document order.</summary>
    public required IReadOnlyList<string> Values
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
