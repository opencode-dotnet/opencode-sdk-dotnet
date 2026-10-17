using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// What every branch of one marked union binds against: the union's identity, the marker it
/// dispatches on, and the edges this single decision produces.
/// </summary>
internal sealed record UnionBindingContext
{
    /// <summary>Gets the union's graph key — the subject of every refusal.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the union's bound interface name, which every member inherits.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the literal marker the union dispatches on.</summary>
    public required LiteralMarker Marker { get; init; }

    /// <summary>Gets the ordered membership edges produced by this union decision alone.</summary>
    public required ICollection<UnionChildEdge> ChildEdges { get; init; }

    /// <summary>Gets the collector that publishes branch refusals in traversal order.</summary>
    public required BindingErrorCollector Errors { get; init; }
}
