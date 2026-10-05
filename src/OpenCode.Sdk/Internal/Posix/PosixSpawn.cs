using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using OpenCode.Sdk.Internal.Posix.Abstractions;
using static OpenCode.Sdk.Internal.Posix.PosixInterop;

namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// The shipped <see cref="IPosixSpawn"/>: <c>posix_spawnp</c> with the session flag, default signal
/// state, and one file action per standard descriptor. A pipe route takes the BCL's anonymous
/// pipe, both ends close-on-exec as the runtime creates them, so no other child of this host
/// inherits either end; only the child's end crosses, onto its descriptor through the file
/// action's <c>dup2</c>, which clears close-on-exec on the target alone.
/// </summary>
internal sealed class PosixSpawn : IPosixSpawn
{
    /// <summary><c>O_RDONLY</c>, for a <c>/dev/null</c> standard input.</summary>
    private const int ReadOnly = 0;

    /// <summary><c>O_WRONLY</c>, for a <c>/dev/null</c> standard output or error.</summary>
    private const int WriteOnly = 1;

    /// <summary><c>POSIX_SPAWN_SETSIGDEF</c>, the same value on glibc, musl, and Darwin: the default-disposition set applies.</summary>
    private const short ResetSignalDispositions = 0x04;

    /// <summary><c>POSIX_SPAWN_SETSIGMASK</c>, the same value on glibc, musl, and Darwin: the mask set applies.</summary>
    private const short ResetSignalMask = 0x08;

    /// <summary>The opaque <c>sigset_t</c>: 128 bytes on glibc, 4 on Darwin, so one buffer fits both.</summary>
    private const int SignalSetCapacity = 128;

    /// <summary><c>POSIX_SPAWN_SETSID</c> on Linux (glibc 2.26 and later, musl); macOS uses a different value, hence the branch.</summary>
    private const short LinuxNewSession = 0x80;

    /// <summary><c>POSIX_SPAWN_SETSID</c> on macOS, the same on Intel and Apple silicon.</summary>
    private const short MacNewSession = 0x400;

    /// <summary>
    /// The spawn-attribute and file-action objects are opaque and sized by the C library, so both
    /// ride in deliberately oversized pinned buffers the init calls shape; the destroy calls in the
    /// matching finally blocks give them back.
    /// </summary>
    private const int FileActionsCapacity = 256;

    private const int SpawnAttributesCapacity = 1024;

    private const int StandardInputDescriptor = 0;
    private const int StandardOutputDescriptor = 1;
    private const int StandardErrorDescriptor = 2;

    /// <inheritdoc />
    public PosixSpawnedChild Spawn(PosixSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionFlag = NewSessionFlag();
        var arguments = new List<string>(request.Arguments.Count + 1) { request.ExecutablePath };
        arguments.AddRange(request.Arguments);
        var variables = new List<string>(request.Environment.Count);
        variables.AddRange(request.Environment.Select(static entry => entry.Key + "=" + entry.Value));

        var argv = AllocArgumentVector(arguments);
        try
        {
            var envp = AllocArgumentVector(variables);
            try
            {
                return SpawnChild(request, sessionFlag, argv, envp);
            }
            finally
            {
                FreeArgumentVector(envp, variables.Count);
            }
        }
        finally
        {
            FreeArgumentVector(argv, arguments.Count);
        }
    }

