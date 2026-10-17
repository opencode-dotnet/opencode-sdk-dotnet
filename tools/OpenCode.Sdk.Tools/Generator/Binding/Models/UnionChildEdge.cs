namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record UnionChildEdge(string SchemaKey, string ParentTypeName, UnionFixedMarkerPlan? FixedMarker = null);
