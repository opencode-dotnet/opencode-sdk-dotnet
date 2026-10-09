using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// Every kernel32 function the SDK binds, in one place: process creation with an explicit
/// inherited-handle list, the named pipes a child's standard streams run over, the job object that
/// ends a server with its owner, and the exit and termination calls. The modern targets take
/// <c>LibraryImport</c>, the compile-time stub .NET recommends (its generator is what needs the
/// project's unsafe-code switch); the <c>net472</c> and <c>netstandard2.0</c> assets take the
/// equivalent <c>DllImport</c>. Every binding has a fixed signature, and the structures carry
/// pointer-sized fields wherever Windows declares <c>SIZE_T</c>, <c>ULONG_PTR</c>, or a handle, so
/// their layout follows the process's bitness on x86, x64, and arm64 alike.
/// </summary>
internal static partial class WindowsInterop
{
    /// <summary><c>CREATE_SUSPENDED</c>: the main thread waits for <c>ResumeThread</c> before it runs a single instruction.</summary>
    internal const uint CreateSuspended = 0x00000004;

    /// <summary><c>DETACHED_PROCESS</c>: the child gets no console at all.</summary>
    internal const uint DetachedProcess = 0x00000008;

    /// <summary><c>CREATE_NEW_PROCESS_GROUP</c>: the child roots its own process group and starts with Ctrl+C ignored.</summary>
    internal const uint CreateNewProcessGroup = 0x00000200;

    /// <summary><c>CREATE_UNICODE_ENVIRONMENT</c>: the environment block is UTF-16.</summary>
    internal const uint CreateUnicodeEnvironment = 0x00000400;

    /// <summary><c>EXTENDED_STARTUPINFO_PRESENT</c>: the startup info carries an attribute list.</summary>
    internal const uint ExtendedStartupInfoPresent = 0x00080000;

    /// <summary><c>CREATE_NO_WINDOW</c>: a console child gets a console of its own that has no window.</summary>
    internal const uint CreateNoWindow = 0x08000000;

    /// <summary><c>STARTF_USESHOWWINDOW</c>: the first window the child shows takes the startup info's show state.</summary>
    internal const uint UseShowWindow = 0x00000001;

    /// <summary><c>STARTF_USESTDHANDLES</c>: the three standard handles come from the startup info.</summary>
    internal const uint UseStandardHandles = 0x00000100;

    /// <summary><c>SW_HIDE</c>: that first window starts hidden.</summary>
    internal const short HideWindow = 0;

    /// <summary><c>STD_INPUT_HANDLE</c>.</summary>
    internal const int StandardInputHandle = -10;

    /// <summary><c>STD_OUTPUT_HANDLE</c>.</summary>
    internal const int StandardOutputHandle = -11;

    /// <summary><c>STD_ERROR_HANDLE</c>.</summary>
    internal const int StandardErrorHandle = -12;

    /// <summary><c>GENERIC_READ</c>.</summary>
    internal const uint GenericRead = 0x80000000;

    /// <summary><c>GENERIC_WRITE</c>.</summary>
    internal const uint GenericWrite = 0x40000000;

    /// <summary><c>FILE_READ_ATTRIBUTES</c>.</summary>
    internal const uint FileReadAttributes = 0x00000080;

    /// <summary><c>FILE_WRITE_ATTRIBUTES</c>.</summary>
    internal const uint FileWriteAttributes = 0x00000100;

    /// <summary><c>WRITE_DAC</c>.</summary>
    internal const uint WriteDac = 0x00040000;

    /// <summary><c>SYNCHRONIZE</c>: the right a wait on a process handle needs.</summary>
    internal const uint Synchronize = 0x00100000;

    /// <summary><c>FILE_SHARE_READ | FILE_SHARE_WRITE</c>.</summary>
    internal const uint ShareReadWrite = 0x00000003;

    /// <summary><c>OPEN_EXISTING</c>.</summary>
    internal const uint OpenExisting = 3;

    /// <summary><c>PIPE_ACCESS_INBOUND</c>: the server end only reads.</summary>
    internal const uint PipeAccessInbound = 0x00000001;

    /// <summary><c>PIPE_ACCESS_OUTBOUND</c>: the server end only writes.</summary>
    internal const uint PipeAccessOutbound = 0x00000002;

    /// <summary><c>FILE_FLAG_FIRST_PIPE_INSTANCE</c>: creation fails if the name already exists, so no other process can have staged it.</summary>
    internal const uint FirstPipeInstance = 0x00080000;

    /// <summary><c>FILE_FLAG_OVERLAPPED</c>: I/O on the handle completes on the I/O completion port.</summary>
    internal const uint Overlapped = 0x40000000;

    /// <summary><c>PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS</c>.</summary>
    internal const uint LocalBytePipe = 0x00000008;

