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
/// One snapshot of the handle table (<c>ProcessHandleInformation</c>) gives each handle's value and
/// granted access. Each handle is then asked its type by a type-only <c>NtQueryObject</c>, which
/// never waits, unlike a name query, which hangs on a synchronous pipe. A handle can close between
/// the snapshot and a question about it, at any time, from the finalizer, and its value can be
/// reused: the type comes from the handle as it is now rather than from the snapshot, a handle
/// that answers no type is skipped, and an owner question that fails with
/// <c>ERROR_INVALID_HANDLE</c> skips the handle too, while any other failure, such as a handle
/// without the query right, still counts it as foreign. The pipe question (<c>GetFileType</c>) waits behind a synchronous read pending on the same file
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

    /// <summary><c>ERROR_INVALID_HANDLE</c>: the handle closed after the snapshot listed it.</summary>
    private const int InvalidHandle = 6;

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

        return Count(Snapshot());
    }

    /// <summary>
    /// Counts the launcher-kind handles among these handle values as they are now: a value that no
    /// longer names an open handle counts as nothing.
    /// </summary>
    /// <param name="handles">Handle values of this process with the access the snapshot listed.</param>
    /// <returns>The counts.</returns>
    public static LauncherHandles Count(IEnumerable<(IntPtr Handle, uint Access)> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);

        var self = GetCurrentProcessId();
        int processes = 0, exitWaits = 0, threads = 0, pipes = 0, jobs = 0;
        foreach (var (handle, access) in handles)
        {
            switch (TypeName(handle))
            {
                // GetProcessId needs a query right a SYNCHRONIZE duplicate lacks, so the duplicate
                // is told apart by its access before the owner is asked.
                case "Process" when access == Synchronize:
                    exitWaits++;
                    break;
                case "Process" when IsForeign(GetProcessId(handle), self) is true:
                    processes++;
                    break;
                case "Thread" when IsForeign(GetProcessIdOfThread(handle), self) is true:
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

    /// <summary>
    /// Reads an owner answer: true for another process, false for this one, and null for a handle
    /// that closed after it was listed, which counts as nothing.
    /// </summary>
    private static bool? IsForeign(uint owner, uint self)
    {
        if (owner != 0)
        {
            return owner != self;
        }

        // Any other failure, a handle without the query right among them, stays foreign.
        return Marshal.GetLastWin32Error() == InvalidHandle ? null : true;
    }

    private static List<(IntPtr Handle, uint Access)> Snapshot()
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
                var entries = new List<(IntPtr, uint)>((int)count);
                for (long index = 0; index < count; index++)
                {
                    var entry = IntPtr.Add(buffer, SnapshotHeaderSize + (int)(index * EntrySize));
                    entries.Add((Marshal.ReadIntPtr(entry), unchecked((uint)Marshal.ReadInt32(entry, GrantedAccessOffset))));
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

    [DllImport("kernel32", EntryPoint = "GetProcessId", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProcessId(IntPtr process);

    [DllImport("kernel32", EntryPoint = "GetProcessIdOfThread", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProcessIdOfThread(IntPtr thread);

    [DllImport("kernel32", EntryPoint = "GetFileType")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFileType(IntPtr file);
}
