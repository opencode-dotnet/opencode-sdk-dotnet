namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>One operation a <c>declined</c> curation row keeps out of the pending set, with the reason the row declares.</summary>
internal sealed record DeclinedOperationPlan
{
    public required string OperationId { get; init; }

    public required string Reason { get; init; }

    public string? UpstreamOmitted { get; init; }
}
