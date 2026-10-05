using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.BackgroundService.ProcessControl;

/// <summary>
/// The Windows process API the background-service door binds, in one place; its C library calls
/// live in <see cref="Posix.PosixInterop"/>, beside the rest of the SDK's. The modern targets take
/// <c>LibraryImport</c>, the compile-time stub .NET recommends (its generator is what needs the
/// project's unsafe-code switch); the <c>netstandard2.0</c> asset, where that generator is
/// unavailable, takes the equivalent <c>DllImport</c>.
/// </summary>
internal static partial class BackgroundServiceInterop
{
    /// <summary>Gets a value indicating whether the process runs on Windows, on every target framework.</summary>
    public static bool IsWindows =>
#if NET
        OperatingSystem.IsWindows();
#else
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif

    /// <summary>The Windows process API.</summary>
    internal static partial class Kernel32
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            public uint Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

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

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            public StartupInfo Startup;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr ProcessThread;
            public uint ProcessId;
            public uint ThreadId;
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
        internal static partial IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            ref SecurityAttributes attributes,
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

        [LibraryImport("kernel32", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
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
        internal static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            ref SecurityAttributes attributes,
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

        [DllImport("kernel32", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
#endif
    }
}
