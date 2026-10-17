namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record BoundOperation
{
    public required string OperationId { get; init; }

    public required GroupCuration Row { get; init; }

    public required bool IsHandleOperation { get; init; }

    public required OperationPlan Plan { get; init; }
}
