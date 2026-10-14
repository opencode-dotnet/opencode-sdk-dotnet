using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// Counts the handles of this process that are of the kinds the Windows launcher creates, and no
/// others: a handle on another process (the server's, or the exit watch's <c>SYNCHRONIZE</c>
/// duplicate of it, counted apart), a handle on another process's thread (the server's primary
/// thread, held until it is resumed), a pipe end, and a job. The runtime's own churn is outside it:
/// each thread-pool or tiered-compilation worker adds a thread handle of this process and two
/// events, and an exited worker keeps them until its thread object is finalized, so the whole
/// process's handle count moves for reasons no launcher path controls. The Windows counterpart of
/// the POSIX host descriptor reader, which counts pipes only.
/// </summary>
/// <remarks>
/// One snapshot of the handle table (<c>ProcessHandleInformation</c>) gives each handle's granted
/// access and object type index; each type index is named once, by a type-only
/// <c>NtQueryObject</c>, which never waits, unlike a name query, which hangs on a synchronous pipe.
/// A handle closed between the snapshot and a question about it is skipped: it no longer exists.
/// The pipe question (<c>GetFileType</c>) waits behind a synchronous read pending on the same file
/// object; the launcher's pipe ends are overlapped, and a census is taken while nothing reads a
/// synchronous handle of this process. The snapshot layout read here is the 64-bit one.
/// </remarks>
internal static class LauncherHandleCensus
{
    /// <summary><c>ProcessHandleInformation</c>.</summary>
    private const int ProcessHandleInformation = 51;

    /// <summary><c>ObjectTypeInformation</c>.</summary>
    private const int ObjectTypeInformation = 2;

    /// <summary><c>STATUS_INFO_LENGTH_MISMATCH</c>.</summary>
    private const int InfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary><c>SYNCHRONIZE</c>: the one right the exit watch's duplicate carries.</summary>
    private const uint Synchronize = 0x00100000;

    /// <summary><c>FILE_TYPE_PIPE</c>.</summary>
    private const uint FileTypePipe = 3;

    /// <summary><c>PROCESS_HANDLE_SNAPSHOT_INFORMATION</c>: the handle count and a reserved word.</summary>
    private const int SnapshotHeaderSize = 16;

    /// <summary><c>PROCESS_HANDLE_TABLE_ENTRY_INFO</c>.</summary>
    private const int EntrySize = 40;

    private const int GrantedAccessOffset = 24;

    private const int ObjectTypeIndexOffset = 28;

    private static readonly IntPtr CurrentProcess = new(-1);

    /// <summary>Counts this process's launcher-kind handles now.</summary>
    /// <returns>The counts.</returns>
    /// <exception cref="PlatformNotSupportedException">The process is not a 64-bit Windows process.</exception>
    public static LauncherHandles Take()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException("The census reads a 64-bit Windows process's handle table.");
        }

        var self = GetCurrentProcessId();
        var types = new Dictionary<int, string>();
        int processes = 0, exitWaits = 0, threads = 0, pipes = 0, jobs = 0;
        foreach (var (handle, access, typeIndex) in Snapshot())
        {
            if (!types.TryGetValue(typeIndex, out var type))
            {
                if (TypeName(handle) is not { } named)
                {
                    continue;
                }

                type = named;
                types[typeIndex] = named;
            }

            switch (type)
            {
                // GetProcessId needs a query right a SYNCHRONIZE duplicate lacks, and answers 0 for
                // it, so the duplicate is told apart by its access before the owner is asked.
                case "Process" when access == Synchronize:
                    exitWaits++;
                    break;
                case "Process" when GetProcessId(handle) != self:
                    processes++;
                    break;
                case "Thread" when GetProcessIdOfThread(handle) != self:
                    threads++;
                    break;
                case "File" when GetFileType(handle) == FileTypePipe:
                    pipes++;
                    break;
                case "Job":
                    jobs++;
                    break;
                default:
                    break;
            }
        }

        return new LauncherHandles(processes, exitWaits, threads, pipes, jobs);
    }

    private static List<(IntPtr Handle, uint Access, int TypeIndex)> Snapshot()
    {
        var size = 1 << 16;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(CurrentProcess, ProcessHandleInformation, buffer, size, out var needed);
                if (status == InfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 4096);
                    continue;
                }

                if (status < 0)
                {
                    throw new Win32Exception(RtlNtStatusToDosError(status));
                }

                var count = Marshal.ReadInt64(buffer);
                var entries = new List<(IntPtr, uint, int)>((int)count);
                for (long index = 0; index < count; index++)
                {
                    var entry = IntPtr.Add(buffer, SnapshotHeaderSize + (int)(index * EntrySize));
                    entries.Add((
                        Marshal.ReadIntPtr(entry),
                        unchecked((uint)Marshal.ReadInt32(entry, GrantedAccessOffset)),
                        Marshal.ReadInt32(entry, ObjectTypeIndexOffset)));
                }

                return entries;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string? TypeName(IntPtr handle)
    {
        const int size = 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryObject(handle, ObjectTypeInformation, buffer, size, out _) < 0)
            {
                return null;
            }

            // OBJECT_TYPE_INFORMATION opens with the type name as a UNICODE_STRING: its length in
            // bytes, then, pointer-aligned, its buffer.
            var length = (ushort)Marshal.ReadInt16(buffer);
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, IntPtr.Size), length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("ntdll", EntryPoint = "NtQueryInformationProcess")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("ntdll", EntryPoint = "NtQueryObject")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryObject(IntPtr handle, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("ntdll", EntryPoint = "RtlNtStatusToDosError")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32", EntryPoint = "GetCurrentProcessId")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32", EntryPoint = "GetProcessId")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProcessId(IntPtr process);

    [DllImport("kernel32", EntryPoint = "GetProcessIdOfThread")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProcessIdOfThread(IntPtr thread);

    [DllImport("kernel32", EntryPoint = "GetFileType")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFileType(IntPtr file);
}
