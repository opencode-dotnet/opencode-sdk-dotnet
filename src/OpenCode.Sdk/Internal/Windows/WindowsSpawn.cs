using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// The shipped <see cref="IWindowsSpawn"/>: <c>CreateProcessW</c> with an explicit inherited-handle
/// list. Every handle the SDK creates for a child is non-inheritable. Under one lock that every SDK
/// spawn on Windows takes (the launcher, the background-service contender, and the tree kill), the
/// spawn duplicates each handle the child receives as inheritable, lists exactly those duplicates,
/// creates the child, and closes the duplicates again; the parent then closes its own copies of the
/// child's ends, so only the child holds them. The child therefore receives its own standard
/// handles and nothing else the host holds, whoever created it. The other direction stays open on
/// every runtime: a <c>Process.Start</c> on another thread passes every inheritable handle of the
/// host under the runtime's own lock, which this spawn does not take, so it can inherit a duplicate
/// while the child is being created. That lock is private; reaching it through reflection would
/// break with any runtime change and under trimming. Every child is created with <c>SW_HIDE</c>, and
/// with <c>CREATE_NO_WINDOW</c> unless a standard handle is the host's own, libuv's rule for a
/// hidden child: a child that shares the host's stderr shares its console too.
/// </summary>
internal sealed class WindowsSpawn : IWindowsSpawn
{
    /// <summary>The one lock every Windows spawn of the SDK holds while an inheritable copy of a child's handle exists.</summary>
    private static readonly Lock SpawnGate = new();

    /// <inheritdoc />
    public WindowsSpawnedChild Spawn(WindowsSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var environment = request.Environment is null ? null : WindowsEnvironmentBlock.Build(request.Environment);

        // Disposed on every path: the child holds its own copies of its ends once it exists, and a
        // parent end not handed over below belongs to no child.
        using var streams = ChildStreamSet.Open(request.StandardStreams);
        var created = Create(request, environment, streams);
        return created with
        {
            StandardInput = streams.Input?.TakeParent(),
            StandardOutput = streams.Output?.TakeParent(),
            StandardError = streams.Error?.TakeParent(),
        };
    }

    private static WindowsSpawnedChild Create(WindowsSpawnRequest request, string? environment, ChildStreamSet streams)
    {
        // The native call may write into its command line, so it crosses as a writable copy.
        var commandLine = Marshal.StringToHGlobalUni(request.CommandLine);
        try
        {
            lock (SpawnGate)
            {
                var duplicates = new List<SafeFileHandle>(3);
                try
                {
                    var listed = new List<IntPtr>(3);
                    var startup = new WindowsInterop.StartupInfoEx
                    {
                        Startup = new WindowsInterop.StartupInfo
                        {
                            StructureSize = (uint)Marshal.SizeOf<WindowsInterop.StartupInfo>(),
                            Flags = WindowsInterop.UseShowWindow,
                            ShowWindow = WindowsInterop.HideWindow,
                        },
                    };
                    if (request.StandardStreams is not null)
                    {
                        startup.Startup.StandardInput = Inheritable(streams.Input, duplicates, listed);
                        startup.Startup.StandardOutput = Inheritable(streams.Output, duplicates, listed);
                        startup.Startup.StandardError = Inheritable(streams.Error, duplicates, listed);
                    }

                    using var list = listed.Count == 0 ? null : InheritedHandleList.Create(listed);
                    var flags = CreationFlags(request);
                    if (list is not null)
                    {
                        // Standard handles are passed only together with inheritance, which the
                        // call requires of them. With nothing listed, every stream is the host's
                        // own and the host has none, so the child has none either way.
                        startup.Startup.Flags |= WindowsInterop.UseStandardHandles;
                        startup.Startup.StructureSize = (uint)Marshal.SizeOf<WindowsInterop.StartupInfoEx>();
                        startup.AttributeList = list.Value;
                        flags |= WindowsInterop.ExtendedStartupInfoPresent;
                    }

                    return Launch(request, commandLine, environment, flags, list is not null, ref startup);
                }
                finally
                {
                    foreach (var duplicate in duplicates)
                    {
                        duplicate.Dispose();
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
        }
    }

    /// <summary>
    /// An inheritable duplicate of the handle one stream's child side holds, recorded for the list
    /// and for its closing; zero when the child receives no handle for the stream. The duplicate is
    /// wrapped in the same <c>finally</c> block that creates it, so a thread abort on .NET Framework
    /// cannot leave an inheritable handle unowned.
    /// </summary>
    private static IntPtr Inheritable(ChildStreamEnd? end, List<SafeFileHandle> duplicates, List<IntPtr> listed)
    {
        if (end?.Child is not { } child)
        {
            return IntPtr.Zero;
        }

        bool duplicated;
        int lastError;
        IntPtr duplicate;
        try
        {
            // Nothing runs here: the work is in the finally block, which a thread abort waits for.
        }
        finally
        {
            duplicated = WindowsInterop.DuplicateFileHandle(
                WindowsInterop.CurrentProcess,
                child,
                WindowsInterop.CurrentProcess,
                out duplicate,
                0,
                inheritHandle: true,
                WindowsInterop.DuplicateSameAccess);
            lastError = Marshal.GetLastWin32Error();
            if (duplicated)
            {
                duplicates.Add(new SafeFileHandle(duplicate, ownsHandle: true));
            }
        }

        if (!duplicated)
        {
            throw new Win32Exception(lastError);
        }

        listed.Add(duplicate);
        return duplicate;
    }

    private static uint CreationFlags(WindowsSpawnRequest request)
    {
        var flags = WindowsInterop.CreateUnicodeEnvironment;
        if (request.StartSuspended)
        {
            flags |= WindowsInterop.CreateSuspended;
        }

        if (request.Detached)
        {
            flags |= WindowsInterop.DetachedProcess | WindowsInterop.CreateNewProcessGroup;
        }

        if (request.StandardStreams is not { InheritsAny: true })
        {
            flags |= WindowsInterop.CreateNoWindow;
        }

        return flags;
    }

    /// <summary>
    /// The call itself. It runs inside a <c>finally</c> block together with the wrapping of the
    /// handles it returns, because on .NET Framework a thread abort can land between any two
    /// statements but waits for a <c>finally</c> block to end: a process created here is always
    /// owned by a safe handle, which the caller's cleanup reaches.
    /// </summary>
    private static WindowsSpawnedChild Launch(
        WindowsSpawnRequest request,
        IntPtr commandLine,
        string? environment,
        uint flags,
        bool inheritHandles,
        ref WindowsInterop.StartupInfoEx startup)
    {
        bool created;
        int lastError;
        WindowsInterop.ProcessInformation information;
        SafeProcessHandle? process = null;
        SafeThreadHandle? thread = null;
        try
        {
            // Nothing runs here: the work is in the finally block, which a thread abort waits for.
        }
        finally
        {
            created = WindowsInterop.CreateProcess(
                request.ApplicationPath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles,
                flags,
                environment,
                request.WorkingDirectory,
                ref startup,
                out information);
            lastError = Marshal.GetLastWin32Error();
            if (created)
            {
                process = new SafeProcessHandle(information.Process, ownsHandle: true);
                thread = new SafeThreadHandle(information.Thread);
            }
        }

        if (!created || process is null || thread is null)
        {
            throw new Win32Exception(lastError);
        }

        if (!request.StartSuspended)
        {
            // A running child's main thread is never needed again.
            thread.Dispose();
            thread = null;
        }

        return new WindowsSpawnedChild { ProcessId = (int)information.ProcessId, Process = process, MainThread = thread };
    }
}
