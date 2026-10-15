namespace OpenCode.Sdk.Internal;

/// <summary>
/// The pinned client's <c>matchesVersion</c> (<c>service-version.ts</c>), the one version rule
/// Discover and the Ensure election share. The predicate form upstream's public client also accepts
/// has no SDK surface: the CLI never passes one, and an exact version is what the options carry.
/// </summary>
internal static class ServiceVersionPolicy
{
    /// <summary>Whether a reported version satisfies the expected one.</summary>
    /// <param name="reported">The version the probe reported, or null.</param>
    /// <param name="expected">The required version, or null for any.</param>
    /// <returns>True when nothing is expected, or when both are present and ordinally equal.</returns>
    public static bool MatchesVersion(string? reported, string? expected)
    {
        if (expected is null)
        {
            return true;
        }

        return reported is not null && string.Equals(reported, expected, StringComparison.Ordinal);
    }
}
