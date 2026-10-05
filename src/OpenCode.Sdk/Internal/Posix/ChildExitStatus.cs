using System.Globalization;

namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// How a child process ended, as far as its parent learned it: an exit code, a terminating signal,
/// or neither when the status could not be read (another reaper took the child first). The record
/// is the decoded form of a <c>waitpid</c> status word; a Windows exit code fits it too, as a code.
/// </summary>
internal sealed record ChildExitStatus
{
    /// <summary>Low seven bits of the status word: zero for an exit, the signal number for a signal death.</summary>
    private const int TerminationMask = 0x7f;

    /// <summary>The low-bits value of a stopped child, reported only to a wait that asked for stops.</summary>
    private const int Stopped = 0x7f;

    /// <summary>Gets the status of a child whose end could not be read.</summary>
    public static ChildExitStatus Unknown { get; } = new();

    /// <summary>Gets the exit code, when the child exited on its own.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Gets the signal number, when a signal ended the child.</summary>
    public int? Signal { get; init; }

    /// <summary>
    /// Decodes a <c>waitpid</c> status word, the <c>WIFEXITED</c>/<c>WEXITSTATUS</c>/<c>WTERMSIG</c>
    /// macros spelled out, which Linux and macOS lay out the same way: zero low bits are an exit
    /// with the code in the next byte, and any other low bits but the stop marker are the
    /// terminating signal (the core-dump flag above them does not change it). A stop is not an end,
    /// and none is reported without <c>WUNTRACED</c>, so it reads as unknown.
    /// </summary>
    /// <param name="status">The status word <c>waitpid</c> stored.</param>
    /// <returns>The decoded status.</returns>
    public static ChildExitStatus FromWaitStatus(int status) => (status & TerminationMask) switch
    {
        0 => new ChildExitStatus { ExitCode = (status >> 8) & 0xff },
        Stopped => Unknown,
        var signal => new ChildExitStatus { Signal = signal },
    };

    /// <summary>Describes the end as a predicate for a sentence whose subject is the child.</summary>
    /// <returns>"exited with code N", "terminated on signal N", or "exited with an unknown status".</returns>
    public string Describe()
    {
        if (Signal is { } signal)
        {
            return "terminated on signal " + signal.ToString(CultureInfo.InvariantCulture);
        }

        return ExitCode is { } code
            ? "exited with code " + code.ToString(CultureInfo.InvariantCulture)
            : "exited with an unknown status";
    }
}
