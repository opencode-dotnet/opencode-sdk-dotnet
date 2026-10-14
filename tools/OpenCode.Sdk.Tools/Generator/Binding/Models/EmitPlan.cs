namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record EmitPlan
{
    public required IReadOnlyList<string> SelectedOperationIds
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    public required IReadOnlyList<ModelPlan> Models
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<ModelPlan>());

    public required IReadOnlyList<UnionPlan> Unions
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<UnionPlan>());

    /// <summary>Gets the interfaces emitted for hoisted promoted-object members (ADR-0011).</summary>
    public IReadOnlyList<HoistedInterfacePlan> HoistedInterfaces
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<HoistedInterfacePlan>());

    public required RegistryPlan Registry { get; init; }

    public required IReadOnlyList<ClientPlan> Clients
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<ClientPlan>());

    public required IReadOnlyList<PendingOperationPlan> PendingOperations
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<PendingOperationPlan>());

    /// <summary>
    /// Operations a <c>transportOwned</c> curation row covers: neither selected nor pending, their
    /// shape is fingerprint-pinned for a hand-written door (ADR-0021). Ordinal-sorted.
    /// </summary>
    public required IReadOnlyList<string> TransportOwnedOperationIds
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>
    /// Operations a <c>declined</c> curation row covers: neither selected nor pending, a standing
    /// wall refuses them and the maintainer has decided the wall stands. Ordinal-sorted by id.
    /// </summary>
    public required IReadOnlyList<DeclinedOperationPlan> DeclinedOperations
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<DeclinedOperationPlan>());

    /// <summary>
    /// Gets the stabilize duplicates the binder folded mechanically, the committed telltale the
    /// generation manifest carries in place of the curated rows the convention retires.
    /// </summary>
    public required StabilizeDuplicateCollapse ImplicitAliases { get; init; }

    /// <summary>
    /// Gets the slice each generated model lives in. Emission places model files from it; the
    /// generate command refuses while any model is unplaced.
    /// </summary>
    public ModelPlacement Placement { get; init; } = ModelPlacement.Empty;

    /// <summary>Gets the families whose selected operations reach each generated type; a report, never a placement input.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ModelReach { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}
