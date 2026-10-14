using OpenCode.Sdk.Tools.Output;

namespace OpenCode.Sdk.Tools.Generator;

internal sealed record GenerationReport
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

    public required IReadOnlyList<string> PendingOperationIds
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    public required IReadOnlyList<string> TransportOwnedOperationIds
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    public required IReadOnlyList<string> DeclinedOperationIds
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    public required WriteResult WriteResult { get; init; }

    /// <summary>Gets the number of models a family other than their owner reaches, or their owner does not reach.</summary>
    public int ModelsReachedBeyondOwner { get; init; }
}
