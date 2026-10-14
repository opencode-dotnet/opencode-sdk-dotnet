namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// The interface emitted for a set of structurally identical promoted records so a hoisted
/// member has one CLR type. One wire schema still stays one record: the records keep their own
/// identity and implement this interface beside it.
/// </summary>
internal sealed record HoistedInterfacePlan
{
    public TypeDocumentationPlan Documentation { get; init; } = new();

    public required string Name { get; init; }

    public required string Namespace { get; init; }

    /// <summary>Gets the id the type carries when only experimental operations of one family reach it.</summary>
    public string? ExperimentalDiagnostic { get; init; }

    /// <summary>Gets the records this interface was derived from, in emission order.</summary>
    public required IReadOnlyList<string> ImplementedBy
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    public required IReadOnlyList<HoistedMemberPlan> Members
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<HoistedMemberPlan>());

    public string? Description { get; init; }
}
