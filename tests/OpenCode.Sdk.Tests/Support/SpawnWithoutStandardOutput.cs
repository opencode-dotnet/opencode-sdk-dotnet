using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Posix.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The shipped spawn, with the read end of the child's stdout closed and withheld from the caller,
/// so the launcher cannot adopt the child it just started. Records the pid of each child it starts.
/// </summary>
internal sealed class SpawnWithoutStandardOutput : IPosixSpawn
{
    private readonly PosixSpawn _spawn = new();

    /// <summary>Gets the pid of the last child started; zero before the first.</summary>
    public int ProcessId { get; private set; }

    public PosixSpawnedChild Spawn(PosixSpawnRequest request)
    {
        var child = _spawn.Spawn(request);
        ProcessId = child.ProcessId;
        child.StandardOutput?.Dispose();
        return child with { StandardOutput = null };
    }
}
