using System.ComponentModel;

namespace OpenCode.Sdk.Internal.Posix.Abstractions;

/// <summary>
/// The POSIX spawn seam: <c>posix_spawnp</c> with the child state Node's runtime (libuv) gives a
/// detached child. The child leads a new session, so it is in no process group or controlling
/// terminal of the parent's; every signal is back at its default disposition, which
/// <c>posix_spawn</c> alone does not do for signals the parent ignores (the .NET runtime ignores
/// <c>SIGPIPE</c>); and its signal mask is empty. Each standard descriptor leads where the request
/// routes it. There is no <c>fork</c> fallback: a platform that cannot start a process in a new
/// session through <c>posix_spawn</c> is refused.
/// </summary>
internal interface IPosixSpawn
{
    /// <summary>Starts one child.</summary>
    /// <param name="request">What to spawn and where its descriptors lead.</param>
    /// <returns>The child's pid and the parent's pipe ends.</returns>
    /// <exception cref="PlatformNotSupportedException">The platform has no known way to start a process in a new session; nothing was started.</exception>
    /// <exception cref="Win32Exception">A C library call failed, the spawn itself included (a missing executable is <c>ENOENT</c>); its error code is the errno, and nothing was started.</exception>
    public PosixSpawnedChild Spawn(PosixSpawnRequest request);
}
