using System.Runtime.InteropServices;

namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// Every C library function the SDK binds, in one place: the spawn, signal-set, signal, and wait
/// calls that Linux and macOS share, plus the two spawn file actions only one of them has. The modern targets take <c>LibraryImport</c>, the
/// compile-time stub .NET recommends (its generator is what needs the project's unsafe-code
/// switch); the <c>netstandard2.0</c> asset, where that generator is unavailable, takes the
/// equivalent <c>DllImport</c>. "libc" is the portable spelling: <c>libSystem.Native</c>'s loader
/// maps it to the platform's own C library (<c>libc.so.6</c>, <c>/usr/lib/libc.dylib</c>) instead of
/// probing for a file, under CoreCLR and native AOT alike. Every binding has a fixed signature and
/// none is variadic: on Apple arm64 a variadic argument travels on the stack while a fixed-signature
/// stub passes it in a register, so a variadic function (<c>fcntl</c>, <c>ioctl</c>, <c>open</c>)
/// bound this way would read garbage there.
/// </summary>
internal static partial class PosixInterop
{
#if NET
    [LibraryImport("libc", EntryPoint = "sigfillset", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int FillSignalSet(IntPtr set);

    [LibraryImport("libc", EntryPoint = "sigemptyset", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int EmptySignalSet(IntPtr set);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setsigdefault")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int SetDefaultSignals(IntPtr attributes, IntPtr set);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setsigmask")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int SetSignalMask(IntPtr attributes, IntPtr set);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_init")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int InitAttributes(IntPtr attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int SetAttributeFlags(IntPtr attributes, short flags);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int DestroyAttributes(IntPtr attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int InitFileActions(IntPtr actions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addopen", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int AddOpenFileAction(IntPtr actions, int descriptor, string path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int AddDuplicateAction(IntPtr actions, int descriptor, int newDescriptor);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int DestroyFileActions(IntPtr actions);

    [LibraryImport("libc", EntryPoint = "posix_spawnp", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int SpawnProcess(
        out int pid,
        string file,
        IntPtr fileActions,
        IntPtr attributes,
        IntPtr argv,
        IntPtr envp);

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int WaitPid(int pid, out int status, int options);

    /// <summary>
    /// <c>kill(2)</c>. The errno is kept, because the launcher tells a target that is gone
    /// (<c>ESRCH</c>) from one it may not signal; keeping it makes the generator emit a small stub
    /// rather than a plain forwarder.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int Kill(int processId, int signal);

    /// <summary>
    /// <c>waitid(2)</c>. The id type and the options are plain integers on both kernels, and the
    /// <c>siginfo_t</c> it fills is a caller buffer the launcher never parses.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "waitid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int WaitId(int idType, int id, IntPtr info, int options);

    /// <summary>
    /// <c>posix_spawn_file_actions_addchdir_np</c>: glibc 2.29 and later, musl, and macOS 10.15 and
    /// later. An older C library lacks the export, and the call then throws
    /// <see cref="EntryPointNotFoundException"/>.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int AddChangeDirectoryAction(IntPtr actions, string path);

    /// <summary><c>posix_spawn_file_actions_addinherit_np</c>, macOS only: keeps one descriptor open across a close-on-exec-by-default spawn.</summary>
    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addinherit_np")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int AddInheritAction(IntPtr actions, int descriptor);

    /// <summary><c>sigaction(2)</c>, called with a null new action only, to read a disposition.</summary>
    [LibraryImport("libc", EntryPoint = "sigaction", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int QuerySignalAction(int signal, IntPtr action, IntPtr previousAction);
#else
    [DllImport("libc", EntryPoint = "sigfillset", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int FillSignalSet(IntPtr set);

    [DllImport("libc", EntryPoint = "sigemptyset", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int EmptySignalSet(IntPtr set);

    [DllImport("libc", EntryPoint = "posix_spawnattr_setsigdefault")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int SetDefaultSignals(IntPtr attributes, IntPtr set);

    [DllImport("libc", EntryPoint = "posix_spawnattr_setsigmask")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int SetSignalMask(IntPtr attributes, IntPtr set);

    [DllImport("libc", EntryPoint = "posix_spawnattr_init")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int InitAttributes(IntPtr attributes);

    [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int SetAttributeFlags(IntPtr attributes, short flags);

    [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int DestroyAttributes(IntPtr attributes);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int InitFileActions(IntPtr actions);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addopen", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int AddOpenFileAction(IntPtr actions, int descriptor, [MarshalAs(UnmanagedType.LPStr)] string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int AddDuplicateAction(IntPtr actions, int descriptor, int newDescriptor);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int DestroyFileActions(IntPtr actions);

    [DllImport("libc", EntryPoint = "posix_spawnp", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int SpawnProcess(
        out int pid,
        [MarshalAs(UnmanagedType.LPStr)] string file,
        IntPtr fileActions,
        IntPtr attributes,
        IntPtr argv,
        IntPtr envp);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int WaitPid(int pid, out int status, int options);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int Kill(int processId, int signal);

    [DllImport("libc", EntryPoint = "waitid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int WaitId(int idType, int id, IntPtr info, int options);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int AddChangeDirectoryAction(IntPtr actions, [MarshalAs(UnmanagedType.LPStr)] string path);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addinherit_np")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int AddInheritAction(IntPtr actions, int descriptor);

    [DllImport("libc", EntryPoint = "sigaction", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int QuerySignalAction(int signal, IntPtr action, IntPtr previousAction);
#endif
}
