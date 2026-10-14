using System.Globalization;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// How an owned server ended, as <see cref="OpenCodeServer.Exited"/> reports it: an exit code, a
/// terminating signal, or an unknown status. Exactly the members <see cref="Kind"/> names are set.
/// </summary>
public sealed record OpenCodeServerExit
{
    private OpenCodeServerExit(OpenCodeServerExitKind kind, int? exitCode, string? signal, int? signalNumber)
    {
        Kind = kind;
        ExitCode = exitCode;
        Signal = signal;
        SignalNumber = signalNumber;
    }

    /// <summary>Gets the end of a server whose status could not be read.</summary>
    public static OpenCodeServerExit Unknown { get; } = new(OpenCodeServerExitKind.Unknown, null, null, null);

    /// <summary>Gets how the server ended; members may be added in a minor version.</summary>
    public OpenCodeServerExitKind Kind { get; }

    /// <summary>
    /// Gets the exit code when <see cref="Kind"/> is <see cref="OpenCodeServerExitKind.Exited"/>,
    /// otherwise <see langword="null"/>. On Linux and macOS it is 0 to 255. On Windows it is the
    /// operating system's code in its full 32 bits, as <c>Process.ExitCode</c> gives it, so an
    /// access violation reads as <c>-1073741819</c> (<c>0xC0000005</c>).
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Gets the terminating signal's name, such as <c>SIGTERM</c>, the name upstream reports, when
    /// <see cref="Kind"/> is <see cref="OpenCodeServerExitKind.Signaled"/>. It is
    /// <see langword="null"/> for a signal the host's operating system gives no standard name, such
    /// as a Linux real-time signal; <see cref="SignalNumber"/> still carries it.
    /// </summary>
    public string? Signal { get; }

    /// <summary>
    /// Gets the terminating signal's number on the host's operating system when <see cref="Kind"/>
    /// is <see cref="OpenCodeServerExitKind.Signaled"/>, otherwise <see langword="null"/>. Numbers
    /// differ between Linux and macOS (<c>SIGBUS</c> is 7 on Linux and 10 on macOS); compare
    /// <see cref="Signal"/> to stay portable.
    /// </summary>
    public int? SignalNumber { get; }

    /// <summary>Creates the end of a server that exited with a code.</summary>
    /// <param name="exitCode">The exit code.</param>
    /// <returns>The end.</returns>
    public static OpenCodeServerExit FromExitCode(int exitCode) =>
        new(OpenCodeServerExitKind.Exited, exitCode, null, null);

    /// <summary>Creates the end of a server a signal ended.</summary>
    /// <param name="signal">The signal's name, or <see langword="null"/> when it has no standard name.</param>
    /// <param name="signalNumber">The signal's number on the host's operating system.</param>
    /// <returns>The end.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="signalNumber"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="signal"/> is empty or whitespace.</exception>
    public static OpenCodeServerExit FromSignal(string? signal, int signalNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(signalNumber);
        if (signal is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        }

        return new(OpenCodeServerExitKind.Signaled, null, signal, signalNumber);
    }

    /// <summary>
    /// Describes the end: <c>exited with code 1</c>; on Windows a negative code with its
    /// hexadecimal form, <c>exited with code -1073741819 (0xC0000005)</c>;
    /// <c>terminated on signal SIGTERM (15)</c>, or <c>terminated on signal 40</c> for a signal
    /// without a name; <c>exited with an unknown status</c>.
    /// </summary>
    /// <returns>The description.</returns>
    public override string ToString() => Kind switch
    {
        OpenCodeServerExitKind.Exited when ExitCode is < 0 =>
            "exited with code " + ExitCode.Value.ToString(CultureInfo.InvariantCulture) + " (0x" + ExitCode.Value.ToString("X8", CultureInfo.InvariantCulture) + ")",
        OpenCodeServerExitKind.Exited => "exited with code " + ExitCode!.Value.ToString(CultureInfo.InvariantCulture),
        OpenCodeServerExitKind.Signaled when Signal is not null =>
            "terminated on signal " + Signal + " (" + SignalNumber!.Value.ToString(CultureInfo.InvariantCulture) + ")",
        OpenCodeServerExitKind.Signaled => "terminated on signal " + SignalNumber!.Value.ToString(CultureInfo.InvariantCulture),
        OpenCodeServerExitKind.Unknown or _ => "exited with an unknown status",
    };

    /// <summary>Translates the launcher's decoded status, naming a signal by the host's table.</summary>
    /// <param name="status">The status the exit watch decoded.</param>
    /// <param name="names">The host operating system's signal names.</param>
    /// <returns>The end.</returns>
    internal static OpenCodeServerExit From(ChildExitStatus status, PosixSignalNames names)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(names);

        if (status.Signal is { } signal)
        {
            return FromSignal(names.NameOf(signal), signal);
        }

        return status.ExitCode is { } code ? FromExitCode(code) : Unknown;
    }
}
