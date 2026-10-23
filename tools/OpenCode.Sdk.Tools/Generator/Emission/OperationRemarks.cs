using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// The remark every member emitted for an operation carries: the operation identity and its HTTP
/// method and route template, so a caller can join the member to the pinned document. The
/// identity is documentation text only; no emitter branches on it (ADR-0008).
/// </summary>
internal static class OperationRemarks
{
    public static IReadOnlyList<DocumentationRun> Of(OperationPlan operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Array.AsReadOnly(
        [
            DocumentationRun.Prose("Operation "),
            DocumentationRun.Code(operation.OperationId),
            DocumentationRun.Prose(": "),
            DocumentationRun.Code($"{operation.HttpMethod.ToUpperInvariant()} {operation.RouteTemplate}"),
            DocumentationRun.Prose("."),
            .. operation.CuratedRemarks is { } remarks ? new[] { DocumentationRun.Prose(" " + remarks) } : [],
        ]);
    }
}
