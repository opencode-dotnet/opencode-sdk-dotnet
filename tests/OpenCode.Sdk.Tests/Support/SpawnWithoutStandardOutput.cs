using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Posix.Abstractions;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The shipped spawn of either platform, with the read end of the child's stdout closed and
/// withheld from the caller, so the launcher cannot adopt the child it just started. Records the
/// pid of each child it starts; on Windows it also holds a handle of its own on that child, so the
/// pid names the same process for as long as the test looks at it.
/// </summary>
internal sealed class SpawnWithoutStandardOutput : IPosixSpawn, IWindowsSpawn, IDisposable
{
    private readonly PosixSpawn _posixSpawn = new();
    private readonly WindowsSpawn _windowsSpawn = new();

    /// <summary>Gets the pid of the last child started; zero before the first.</summary>
    public int ProcessId { get; private set; }

    /// <summary>Gets a handle of the test's own on the last Windows child, once one started.</summary>
    public System.Diagnostics.Process? Held { get; private set; }

    public PosixSpawnedChild Spawn(PosixSpawnRequest request)
    {
        var child = _posixSpawn.Spawn(request);
        ProcessId = child.ProcessId;
        child.StandardOutput?.Dispose();
        return child with { StandardOutput = null };
    }

    public WindowsSpawnedChild Spawn(WindowsSpawnRequest request)
    {
        var child = _windowsSpawn.Spawn(request);
        ProcessId = child.ProcessId;
        Held = System.Diagnostics.Process.GetProcessById(child.ProcessId);
        _ = Held.Handle;
        child.StandardOutput?.Dispose();
        return child with { StandardOutput = null };
    }

    public void Dispose() => Held?.Dispose();
}
