namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record ObjectModelPlan : ModelPlan
{
    /// <summary>The public read-only view of an open model's extension data; no named property may take the name.</summary>
    public const string ExtensionDataMemberName = "AdditionalProperties";

    /// <summary>The internal bag the serializer fills behind <see cref="ExtensionDataMemberName"/>; no named property may take the name either.</summary>
    public const string ExtensionDataBagName = "OpenMembers";

    public required IReadOnlyList<ModelPropertyPlan> Properties
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<ModelPropertyPlan>());

    /// <summary>Gets every union interface this schema is a branch of; a schema can be a branch
    /// of more than one (ADR-0011).</summary>
    public IReadOnlyList<string> ImplementedUnionNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>Gets the hoisted carriers this record implements beside its own identity.</summary>
    public IReadOnlyList<string> ImplementedHoistedInterfaceNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>
    /// Gets the hoisted interface members this record answers explicitly because its own
    /// property cannot satisfy the declared type on its own.
    /// </summary>
    public IReadOnlyList<HoistedImplementationPlan> ExplicitHoistedImplementations
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<HoistedImplementationPlan>());

    /// <summary>
    /// Gets the query-side properties a merged operation request carries beside its body
    /// properties; they never serialize and the route builder consumes them instead.
    /// </summary>
    public IReadOnlyList<QueryPropertyPlan> RequestQueryProperties
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<QueryPropertyPlan>());

    /// <summary>
    /// Gets whether the record carries the extension-data member beside its named properties:
    /// the schema declares an unrestricted additional-properties bag, so every wire member the
    /// document leaves open lands there by name instead of being dropped.
    /// </summary>
    public bool EmitsExtensionData { get; init; }

    /// <summary>
    /// Gets whether the record prints its extension data masked: a curation row records that the
    /// members the document leaves open carry a secret. Only an open model sets it.
    /// </summary>
    public bool RedactsExtensionData { get; init; }
}
