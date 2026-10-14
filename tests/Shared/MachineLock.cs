using System.Globalization;
using System.IO.Abstractions;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// A named lock every test host on the machine contends for. The legs of one run start in the
/// same second — one host per target framework, several at a time — and TUnit's parallel keys
/// order tests inside one host only, so a resource two hosts must not use at the same moment
/// needs a lock the operating system holds across processes.
/// A lock file rather than a <see cref="Mutex"/>: a mutex has thread affinity and this is held
/// across awaits, and the operating system releases a file lock when a killed holder's process
/// exits, so a crashed run cannot wedge the next one.
/// </summary>
internal sealed class MachineLock : IDisposable
{
    /// <summary>
    /// The drive manifest's reserve-then-rebind window. <see cref="LoopbackPortReservation"/>
    /// must release a port before the pinned server can bind it, and that server takes seconds to
    /// boot, so two hosts starting simulated servers at the same moment are handed the same
    /// loopback port and the loser dies before readiness with "Failed to start server. Is port N in
    /// use?" (control-server.ts:61) — reproduced on this repository's own four-target-framework run.
    /// Holding the lock from the reservation until the backend control socket is proven bound
    /// closes the window by construction rather than by odds.
    /// </summary>
    public const string DrivePorts = "drive-ports";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly FileSystemStream _handle;

    private MachineLock(FileSystemStream handle) => _handle = handle;

    /// <summary>Takes the named lock every host on the machine contends for.</summary>
    public static Task<MachineLock> AcquireAsync(IFileSystem fileSystem, string name, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var directory = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "opencode-sdk-tests");
        _ = fileSystem.Directory.CreateDirectory(directory);
        return AcquireAtAsync(fileSystem, fileSystem.Path.Combine(directory, name + ".lock"), timeout);
    }

    /// <summary>
    /// Test-only seam: the shared lock paths above are the ones every fixture on the machine
    /// contends for, so a test that proved mutual exclusion against one would stall real fixtures.
    /// Taking the path lets the contract be pinned against a private file instead.
    /// </summary>
    [SlopwatchSuppress(
        "SW004",
        "Test-only cross-process lock: the holder is another process, and no OS API signals another process's file-lock release — polling the lock is the condition wait.")]
    public static async Task<MachineLock> AcquireAtAsync(IFileSystem fileSystem, string path, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var handle = TryOpenExclusively(fileSystem, path);
            if (handle is not null)
            {
                return new MachineLock(handle);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Another test host held the machine lock at '{path}' for more than {timeout.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture)} minutes.");
            }

            // The lock is an operating-system file lock held by another *process*, and no portable
            // API signals its release: a named mutex or semaphore is what the class comment above
            // already rules out, and a watcher never fires because the file is unlocked, not
            // changed. Polling the lock is therefore the condition wait, and this is its interval.
            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// Takes the lock file for exclusive use.
    /// </summary>
    /// <returns>
    /// The held handle, or null when another host holds the lock — which is contention, not
    /// failure, so the caller's bounded poll turns it into either the lock or a loud timeout.
    /// </returns>
    private static FileSystemStream? TryOpenExclusively(IFileSystem fileSystem, string path)
    {
        try
        {
            return fileSystem.FileStream.New(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // A sharing violation: another host holds the lock.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // The same contention surfaces as an access denial on some platforms.
            return null;
        }
    }

    public void Dispose() => _handle.Dispose();
}
