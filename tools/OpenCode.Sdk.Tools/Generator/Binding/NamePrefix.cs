namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The name-prefix rule model ownership and Sessions areas share: a prefix matches when the name
/// starts with it, ignoring case, and the next character starts a new word (an upper-case letter or
/// a digit) or the name ends there. So <c>SessionFork</c> matches <c>SessionForkRequest</c> but not
/// <c>SessionForked</c>, and the <c>Websearch</c> family's prefix matches <c>WebSearchResult</c>.
/// </summary>
internal static class NamePrefix
{
    public static bool Matches(string name, string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && (name.Length == prefix.Length || char.IsUpper(name[prefix.Length]) || char.IsDigit(name[prefix.Length]));
    }
}
