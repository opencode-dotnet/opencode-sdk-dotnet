namespace OpenCode.Sdk.Tests.Harness;

public sealed class PersistentPtyDaemonGateTests
{
    [Test]
    public async Task Resolve_Should_Return_True_When_The_Override_Is_One()
    {
        var result = PersistentPtyDaemonGate.Resolve(static _ => "1");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task Resolve_Should_Return_False_When_The_Override_Is_Zero()
    {
        var result = PersistentPtyDaemonGate.Resolve(static _ => "0");

        await Assert.That(result).IsFalse();
    }

    /// <summary>
    /// From the pinned 2.1.x upstream on, the daemon package ships a win32 binary as well as darwin
    /// and linux ones, so an unset override expects the daemon on every platform, Windows included.
    /// </summary>
    [Test]
    public async Task Resolve_Should_Expect_The_Daemon_When_The_Override_Is_Unset()
    {
        var result = PersistentPtyDaemonGate.Resolve(static _ => null);

        await Assert.That(result).IsTrue();
    }
}