    /// <summary>
    /// The session flag differs by kernel, and anything else is refused: without a session the
    /// child would stay in the parent's process group, and there is no <c>fork</c> fallback.
    /// </summary>
    private static short NewSessionFlag()
    {
#if NET
        if (OperatingSystem.IsLinux())
        {
            return LinuxNewSession;
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacNewSession;
        }
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return LinuxNewSession;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return MacNewSession;
        }
#endif
        throw new PlatformNotSupportedException(
            "posix_spawn has no known way to start a process in a new session on this platform.");
    }

    /// <summary>
    /// The spawn proper. The parent's pipe ends pass to the returned child; every child end is
    /// closed in the parent once the spawn returned, on success and failure alike, because a parent
    /// still holding a write end would keep end-of-stream away after the child is gone.
    /// </summary>
    private static PosixSpawnedChild SpawnChild(PosixSpawnRequest request, short sessionFlag, IntPtr argv, IntPtr envp)
    {
        var pipes = new AnonymousPipeServerStream?[3];
        var actions = new byte[FileActionsCapacity];
        var attributes = new byte[SpawnAttributesCapacity];
        var actionsHandle = GCHandle.Alloc(actions, GCHandleType.Pinned);
        var attributesHandle = GCHandle.Alloc(attributes, GCHandleType.Pinned);
        try
        {
            var actionsPtr = actionsHandle.AddrOfPinnedObject();
            var attributesPtr = attributesHandle.AddrOfPinnedObject();
            CheckNativeResult(InitFileActions(actionsPtr));
            try
            {
                CheckNativeResult(InitAttributes(attributesPtr));
                try
                {
                    ConfigureAttributes(attributesPtr, sessionFlag);
                    Route(actionsPtr, StandardInputDescriptor, request.StandardInput, pipes);
                    Route(actionsPtr, StandardOutputDescriptor, request.StandardOutput, pipes);
                    Route(actionsPtr, StandardErrorDescriptor, request.StandardError, pipes);

                    // posix_spawn reports the errno as its return value rather than through the
                    // thread's errno, so the value below — not GetLastWin32Error — is the failure.
                    var spawned = SpawnProcess(out var pid, request.ExecutablePath, actionsPtr, attributesPtr, argv, envp);
                    CloseChildEnds(pipes);
                    if (spawned != 0)
                    {
                        throw new Win32Exception(spawned);
                    }

                    var child = new PosixSpawnedChild
                    {
                        ProcessId = pid,
                        StandardInput = pipes[StandardInputDescriptor],
                        StandardOutput = pipes[StandardOutputDescriptor],
                        StandardError = pipes[StandardErrorDescriptor],
                    };
                    Array.Clear(pipes, 0, pipes.Length);
                    return child;
                }
                finally
                {
                    _ = DestroyAttributes(attributesPtr);
                }
            }
            finally
            {
                _ = DestroyFileActions(actionsPtr);
            }
        }
        finally
        {
            actionsHandle.Free();
            attributesHandle.Free();
            CloseChildEnds(pipes);
            foreach (var pipe in pipes)
            {
                pipe?.Dispose();
            }
        }
    }

    /// <summary>
    /// Adds the file action one descriptor's route asks for. A pipe is stored in the descriptor's
    /// slot before anything can fail, so the spawn's cleanup owns it from its creation on; its
    /// direction follows the descriptor: the parent writes standard input and reads the two outputs.
    /// </summary>
    private static void Route(IntPtr actions, int descriptor, ChildStreamRoute route, AnonymousPipeServerStream?[] pipes)
    {
        switch (route)
        {
            case ChildStreamRoute.Null:
                CheckNativeResult(AddOpenFileAction(
                    actions, descriptor, "/dev/null", descriptor == StandardInputDescriptor ? ReadOnly : WriteOnly, 0));
                break;
            case ChildStreamRoute.Pipe:
                var pipe = new AnonymousPipeServerStream(
                    descriptor == StandardInputDescriptor ? PipeDirection.Out : PipeDirection.In,
                    HandleInheritability.None);
                pipes[descriptor] = pipe;
                CheckNativeResult(AddDuplicateAction(actions, (int)pipe.ClientSafePipeHandle.DangerousGetHandle(), descriptor));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown child stream route.");
        }
    }

    /// <summary>
    /// Closes the parent's copy of every child end. Disposing the pipe alone would not: once its
    /// client handle was read for the file action, the pipe leaves that handle to its reader.
    /// </summary>
    private static void CloseChildEnds(AnonymousPipeServerStream?[] pipes)
    {
        foreach (var pipe in pipes)
        {
            pipe?.DisposeLocalCopyOfClientHandle();
        }
    }

    /// <summary>
    /// The session flag, plus the signal state libuv gives a Node child: every signal back at its
    /// default disposition and an empty mask. <c>posix_spawn</c> alone resets only handled signals
    /// and keeps ignored ones (the .NET runtime ignores <c>SIGPIPE</c>) and the calling thread's
    /// mask. The attribute calls copy the set, so its buffer lives only for these calls; it is
    /// oversized and zeroed, the discipline the attribute and file-action buffers follow.
    /// </summary>
    private static void ConfigureAttributes(IntPtr attributes, short sessionFlag)
    {
        CheckNativeResult(SetAttributeFlags(attributes, (short)(sessionFlag | ResetSignalDispositions | ResetSignalMask)));
        var set = Marshal.AllocHGlobal(SignalSetCapacity);
        try
        {
            Marshal.Copy(new byte[SignalSetCapacity], 0, set, SignalSetCapacity);
            CheckSignalSetResult(FillSignalSet(set));
            CheckNativeResult(SetDefaultSignals(attributes, set));
            CheckSignalSetResult(EmptySignalSet(set));
            CheckNativeResult(SetSignalMask(attributes, set));
        }
        finally
        {
            Marshal.FreeHGlobal(set);
        }
    }

    /// <summary>The set functions report through errno, unlike the <c>posix_spawn</c> family, which returns it.</summary>
    private static void CheckSignalSetResult(int result)
    {
        if (result != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void CheckNativeResult(int result)
    {
        if (result != 0)
        {
            throw new Win32Exception(result);
        }
    }

    /// <summary>Marshals one UTF-8 argument vector: a null-terminated array of null-terminated byte strings.</summary>
    private static IntPtr AllocArgumentVector(List<string> values)
    {
        var vector = Marshal.AllocHGlobal((values.Count + 1) * IntPtr.Size);
        var completed = false;
        try
        {
            // Zeroed first, so the failure cleanup below frees only what was actually stored.
            for (var index = 0; index <= values.Count; index++)
            {
                Marshal.WriteIntPtr(vector, index * IntPtr.Size, IntPtr.Zero);
            }

            for (var index = 0; index < values.Count; index++)
            {
                var bytes = Encoding.UTF8.GetBytes(values[index]);
                var slot = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, slot, bytes.Length);
                Marshal.WriteByte(slot, bytes.Length, 0);
                Marshal.WriteIntPtr(vector, index * IntPtr.Size, slot);
            }

            Marshal.WriteIntPtr(vector, values.Count * IntPtr.Size, IntPtr.Zero);
            completed = true;
            return vector;
        }
        finally
        {
            if (!completed)
            {
                FreeArgumentVector(vector, values.Count);
            }
        }
    }

    private static void FreeArgumentVector(IntPtr vector, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var slot = Marshal.ReadIntPtr(vector, index * IntPtr.Size);
            if (slot != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(slot);
            }
        }

        Marshal.FreeHGlobal(vector);
    }
}
