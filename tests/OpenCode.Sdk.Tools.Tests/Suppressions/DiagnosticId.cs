using System.Globalization;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// One spelling per diagnostic ID, so two channels that name the same rule meet on one key: upper
/// case, and a bare compiler warning number (<c>618</c>) written as the compiler names it
/// (<c>CS0618</c>).
/// </summary>
internal static class DiagnosticId
{
    public static string Normalize(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var trimmed = id.Trim();
        return trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit)
            ? "CS" + int.Parse(trimmed, CultureInfo.InvariantCulture).ToString("D4", CultureInfo.InvariantCulture)
            : trimmed.ToUpperInvariant();
    }
}