    /// <summary><c>DUPLICATE_SAME_ACCESS</c>.</summary>
    internal const uint DuplicateSameAccess = 0x00000002;

    /// <summary><c>JobObjectExtendedLimitInformation</c>, the information class of <see cref="JobExtendedLimitInformation"/>.</summary>
    internal const int JobObjectExtendedLimitInformationClass = 9;

    /// <summary><c>JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION</c>: a member that crashes ends without an error-reporting dialog.</summary>
    internal const uint JobLimitDieOnUnhandledException = 0x00000400;

    /// <summary><c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c>: a member may create a child outside the job on request.</summary>
    internal const uint JobLimitBreakawayOk = 0x00000800;

    /// <summary><c>JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK</c>: a member's children are created outside the job unless something assigns them.</summary>
    internal const uint JobLimitSilentBreakawayOk = 0x00001000;

    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: closing the job's last handle ends every member.</summary>
    internal const uint JobLimitKillOnJobClose = 0x00002000;

    /// <summary><c>ERROR_ACCESS_DENIED</c>.</summary>
    internal const int AccessDenied = 5;

    /// <summary><c>ERROR_INVALID_PARAMETER</c>.</summary>
    internal const int InvalidParameter = 87;

    /// <summary><c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>: only the listed handles cross into the child.</summary>
    internal static readonly IntPtr HandleListAttribute = new(0x20002);

    /// <summary>The pseudo-handle <c>GetCurrentProcess</c> returns; it needs no closing.</summary>
    internal static readonly IntPtr CurrentProcess = new(-1);

    /// <summary><c>INVALID_HANDLE_VALUE</c>.</summary>
    internal static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary><c>STARTUPINFOW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public uint StructureSize;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedData;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    /// <summary><c>STARTUPINFOEXW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo Startup;
        public IntPtr AttributeList;
    }

    /// <summary><c>PROCESS_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>: 64 bytes on 64-bit processes, 48 on x86.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary><c>IO_COUNTERS</c>: 48 bytes everywhere.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>: 144 bytes on 64-bit processes, 112 on x86.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

#if NET
    [LibraryImport("kernel32", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcess(
        string? applicationName,
        IntPtr commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        string? environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        IntPtr securityAttributes);

    [LibraryImport("kernel32", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateFileHandle(
        IntPtr sourceProcess,
        SafeFileHandle source,
        IntPtr targetProcess,
        out IntPtr target,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernel32", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateProcessHandle(
        IntPtr sourceProcess,
        SafeProcessHandle source,
        IntPtr targetProcess,
        out SafeWaitHandle target,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernel32", EntryPoint = "GetStdHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial IntPtr GetStdHandle(int standardHandle);

    [LibraryImport("kernel32", EntryPoint = "CloseHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32", EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeAttributeList(IntPtr list, int attributeCount, uint flags, ref IntPtr size);

    [LibraryImport("kernel32", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateAttribute(
        IntPtr list,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [LibraryImport("kernel32", EntryPoint = "DeleteProcThreadAttributeList")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial void DeleteAttributeList(IntPtr list);

    [LibraryImport("kernel32", EntryPoint = "ResumeThread", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint ResumeThread(SafeThreadHandle thread);

    [LibraryImport("kernel32", EntryPoint = "TerminateProcess", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("kernel32", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial SafeJobHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(
        SafeJobHandle job, int informationClass, ref JobExtendedLimitInformation information, uint length);

    [LibraryImport("kernel32", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
#else
    [DllImport("kernel32", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string? applicationName,
        IntPtr commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        string? environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32", EntryPoint = "CreateNamedPipeW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        IntPtr securityAttributes);

    [DllImport("kernel32", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateFileHandle(
        IntPtr sourceProcess,
        SafeFileHandle source,
        IntPtr targetProcess,
        out IntPtr target,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateProcessHandle(
        IntPtr sourceProcess,
        SafeProcessHandle source,
        IntPtr targetProcess,
        out SafeWaitHandle target,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32", EntryPoint = "GetStdHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32", EntryPoint = "CloseHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32", EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeAttributeList(IntPtr list, int attributeCount, uint flags, ref IntPtr size);

    [DllImport("kernel32", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateAttribute(
        IntPtr list,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32", EntryPoint = "DeleteProcThreadAttributeList")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern void DeleteAttributeList(IntPtr list);

    [DllImport("kernel32", EntryPoint = "ResumeThread", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint ResumeThread(SafeThreadHandle thread);

    [DllImport("kernel32", EntryPoint = "TerminateProcess", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern SafeJobHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        SafeJobHandle job, int informationClass, ref JobExtendedLimitInformation information, uint length);

    [DllImport("kernel32", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
#endif
}
