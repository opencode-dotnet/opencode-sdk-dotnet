using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using static OpenCode.Sdk.Internal.BackgroundService.ProcessControl.BackgroundServiceInterop.Kernel32;

namespace OpenCode.Sdk.Internal.BackgroundService.Contender;

/// <summary>
/// The Windows arm: <c>CreateProcessW</c> with the creation flags and show state libuv's
/// <c>uv_spawn</c> gives a child Node spawns <c>detached</c> and <c>windowsHide</c> with no
/// standard stream inherited (<c>DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW</c>,
/// <c>STARTF_USESHOWWINDOW</c> with <c>SW_HIDE</c>), NUL stdin and stdout, the stderr pipe, and an
/// explicit inherited-handle list.
/// </summary>
internal sealed partial class ServiceContenderSpawner
{
    /// <summary><c>DETACHED_PROCESS</c>: the contender gets no console, so the console control events of the console the host runs in never reach it.</summary>
    private const uint DetachedProcess = 0x00000008;

    /// <summary><c>CREATE_NEW_PROCESS_GROUP</c>: the contender roots its own process group and starts with Ctrl+C ignored, an attribute its own children inherit.</summary>
    private const uint NewProcessGroup = 0x00000200;

    /// <summary>
    /// <c>CREATE_NO_WINDOW</c>: no window for a console of the contender's own. Windows ignores it
    /// beside <c>DETACHED_PROCESS</c>, which already leaves the contender without a console;
    /// libuv sets it for a hidden spawn whenever no standard stream is inherited, which is this
    /// spawn's shape, so the flag word is the one a hidden detached Node child gets.
    /// </summary>
    private const uint NoWindow = 0x08000000;

    /// <summary><c>CREATE_UNICODE_ENVIRONMENT</c>: the environment block below is UTF-16.</summary>
    private const uint UnicodeEnvironment = 0x00000400;

    /// <summary><c>EXTENDED_STARTUPINFO_PRESENT</c>: the startup info carries the attribute list.</summary>
    private const uint ExtendedStartupInfo = 0x00080000;

    /// <summary><c>STARTF_USESTDHANDLES</c>: the three standard handles come from the startup info.</summary>
    private const uint UseStandardHandles = 0x00000100;

    /// <summary><c>STARTF_USESHOWWINDOW</c>: the first window the contender shows takes the startup info's show state.</summary>
    private const uint UseShowWindow = 0x00000001;

    /// <summary><c>SW_HIDE</c>: that window starts hidden, so a contender that opens one never flashes it on the desktop.</summary>
    private const short HideWindow = 0;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    /// <summary><c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>: only these handles cross into the child.</summary>
    private static readonly IntPtr HandleListAttribute = new(0x20002);

    /// <summary><c>PIPE_ACCESS_INBOUND</c>: the parent end only reads.</summary>
    private const uint PipeAccessInbound = 0x00000001;

    /// <summary><c>FILE_FLAG_FIRST_PIPE_INSTANCE</c>: creation fails if the name already exists, so no other process can have staged it.</summary>
    private const uint FirstPipeInstance = 0x00080000;

    /// <summary><c>FILE_FLAG_OVERLAPPED</c>: reads on the parent end complete on the I/O completion port.</summary>
    private const uint OverlappedIo = 0x40000000;

    /// <summary><c>PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS</c>.</summary>
    private const uint LocalBytePipe = 0x00000008;

    /// <summary>The buffer an anonymous pipe gets by default, kept for the stderr pipe.</summary>
    private const uint PipeBufferSize = 4096;

    /// <summary>The default wait <c>CreatePipe</c> gives its own named pipe.</summary>
    private const uint PipeDefaultTimeoutMilliseconds = 120_000;

    /// <summary><c>INVALID_HANDLE_VALUE</c>, what <c>CreateFileW</c> returns on failure.</summary>
    private static readonly IntPtr InvalidHandle = new(-1);

    private static ServiceContender SpawnWindows(
        IServiceContenderSpawner.ContenderStartInfo startInfo,
        Dictionary<string, string> environment,
        string? redaction)
    {
        // The native call may scribble over its buffer, so the line crosses as pinned UTF-16 rather
        // than a string the marshaller would have to copy back.
        var commandLine = Marshal.StringToHGlobalUni(WindowsCommandLine(startInfo));
        try
        {
            return SpawnWindowsChild(startInfo, commandLine, WindowsEnvironmentBlock.Build(environment), redaction);
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
        }
    }

