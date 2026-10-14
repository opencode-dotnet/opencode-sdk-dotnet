namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>A record's explicit projection of the session identifier its payload declares.</summary>
internal sealed record SessionScopedEventPlan
{
    public required string InterfaceName { get; init; }

    public required string MemberName { get; init; }

    public required string PayloadPropertyName { get; init; }

    public required string IdentifierPropertyName { get; init; }
}
