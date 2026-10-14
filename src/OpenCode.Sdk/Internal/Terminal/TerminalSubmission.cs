using System.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Composes the one line a terminal submit sends. A terminal's Enter key is carriage return, and
/// a submit is exactly one Enter, so the line the caller hands over carries no break of its own.
/// Both terminal families submit through this rule, because a consumer without an emulator has no
/// other way to learn it. Knowledge source: upstream-observed — the TUI forwards the emulator's
/// raw bytes, so upstream has no submit door to copy the terminator from.
/// </summary>
internal static class TerminalSubmission
{
    /// <summary>The carriage return a terminal's Enter key sends.</summary>
    public const string Enter = "\r";

    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>Composes one submitted line: the caller's text, then <see cref="Enter"/>.</summary>
    /// <param name="line">The guarded, non-null line to submit.</param>
    /// <param name="parameterName">The public parameter a refusal names.</param>
    /// <returns>The input to send.</returns>
    /// <exception cref="ArgumentException">The line carries a carriage return or a line feed.</exception>
    public static string Compose(string line, string parameterName)
    {
        // The null guard belongs to the public door: Debug.Assert carries no DoesNotReturnIf on
        // net472 or netstandard2.0, so asserting non-nullness here would leave the dereference
        // below warning on exactly those two targets.
        Debug.Assert(!string.IsNullOrEmpty(parameterName), "A refusal names the caller's parameter.");

        if (line.IndexOfAny(LineBreaks) >= 0)
        {
            throw new ArgumentException(
                "A submitted line is exactly one line: it cannot contain a carriage return or a line feed, " +
                "because the submit adds the terminal's Enter and an embedded break would submit a different " +
                "command than the caller wrote. Send raw input with WriteAsync instead.",
                parameterName);
        }

        return line + Enter;
    }
}