    /// <summary>
    /// The launcher's two Windows spellings (<c>OpenCodeServer.ConfigureCommandLine</c>): a batch
    /// shim runs through the system cmd.exe on the <see cref="BatchCommandLine"/> line, whose
    /// outer quote pair is what <c>/s</c> strips, and whose metacharacter refusal throws before
    /// anything spawns; anything else is its argv through the MSVCRT quoting, so a path with
    /// spaces survives the round trip the way <c>ArgumentList</c> survives it.
    /// </summary>
    private static string WindowsCommandLine(IServiceContenderSpawner.ContenderStartInfo startInfo)
    {
        var executable = startInfo.Executable;
        if (executable.IsBatchScript)
        {
            return "\"" + BatchCommandLine.InterpreterPath + "\" " +
                BatchCommandLine.Compose(executable.Path, startInfo.Arguments, launcherArguments: []);
        }

        return ProcessArgumentComposer.Compose([executable.Path, .. startInfo.Arguments]);
    }

    /// <summary>
    /// The Windows spawn. Only the stderr pipe's write end is inheritable, and it crosses alone
    /// beside NUL in the explicit handle list, so no later child of this host inherits the read
    /// end (<see cref="CreateStderrPipe"/>). NUL and the write end stay raw handles until the call
    /// returns, because the attribute list needs raw values; the finally below closes whatever is
    /// still owned here.
    /// </summary>
    private static ServiceContender SpawnWindowsChild(
        IServiceContenderSpawner.ContenderStartInfo startInfo,
        IntPtr commandLine,
        string environmentBlock,
        string? redaction)
    {
        var security = new SecurityAttributes
        {
            Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
            SecurityDescriptor = IntPtr.Zero,
            InheritHandle = 1,
        };

        var nul = IntPtr.Zero;
        var stderrWriteEnd = IntPtr.Zero;
        NamedPipeServerStream? pipe = null;
        SafeProcessHandle? process = null;
        try
        {
            try
            {
                pipe = CreateStderrPipe(ref security, out stderrWriteEnd);
            }
            catch (Win32Exception failure)
            {
                throw SpawnFailure(startInfo, failure);
            }

            nul = CreateFile(
                "NUL", GenericRead | GenericWrite, ShareReadWrite, ref security, OpenExisting, 0, IntPtr.Zero);
            if (nul == IntPtr.Zero || nul == InvalidHandle)
            {
                var error = Marshal.GetLastWin32Error();
                nul = IntPtr.Zero;
                throw SpawnFailure(startInfo, new Win32Exception(error));
            }

            var inherited = new[] { nul, stderrWriteEnd };
            var attributes = CreateHandleAttributeList(startInfo, inherited, out var pin);
            try
            {
                var info = LaunchWindowsChild(startInfo, commandLine, environmentBlock, inherited, attributes);

                // The thread handle is never needed again; the process handle becomes the
                // contender's exit poll.
                using (new SafeProcessHandle(info.ProcessThread, ownsHandle: true))
                {
                    process = new SafeProcessHandle(info.Process, ownsHandle: true);
                }

                // The child holds its own copy now; keeping ours would keep EOF away after every
                // writer is gone.
                _ = CloseHandle(stderrWriteEnd);
                stderrWriteEnd = IntPtr.Zero;
                var contender = new ServiceContender((int)info.ProcessId, pipe, process, redaction);
                pipe = null;
                process = null;
                return contender;
            }
            finally
            {
                pin.Free();
                DeleteAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }
        finally
        {
            if (nul != IntPtr.Zero)
            {
                _ = CloseHandle(nul);
            }

            if (stderrWriteEnd != IntPtr.Zero)
            {
                _ = CloseHandle(stderrWriteEnd);
            }

            pipe?.Dispose();
            process?.Dispose();
        }
    }

    /// <summary>
    /// The stderr pipe, made the way .NET 11's <c>Process</c> makes its output pipes
    /// (dotnet/runtime#125643) and libuv its child stdio: a local named pipe whose read end is
    /// overlapped and whose write end is synchronous and inheritable. An anonymous pipe is always
    /// synchronous on Windows, so each pending read would hold a pool thread for as long as the
    /// contender keeps stderr open — for the elected service, its whole life. The overlapped read
    /// waits on the completion port instead. The name is fresh and the flags refuse a second
    /// instance and remote clients, so only the write end opened here can connect.
    /// </summary>
    /// <exception cref="Win32Exception">The pipe or its write end could not be created.</exception>
    internal static NamedPipeServerStream CreateStderrPipe(ref SecurityAttributes inheritable, out IntPtr writeEnd)
    {
        var name = @"\\.\pipe\LOCAL\opencode-sdk-contender-" + Guid.NewGuid().ToString("N");
        writeEnd = IntPtr.Zero;
        SafePipeHandle? readEnd = null;
        var ownedWriteEnd = IntPtr.Zero;
        try
        {
            readEnd = CreateNamedPipe(
                name,
                PipeAccessInbound | FirstPipeInstance | OverlappedIo,
                LocalBytePipe,
                maxInstances: 1,
                PipeBufferSize,
                PipeBufferSize,
                PipeDefaultTimeoutMilliseconds,
                securityAttributes: IntPtr.Zero);
            if (readEnd.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            ownedWriteEnd = CreateFile(name, GenericWrite, 0, ref inheritable, OpenExisting, 0, IntPtr.Zero);
            if (ownedWriteEnd == IntPtr.Zero || ownedWriteEnd == InvalidHandle)
            {
                var error = Marshal.GetLastWin32Error();
                ownedWriteEnd = IntPtr.Zero;
                throw new Win32Exception(error);
            }

            var stream = new NamedPipeServerStream(PipeDirection.In, isAsync: true, isConnected: true, readEnd);
            readEnd = null;
            writeEnd = ownedWriteEnd;
            ownedWriteEnd = IntPtr.Zero;
            return stream;
        }
        finally
        {
            if (ownedWriteEnd != IntPtr.Zero)
            {
                _ = CloseHandle(ownedWriteEnd);
            }

            readEnd?.Dispose();
        }
    }

    private static ProcessInformation LaunchWindowsChild(
        IServiceContenderSpawner.ContenderStartInfo startInfo,
        IntPtr commandLine,
        string environmentBlock,
        IntPtr[] inherited,
        IntPtr attributes)
    {
        var startup = new StartupInfoEx
        {
            Startup = new StartupInfo
            {
                StructureSize = (uint)Marshal.SizeOf<StartupInfoEx>(),
                Flags = UseStandardHandles | UseShowWindow,
                ShowWindow = HideWindow,
                StandardInput = inherited[0],
                StandardOutput = inherited[0],
                StandardError = inherited[1],
            },
            AttributeList = attributes,
        };
        if (!CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,
                DetachedProcess | NewProcessGroup | NoWindow | UnicodeEnvironment | ExtendedStartupInfo,
                environmentBlock,
                null,
                ref startup,
                out var info))
        {
            throw SpawnFailure(startInfo, new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return info;
    }

    /// <summary>
    /// Sizes, allocates, and fills the explicit inherited-handle list: NUL for stdin/stdout and
    /// the stderr pipe's write end, and nothing else — no unrelated inheritable handle leaks into
    /// the contender the way a blanket <c>bInheritHandles</c> spawn would leak them.
    /// </summary>
    private static IntPtr CreateHandleAttributeList(
        IServiceContenderSpawner.ContenderStartInfo startInfo, IntPtr[] handles, out GCHandle pin)
    {
        var size = IntPtr.Zero;
        _ = InitializeAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero)
        {
            throw SpawnFailure(startInfo, new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var list = Marshal.AllocHGlobal(size);
        GCHandle hold = default;
        var completed = false;
        try
        {
            if (!InitializeAttributeList(list, 1, 0, ref size))
            {
                throw SpawnFailure(startInfo, new Win32Exception(Marshal.GetLastWin32Error()));
            }

            hold = GCHandle.Alloc(handles, GCHandleType.Pinned);
            if (!UpdateAttribute(
                    list,
                    0,
                    HandleListAttribute,
                    hold.AddrOfPinnedObject(),
                    new IntPtr(handles.Length * IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw SpawnFailure(startInfo, new Win32Exception(Marshal.GetLastWin32Error()));
            }

            completed = true;
            pin = hold;
            return list;
        }
        finally
        {
            if (!completed)
            {
                if (hold.IsAllocated)
                {
                    hold.Free();
                }

                Marshal.FreeHGlobal(list);
                pin = default;
            }
        }
    }
}
