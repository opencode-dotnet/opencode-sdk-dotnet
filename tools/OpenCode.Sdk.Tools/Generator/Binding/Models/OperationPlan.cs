namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record OperationPlan
{
    /// <summary>
    /// Gets the operation identity the document was ingested under — the intended identity when a
    /// curation row repairs it. Emission documents it as text and never branches on it (ADR-0008).
    /// </summary>
    public required string OperationId { get; init; }

    public required string MethodName { get; init; }

    /// <summary>Gets the invariant-lowercase HTTP method.</summary>
    public required string HttpMethod { get; init; }

    public required string RouteTemplate { get; init; }

    /// <summary>Gets the diagnostic id of the experimental family the operation's route belongs to; <see langword="null"/> for a stable route.</summary>
    public string? ExperimentalDiagnostic { get; init; }

    /// <summary>Gets the id the operation's whole route container carries, which spares its members their own.</summary>
    public string? RouteContainerExperimentalDiagnostic { get; init; }

    /// <summary>Gets the nested container the route members live in (client name, or the group for root placement).</summary>
    public required string RouteContainerName { get; init; }

    /// <summary>Gets the route member name inside its container; containers never restate the group in members.</summary>
    public required string RouteMemberName { get; init; }

    /// <summary>Gets every route parameter in template order, including the handle-supplied one.</summary>
    public required IReadOnlyList<OperationParameterPlan> Parameters
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<OperationParameterPlan>());

    /// <summary>
    /// Gets the document-declared request headers, in declaration order. Only an internal-raw
    /// family may declare one, because its hand-written door owns the value (ADR-0021).
    /// </summary>
    public IReadOnlyList<DeclaredHeaderPlan> DeclaredHeaders
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<DeclaredHeaderPlan>());

    /// <summary>Gets the query-request record plan, or <see langword="null"/> when the operation has no query parameters.</summary>
    public QueryRequestPlan? QueryRequest { get; init; }

    /// <summary>Gets the request-body plan, or <see langword="null"/> when the operation sends no body.</summary>
    public RequestBodyPlan? RequestBody { get; init; }

    /// <summary>Gets the response envelope, or <see langword="null"/> when the operation streams.</summary>
    public EnvelopePlan? Envelope { get; init; }

    /// <summary>Gets the stream contract, or <see langword="null"/> when the operation answers once.</summary>
    public StreamPlan? Stream { get; init; }

    /// <summary>Gets the automatic cursor traversal, or <see langword="null"/> when the operation is not pageable.</summary>
    public PaginationPlan? Pagination { get; init; }

    public required ErrorMapPlan ErrorMap { get; init; }

    public string? Summary { get; init; }

    public string? Description { get; init; }
}
