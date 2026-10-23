using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// Test-only C library bindings for reading where a process sits in the POSIX process tree and for
/// sending a signal the SDK itself never sends. Linux and macOS give these calls the same fixed
/// signatures and the same numbers, except the job-control signals, whose numbers differ.
/// </summary>
internal static class PosixProcessProbe
{
    /// <summary><c>SIGINT</c>, the same on Linux and macOS.</summary>
    public const int Interrupt = 2;

    /// <summary><c>SIGKILL</c>, the same on Linux and macOS.</summary>
    public const int Kill = 9;

    /// <summary>The standard signals 1 through 31 in a <c>/proc</c> mask, where bit n-1 is signal n.</summary>
    public const ulong StandardSignals = 0x7FFF_FFFF;

    private const int LinkCapacity = 4096;

    /// <summary><c>P_PID</c>, the same on Linux and macOS.</summary>
    private const int ByProcessId = 1;

    /// <summary><c>EINTR</c>, the same on Linux and macOS.</summary>
    private const int Interrupted = 4;

    /// <summary>The <c>siginfo_t</c> size on both kernels.</summary>
    private const int SignalInfoCapacity = 128;

    /// <summary><c>WSTOPPED</c> on Linux.</summary>
    private const int LinuxStopped = 0x2;

    /// <summary><c>WSTOPPED</c> on macOS.</summary>
    private const int MacStopped = 0x8;

    /// <summary><c>WNOWAIT</c> on Linux.</summary>
    private const int LinuxNoWait = 0x0100_0000;

    /// <summary><c>WNOWAIT</c> on macOS.</summary>
    private const int MacNoWait = 0x20;

    /// <summary>Gets <c>SIGSTOP</c>: 17 on macOS, 19 on Linux.</summary>
    public static int Stop => OperatingSystem.IsMacOS() ? 17 : 19;

    /// <summary>Gets <c>SIGCONT</c>: 19 on macOS, 18 on Linux.</summary>
    public static int Continue => OperatingSystem.IsMacOS() ? 19 : 18;

    /// <summary>
    /// Blocks until the kernel reports a child of this process stopped:
    /// <c>waitid(P_PID, pid, WSTOPPED | WNOWAIT)</c>, repeated on <c>EINTR</c>, through the SDK's own
    /// binding. <c>WNOWAIT</c> leaves the report for every other wait, and nothing is reaped.
    /// </summary>
    /// <param name="processId">A child of this process.</param>
    /// <returns>True once the stop was reported; false when the wait failed, as for a pid that is no child of this process.</returns>
    public static bool WaitUntilStopped(int processId)
    {
        var options = OperatingSystem.IsMacOS() ? MacStopped | MacNoWait : LinuxStopped | LinuxNoWait;
        var info = Marshal.AllocHGlobal(SignalInfoCapacity);
        try
        {
            Marshal.Copy(new byte[SignalInfoCapacity], 0, info, SignalInfoCapacity);
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

    /// <summary>Gets the session id of a process: <c>getsid</c>; zero names the caller.</summary>
    /// <param name="processId">The process.</param>
    /// <returns>The session id, or -1 when the process does not exist.</returns>
    public static int SessionOf(int processId) => GetSessionId(processId);

    /// <summary>Gets the process group id of a process: <c>getpgid</c>; zero names the caller.</summary>
    /// <param name="processId">The process.</param>
    /// <returns>The group id, or -1 when the process does not exist.</returns>
    public static int GroupOf(int processId) => GetProcessGroupId(processId);

    /// <summary>Sends a signal to every member of a group: <c>kill(-group, signal)</c>.</summary>
    /// <param name="processGroupId">The group, greater than one.</param>
    /// <param name="signal">The signal number.</param>
    /// <returns>True when at least one member received it.</returns>
    public static bool SignalGroup(int processGroupId, int signal) =>
        processGroupId > 1
            ? SendSignal(-processGroupId, signal) == 0
            : throw new ArgumentOutOfRangeException(nameof(processGroupId), processGroupId, "A group id must be greater than one.");

    /// <summary>Sends a signal to one process: <c>kill(pid, signal)</c>.</summary>
    /// <param name="processId">The process, greater than one.</param>
    /// <param name="signal">The signal number.</param>
    /// <returns>True when the process received it.</returns>
    public static bool SignalProcess(int processId, int signal) =>
        processId > 1
            ? SendSignal(processId, signal) == 0
            : throw new ArgumentOutOfRangeException(nameof(processId), processId, "A process id must be greater than one.");

    /// <summary>Reads one hexadecimal mask (<c>SigIgn:</c>, <c>SigBlk:</c>) from a Linux <c>/proc/&lt;pid&gt;/status</c> text.</summary>
    /// <param name="status">The status text, or the lines of it a test captured.</param>
    /// <param name="field">The field name with its colon.</param>
    /// <returns>The mask.</returns>
    public static ulong StatusMask(string status, string field)
    {
        ArgumentNullException.ThrowIfNull(status);
        var line = status
            .Split('\n')
            .Single(candidate => candidate.StartsWith(field, StringComparison.Ordinal));
        return ulong.Parse(line[field.Length..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a symbolic link's target: <c>readlink</c>, which a <c>/proc</c> descriptor link answers with what it holds.</summary>
    /// <param name="path">The link.</param>
    /// <returns>The target, or null when the link no longer exists.</returns>
    public static string? LinkTarget(string path)
    {
        var buffer = Marshal.AllocHGlobal(LinkCapacity);
        try
        {
            var length = ReadLink(path, buffer, new IntPtr(LinkCapacity)).ToInt64();
            if (length <= 0)
            {
                return null;
            }

            var bytes = new byte[(int)length];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", EntryPoint = "readlink", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr ReadLink([MarshalAs(UnmanagedType.LPStr)] string path, IntPtr buffer, IntPtr capacity);

    [DllImport("libc", EntryPoint = "getsid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int GetSessionId(int processId);

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int GetProcessGroupId(int processId);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SendSignal(int processId, int signal);
}
