using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// Test-only C library bindings for reading where a process sits in the POSIX process tree and for
/// sending a signal the SDK itself never sends. Linux and macOS give these calls the same fixed
/// signatures and the same numbers.
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
