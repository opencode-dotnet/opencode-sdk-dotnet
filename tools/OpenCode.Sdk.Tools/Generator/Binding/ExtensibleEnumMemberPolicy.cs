using System.Collections.Frozen;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The members an extensible enum declares beside its values. A value whose C# name is one of
/// them, or the enum's own name, would collide with the generated type, so binding refuses it and
/// an <c>enumMemberNames</c> row names the value instead.
/// </summary>
internal static class ExtensibleEnumMemberPolicy
{
    public const string IsKnown = "IsKnown";

    public const string FromString = "FromString";

    /// <summary>Gets every member name the generated struct declares or inherits besides the values.</summary>
    public static FrozenSet<string> DeclaredNames { get; } = FrozenSet.ToFrozenSet(
        [IsKnown, FromString, "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "ReferenceEquals"],
        StringComparer.Ordinal);
}
