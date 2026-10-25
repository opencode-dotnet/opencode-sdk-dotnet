namespace OpenCode.Sdk.Tools.Output;

internal sealed record WriteResult
{
    public required bool IsVerification { get; init; }

    public required IReadOnlyList<string> CreatedPaths
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    public required IReadOnlyList<string> ChangedPaths
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    public required IReadOnlyList<string> DeletedPaths
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    public bool HasChanges => CreatedPaths.Count > 0 || ChangedPaths.Count > 0 || DeletedPaths.Count > 0;
}
