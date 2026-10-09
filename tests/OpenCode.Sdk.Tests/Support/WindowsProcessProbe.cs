using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// What a Windows test asks the kernel about a process or a handle the launcher placed: whether a
/// process belongs to a job (a nested job counts toward every job above it), and whether a handle
/// is inheritable.
/// </summary>
internal static class WindowsProcessProbe
{
    /// <summary><c>HANDLE_FLAG_INHERIT</c>.</summary>
    private const uint HandleFlagInherit = 0x00000001;

    /// <summary>Asks whether a process belongs to a job.</summary>
    /// <param name="process">A handle on the process.</param>
    /// <param name="job">The job.</param>
    /// <returns>True when the process is in the job or in a job nested under it.</returns>
    public static bool IsInJob(SafeHandle process, SafeHandle job)
    {
        if (!IsProcessInJob(process, job, out var result))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return result;
    }

    /// <summary>Asks whether a handle is inheritable.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>True when a child created with handle inheritance would receive it.</returns>
    public static bool IsInheritable(SafeHandle handle)
    {
        if (!GetHandleInformation(handle, out var flags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (flags & HandleFlagInherit) != 0;
    }

    [DllImport("kernel32", EntryPoint = "IsProcessInJob", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeHandle process, SafeHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32", EntryPoint = "GetHandleInformation", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(SafeHandle handle, out uint flags);
}
