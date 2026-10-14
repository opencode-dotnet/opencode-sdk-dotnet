using System.Globalization;
using System.Runtime.InteropServices;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// One census of the launcher's handle kinds, written as
/// <c>processes=&lt;n&gt; exit-waits=&lt;n&gt; threads=&lt;n&gt; pipes=&lt;n&gt; jobs=&lt;n&gt;</c>, the form a
/// fixture prints and a test reads back.
/// </summary>
/// <param name="Processes">Handles on another process, the exit watch's duplicates aside.</param>
/// <param name="ExitWaits">The exit watch's <c>SYNCHRONIZE</c> duplicates of a process handle.</param>
/// <param name="Threads">Handles on another process's threads.</param>
/// <param name="Pipes">Pipe ends.</param>
/// <param name="Jobs">Jobs.</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct LauncherHandles(int Processes, int ExitWaits, int Threads, int Pipes, int Jobs)
{
    private static readonly string[] Names = ["processes", "exit-waits", "threads", "pipes", "jobs"];

    /// <summary>Reads a census back from its written form.</summary>
    /// <param name="text">The written form.</param>
    /// <returns>The census.</returns>
    /// <exception cref="FormatException">The text is not a census.</exception>
    public static LauncherHandles Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var fields = text.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != Names.Length)
        {
            throw new FormatException("Not a launcher handle census: '" + text + "'.");
        }

        var values = new int[Names.Length];
        for (var index = 0; index < Names.Length; index++)
        {
            var prefix = Names[index] + "=";
            if (!fields[index].StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(fields[index][prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out values[index]))
            {
                throw new FormatException("Not a launcher handle census: '" + text + "'.");
            }
        }

        return new LauncherHandles(values[0], values[1], values[2], values[3], values[4]);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Join(
            ' ',
            new[] { Processes, ExitWaits, Threads, Pipes, Jobs }.Select(
                (count, index) => Names[index] + "=" + count.ToString(CultureInfo.InvariantCulture)));
}
