namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The opencode-owned package names a JavaScript fixture names in its source. A fixture that boots
/// the pinned server imports upstream modules as bare specifiers, so the fixture is coupled to
/// names the accepted snapshot owns; this reads that coupling out of the text, before any install
/// decides whether the names happen to resolve on this machine.
/// </summary>
/// <remarks>
/// Specifiers are read from double-quoted literals, which is how every fixture here writes them.
/// A subpath is reduced to the package it belongs to, so <c>@opencode/util/effect/layer-node</c>
/// is reported as <c>@opencode/util</c>.
/// </remarks>
internal static class UpstreamPackageReferences
{
    private const string UpstreamScope = "@opencode";

    public static IReadOnlyCollection<string> In(string fixtureText)
    {
        ArgumentNullException.ThrowIfNull(fixtureText);

        // Splitting on the quote alternates outside/inside, so every odd segment is one literal.
        var segments = fixtureText.Split('"');
        var names = new SortedSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < segments.Length; index += 2)
        {
            if (segments[index].StartsWith(UpstreamScope, StringComparison.Ordinal))
            {
                names.Add(PackageName(segments[index]));
            }
        }

        return names;
    }

    /// <summary>Reduces a specifier to its package name: a scoped name keeps its first two segments.</summary>
    private static string PackageName(string specifier)
    {
        var segments = specifier.Split('/');
        return segments.Length >= 2 ? segments[0] + "/" + segments[1] : specifier;
    }
}
