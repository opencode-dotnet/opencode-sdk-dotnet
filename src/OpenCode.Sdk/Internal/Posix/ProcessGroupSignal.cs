using System.Runtime.InteropServices;
using OpenCode.Sdk.Internal.Posix.Abstractions;

namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// The shipped <see cref="IProcessGroupSignal"/> over <see cref="PosixInterop.Kill"/>. An id of zero
/// or one is refused outright: <c>kill(0, …)</c> signals the caller's own group, and
/// <c>kill(-1, …)</c> every process the caller may signal.
/// </summary>
internal sealed class ProcessGroupSignal : IProcessGroupSignal
{
    /// <summary><c>SIGTERM</c>, the same on Linux and macOS.</summary>
    private const int TerminateSignal = 15;

    /// <summary><c>SIGKILL</c>, the same on Linux and macOS.</summary>
    private const int KillSignal = 9;

    /// <summary><c>ESRCH</c>, the same on Linux and macOS.</summary>
    private const int NoSuchProcess = 3;

    /// <inheritdoc />
    public SignalDelivery SignalGroup(int processGroupId, ProcessSignal signal) =>
        Deliver(-Checked(processGroupId), Number(signal));

    /// <inheritdoc />
    public SignalDelivery SignalProcess(int processId, ProcessSignal signal) =>
        Deliver(Checked(processId), Number(signal));

    /// <inheritdoc />
    public SignalDelivery ProbeGroup(int processGroupId) => Deliver(-Checked(processGroupId), 0);

    /// <inheritdoc />
    public SignalDelivery ProbeProcess(int processId) => Deliver(Checked(processId), 0);

    private static int Checked(int id) =>
        id > 1 ? id : throw new ArgumentOutOfRangeException(nameof(id), id, "A process or group id must be greater than one.");

    private static int Number(ProcessSignal signal) => signal switch
    {
        ProcessSignal.Terminate => TerminateSignal,
        ProcessSignal.Kill => KillSignal,
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown signal."),
    };

    private static SignalDelivery Deliver(int target, int signal)
    {
        if (PosixInterop.Kill(target, signal) == 0)
        {
            return SignalDelivery.Delivered;
        }

        return Marshal.GetLastWin32Error() == NoSuchProcess ? SignalDelivery.NoSuchTarget : SignalDelivery.Refused;
    }
}
