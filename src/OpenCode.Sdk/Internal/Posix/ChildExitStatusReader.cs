using System.Runtime.InteropServices;
using OpenCode.Sdk.Internal.Posix.Abstractions;

namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// The shipped <see cref="IChildExitStatus"/>. Every number it passes is the kernel's own: the
/// <c>waitid</c> id type and flags, and the <c>struct sigaction</c> layout, which puts the handler
/// first and the flags after a signal set of 128 bytes on Linux and of 4 bytes on macOS.
/// </summary>
internal sealed class ChildExitStatusReader : IChildExitStatus
{
    /// <summary><c>P_PID</c>, the same on Linux and macOS.</summary>
    private const int ByProcessId = 1;

    /// <summary><c>WEXITED</c>, the same on Linux and macOS.</summary>
    private const int ExitedChildren = 4;

    /// <summary><c>WNOHANG</c>, the same on Linux and macOS.</summary>
    private const int NoHang = 1;

    /// <summary><c>WNOWAIT</c> on Linux.</summary>
    private const int LinuxNoWait = 0x0100_0000;

    /// <summary><c>WNOWAIT</c> on macOS.</summary>
    private const int MacNoWait = 0x20;

    /// <summary><c>EINTR</c>, the same on Linux and macOS.</summary>
    private const int Interrupted = 4;

    /// <summary>The <c>siginfo_t</c> buffer: 128 bytes on Linux, 104 on macOS.</summary>
    private const int SignalInfoCapacity = 128;

    /// <summary>The <c>struct sigaction</c> buffer, oversized for both layouts.</summary>
    private const int SignalActionCapacity = 256;

    /// <summary>The <c>sigset_t</c> between the handler and the flags on Linux.</summary>
    private const int LinuxSignalSetSize = 128;

    /// <summary>The <c>sigset_t</c> between the handler and the flags on macOS.</summary>
    private const int MacSignalSetSize = 4;

    /// <summary><c>SIGCHLD</c> on Linux.</summary>
    private const int LinuxChildSignal = 17;

    /// <summary><c>SIGCHLD</c> on macOS.</summary>
    private const int MacChildSignal = 20;

    /// <summary><c>SA_NOCLDWAIT</c> on Linux.</summary>
    private const int LinuxNoChildWait = 0x2;

    /// <summary><c>SA_NOCLDWAIT</c> on macOS.</summary>
    private const int MacNoChildWait = 0x20;

    /// <summary><c>SIG_IGN</c>: the handler value one on both.</summary>
    private static readonly IntPtr IgnoreHandler = new(1);

    /// <inheritdoc />
    public bool AreChildrenReapedAutomatically()
    {
        var mac = PosixPlatform.IsMacOS;
        var action = Marshal.AllocHGlobal(SignalActionCapacity);
        try
        {
            Marshal.Copy(new byte[SignalActionCapacity], 0, action, SignalActionCapacity);
            if (PosixInterop.QuerySignalAction(mac ? MacChildSignal : LinuxChildSignal, IntPtr.Zero, action) != 0)
            {
                return false;
            }

            var handler = Marshal.ReadIntPtr(action);
            var flags = Marshal.ReadInt32(action, IntPtr.Size + (mac ? MacSignalSetSize : LinuxSignalSetSize));
            return handler == IgnoreHandler || (flags & (mac ? MacNoChildWait : LinuxNoChildWait)) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(action);
        }
    }

    /// <inheritdoc />
    public bool WaitUntilExited(int processId)
    {
        var info = Marshal.AllocHGlobal(SignalInfoCapacity);
        try
        {
            var options = ExitedChildren | (PosixPlatform.IsMacOS ? MacNoWait : LinuxNoWait);
            while (true)
            {
                if (PosixInterop.WaitId(ByProcessId, processId, info, options) == 0)
                {
                    return true;
                }

                if (Marshal.GetLastWin32Error() != Interrupted)
                {
                    return false;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    /// <inheritdoc />
    public ChildExitStatus? Reap(int processId)
    {
        var waited = PosixInterop.WaitPid(processId, out var status, NoHang);
        if (waited == processId)
        {
            return ChildExitStatus.FromWaitStatus(status);
        }

        return waited == 0 ? null : ChildExitStatus.Unknown;
    }
}
