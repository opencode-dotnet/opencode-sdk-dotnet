namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// The slice a generated type belongs to: an admitted family, or <see cref="CommonSlice"/> for the
/// shared error spine. <see cref="Area"/> names a Sessions model area, or is <see langword="null"/>.
/// </summary>
internal sealed record TypeHome
{
    public const string CommonSlice = "Common";

    public required string Slice
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value;
        }
    }

    public string? Area { get; init; }

    /// <summary>Gets the home of an operation's types: its family, with no model area.</summary>
    public static TypeHome Family(string family) => new() { Slice = family };

    public bool IsCommon => string.Equals(Slice, CommonSlice, StringComparison.Ordinal);
}
