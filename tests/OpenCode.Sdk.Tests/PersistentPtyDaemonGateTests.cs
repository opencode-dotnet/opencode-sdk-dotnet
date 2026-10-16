using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests;

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
    /// The expectation is spelled per platform rather than re-derived from the production
    /// expression: mirroring the formula would pass whatever the formula became. The pinned
    /// upstream ships darwin and linux daemon packages only, so Windows is the daemon-absent leg
    /// and every other platform is the daemon-present one.
    /// </summary>
    [Test]
    public async Task Resolve_Should_Return_The_Platform_Default_When_The_Override_Is_Unset()
    {
        var result = PersistentPtyDaemonGate.Resolve(static _ => null);

        if (OperatingSystem.IsWindows())
        {
            await Assert.That(result).IsFalse();
            return;
        }

        await Assert.That(result).IsTrue();
    }
}
