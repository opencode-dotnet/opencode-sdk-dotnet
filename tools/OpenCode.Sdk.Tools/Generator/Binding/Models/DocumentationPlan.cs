namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>Bound plain text; emission owns XML escaping.</summary>
internal sealed record DocumentationPlan(string Summary, string? Remarks = null);
