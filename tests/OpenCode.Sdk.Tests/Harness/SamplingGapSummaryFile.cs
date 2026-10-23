using System.Globalization;
using System.IO.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// Writes the sampling-gap detector's session summary into the results directory, where CI keeps
/// it. The write is a report, not a check: an I/O failure goes to the error writer and never fails
/// the session.
/// </summary>
internal static class SamplingGapSummaryFile
{
    /// <summary>Writes <paramref name="text"/> to <c>sampling-gap-summary-&lt;pid&gt;.txt</c> under <paramref name="directory"/>.</summary>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="directory">The results directory.</param>
    /// <param name="processId">The test host's pid, which names the file.</param>
    /// <param name="text">The summary.</param>
    /// <param name="error">Where an I/O failure is reported.</param>
    /// <returns>True when the file was written; false when an I/O failure was reported instead.</returns>
    public static async Task<bool> WriteAsync(IFileSystem fileSystem, string directory, int processId, string text, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(error);
        var path = fileSystem.Path.Combine(directory, "sampling-gap-summary-" + processId.ToString(CultureInfo.InvariantCulture) + ".txt");
        try
        {
            _ = fileSystem.Directory.CreateDirectory(directory);
#if NET
            await fileSystem.File.WriteAllTextAsync(path, text);
#else
            await Task.Run(() => fileSystem.File.WriteAllText(path, text));
#endif
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("sampling-gap: could not write the session summary to '" + path + "': " + exception.Message);
            return false;
        }
    }
}
