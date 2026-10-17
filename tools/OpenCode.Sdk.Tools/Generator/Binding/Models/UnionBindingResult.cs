namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>A union decision; a null schema key keeps the synthetic error union outside schema-node wiring.</summary>
internal sealed record UnionBindingResult(string? SchemaKey, UnionPlan? Plan, IReadOnlyList<UnionChildEdge> ChildEdges);
