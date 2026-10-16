using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The machine lock is what keeps a resource two test hosts must not use at once — the simulated
/// server's port window — from racing across target-framework legs, so its two load-bearing
/// properties - mutual exclusion, and a bounded loud failure rather than an unbounded wait - are
/// pinned here. A real file system with a per-test path is deliberate: the contract is an
/// operating-system file lock, and every assertion below stays hermetic because no test touches a
/// shared production lock path.
/// </summary>
public sealed class MachineLockTests
{
    private static readonly RealFileSystem FileSystem = new();

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(1);

    /// <summary>A private lock file per test, deleted once its holders released it, so no run leaves one in the temp directory.</summary>
    private static string LockPath() =>
        FileSystem.Path.Combine(FileSystem.Path.GetTempPath(), "opencode-sdk-tests-lock-" + Guid.NewGuid().ToString("N"));

    [Test]
    public async Task AcquireAtAsync_Should_Refuse_A_Second_Holder_Within_Its_Bound()
    {
        var path = LockPath();
        try
        {
            using var first = await MachineLock.AcquireAtAsync(FileSystem, path, ShortTimeout);

            _ = await Assert.That(async () => await MachineLock.AcquireAtAsync(FileSystem, path, ShortTimeout))
                .Throws<TimeoutException>();
        }
        finally
        {
            FileSystem.File.Delete(path);
        }
    }

    [Test]
    public async Task AcquireAtAsync_Should_Hand_The_Lock_On_After_The_Holder_Releases()
    {
        var path = LockPath();
        try
        {
            var first = await MachineLock.AcquireAtAsync(FileSystem, path, ShortTimeout);
            first.Dispose();

            using var second = await MachineLock.AcquireAtAsync(FileSystem, path, ShortTimeout);

            await Assert.That(FileSystem.File.Exists(path)).IsTrue();
        }
        finally
        {
            FileSystem.File.Delete(path);
        }
    }
}
