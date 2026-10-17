namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record GeneratedTypeNameClaim(string Name, string Subject,
    GeneratedTypeNameInventory.ClaimRole Role, string? WireName = null);
