namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// Finds a NUL in text the launcher passes to the C library, which ends a C string there. A plain
/// loop, because the analyzer wall leaves no other form: <c>Contains(char)</c> and
/// <c>IndexOf(char)</c> trade CA1307/MA0001 against CA2249, and the <c>StringComparison</c> overload
/// is absent downlevel.
/// </summary>
internal static class NulText
{
    /// <summary>Reports whether the text contains a NUL.</summary>
    /// <param name="value">The text.</param>
    /// <returns>True when a NUL occurs.</returns>
    public static bool Occurs(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\0')
            {
                return true;
            }
        }

        return false;
    }
}
